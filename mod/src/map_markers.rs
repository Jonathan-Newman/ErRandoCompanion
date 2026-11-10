//! Targets 1 to 5 as icons on the game's own world map.
//!
//! The map draws one icon for each row of WorldMapPointParam. Once the game has loaded its params, this
//! module copies that table with five extra rows, one per target number, and puts the copy in its place.
//! After that the five rows are only ever edited in place: a row with no target is moved to area 99,
//! which the map never shows. Each row's text is "Target N: <check name>", served by a hook on the
//! game's message lookup for five PlaceName ids of our own. Check names only, never what a check holds.
//!
//! The approach follows Map for Goblins - DLL by VirusAlex (MIT licence), whose notes document the
//! param table layout and, importantly, that the game frees the table itself at shutdown. So the copy
//! is allocated from the same game memory pool as the original; when that pool cannot be found, the
//! markers stay off rather than risk a crash when the game closes.

use std::collections::HashMap;
use std::ffi::c_void;
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::Duration;

use hudhook::mh::{MH_ApplyQueued, MH_Initialize, MhHook, MH_STATUS};
use serde::Deserialize;

use crate::model::Target;
use crate::{log, memory};

const PARAM_LIST_PATTERN: &str = "48 8B 0D ?? ?? ?? ?? 48 85 C9 0F 84 ?? ?? ?? ?? 45 33 C0 BA 90";
/// A small wrapper that looks a name up in several banks; its first call is to the lookup itself.
const LOOKUP_CALLER_PATTERN: &str = "48 89 5C 24 08 57 48 83 EC 20 8B DA 44 8B CA 33 D2 48 8B F9 44 8D 42 6F E8";
/// The call inside the engine's operator delete that asks which memory pool owns a pointer.
const OWNER_LOOKUP_PATTERN: &str = "48 8B 5F 20 48 C7 47 20 00 00 00 00 48 8B 7C 24 38 48 85 DB 75 26 \
    48 8B CE E8 ?? ?? ?? ?? 48 8B D8 48 85 C0 75 16 4C 8D 05 ?? ?? ?? ?? 8D 50 64 48 8D 0D ?? ?? ?? ?? \
    E8 ?? ?? ?? ?? 4C 8B 03 48 8B D6 48 8B CB 41 FF 50 68";

const PARAM_LIST_ENTRIES: usize = 186;
const ROW_SIZE: usize = 256;
const MARKERS: usize = 5;
/// The bank PlaceName text lives in, which map icons read their names from.
const PLACE_NAME_BANK: u32 = 19;
/// PlaceName ids for "Target 1" to "Target 5", far above anything the game or common mods use.
const TEXT_ID_BASE: i32 = 2_105_000_000;
/// An area the map never shows: where a row without a target waits.
const HIDDEN_AREA: u8 = 99;
const TEXT_LENGTH: usize = 160;

/// Where each check is on its map tile, from Data\map_data.json.
#[derive(Deserialize)]
struct MapData {
    pins: Vec<Pin>,
}

#[derive(Deserialize, Clone)]
#[serde(rename_all = "camelCase")]
struct Pin {
    id: String,
    map_id: Option<String>,
    local: Option<Point>,
    #[serde(default)]
    world: Option<Point>,
    /// "base", "underground" or "dlc": which of the game's maps the check is on.
    #[serde(default)]
    map: Option<String>,
}

#[derive(Deserialize, Clone, Copy)]
struct Point {
    x: f32,
    /// A few pins have only a rough position on the map, with no height.
    #[serde(default)]
    y: Option<f32>,
    z: f32,
}

/// The five injected rows, once they exist.
static ROWS: [AtomicUsize; MARKERS] = [const { AtomicUsize::new(0) }; MARKERS];
static PINS: OnceLock<HashMap<String, Pin>> = OnceLock::new();
static ICON: AtomicUsize = AtomicUsize::new(393);
static GAME_ICONS: Mutex<Vec<u16>> = Mutex::new(Vec::new());
/// The targets shown last, so rows are only rewritten when something changed.
static SHOWN: Mutex<Vec<(i32, String)>> = Mutex::new(Vec::new());

/// Text the game reads through our hook. Fixed buffers, so a pointer the game holds never dangles.
struct Texts([[u16; TEXT_LENGTH]; MARKERS]);
static TEXTS: Mutex<Texts> = Mutex::new(Texts([[0; TEXT_LENGTH]; MARKERS]));
/// Raw pointers into TEXTS for the hook, which must not wait on a lock inside the game's text lookup.
static TEXT_POINTERS: OnceLock<[usize; MARKERS]> = OnceLock::new();

type LookupEntry = unsafe extern "system" fn(*mut c_void, u32, u32, i32) -> *const u16;
static LOOKUP_ORIGINAL: OnceLock<LookupEntry> = OnceLock::new();

/// Reads where every check is, for the map markers and the beacon.
pub fn load_pins(directory: &Path) {
    match std::fs::read_to_string(directory.join("Data").join("map_data.json"))
        .map_err(|e| e.to_string())
        .and_then(|text| serde_json::from_str::<MapData>(&text).map_err(|e| e.to_string()))
    {
        Ok(data) => {
            let _ = PINS.set(data.pins.into_iter().map(|p| (p.id.clone(), p)).collect());
        }
        Err(e) => log::write(&format!("Map markers and the beacon are off: could not read map_data.json ({e}).")),
    }
}

/// A check's world position (east, north) if it is on the surface map of the base game, the only
/// map the beacon can go on so far.
pub fn surface_position(id: &str) -> Option<(f32, f32)> {
    let pin = PINS.get()?.get(id)?;
    let world = pin.world?;
    (pin.map.as_deref() == Some("base")).then_some((world.x, world.z))
}

/// A check's world position (east, north) and which of the game's maps shows it.
pub fn map_position(id: &str) -> Option<(f32, f32, crate::map_view::Layer)> {
    use crate::map_view::Layer;
    let pin = PINS.get()?.get(id)?;
    let world = pin.world?;
    let layer = match pin.map.as_deref()? {
        "base" => Layer::Surface,
        "underground" => Layer::Underground,
        "dlc" => Layer::LandOfShadow,
        _ => return None,
    };
    Some((world.x, world.z, layer))
}

pub fn install(icon: u16) {
    ICON.store(icon as usize, Ordering::SeqCst);
    if PINS.get().is_none() {
        return;
    }
    std::thread::spawn(|| {
        if let Err(e) = unsafe { start() } {
            log::write(&format!("Map markers are off: {e}"));
        }
    });
}

unsafe fn start() -> Result<(), String> {
    let param_list = memory::find_global(&memory::pattern(PARAM_LIST_PATTERN), 3, 7).ok_or("the param list was not found")?;
    let lookup = memory::find_global(&memory::pattern(LOOKUP_CALLER_PATTERN), 0x19, 0x1D).ok_or("the text lookup was not found")?;
    let owner_lookup = memory::find_global(&memory::pattern(OWNER_LOOKUP_PATTERN), 0x1A, 0x1E).ok_or("the game's memory pools were not found")?;

    {
        let mut texts = TEXTS.lock().unwrap_or_else(|p| p.into_inner());
        let pointers = std::array::from_fn(|i| texts.0[i].as_mut_ptr() as usize);
        let _ = TEXT_POINTERS.set(pointers);
    }
    match MH_Initialize() {
        MH_STATUS::MH_OK | MH_STATUS::MH_ERROR_ALREADY_INITIALIZED => {}
        status => return Err(format!("MinHook: {status:?}")),
    }
    let hook = MhHook::new(lookup as *mut c_void, lookup_entry as *mut c_void).map_err(|s| format!("text hook: {s:?}"))?;
    let _ = LOOKUP_ORIGINAL.set(std::mem::transmute::<*mut c_void, LookupEntry>(hook.trampoline()));
    hook.queue_enable().map_err(|s| format!("text hook: {s:?}"))?;
    MH_ApplyQueued().ok().map_err(|s| format!("text hook: {s:?}"))?;

    // The game loads its params a few seconds into startup.
    let header = (0..600)
        .find_map(|_| {
            let found = world_map_point_param(param_list);
            if found.is_none() {
                std::thread::sleep(Duration::from_secs(1));
            }
            found
        })
        .ok_or("the game's map icon table never loaded")?;
    inject(header, owner_lookup)?;
    log::write("Map markers are ready.");
    // Targets set before the rows existed.
    let shown = std::mem::take(&mut *SHOWN.lock().unwrap_or_else(|p| p.into_inner()));
    write_rows(&shown);
    Ok(())
}

/// The WorldMapPointParam header once every param has loaded.
fn world_map_point_param(param_list: usize) -> Option<usize> {
    let list = memory::read::<usize>(param_list).filter(|&l| l != 0)?;
    let mut found = None;
    for i in 0..PARAM_LIST_ENTRIES {
        let res_cap = memory::read::<usize>(list + 136 + i * 72).filter(|&r| r != 0)?;
        // The name is a DLWString at +0x18: inline for up to 7 characters, else a pointer.
        let capacity = memory::read::<u64>(res_cap + 0x30)?;
        let length = memory::read::<u64>(res_cap + 0x28)? as usize;
        let text = if capacity > 7 { memory::read::<usize>(res_cap + 0x18)? } else { res_cap + 0x18 };
        if length == 18 && memory::read::<[u16; 18]>(text)? == utf16::<18>("WorldMapPointParam") {
            found = memory::read::<usize>(res_cap + 0x80).filter(|&h| h != 0);
        }
    }
    found.filter(|&header| memory::read::<usize>(header + 0x80).is_some_and(|f| f != 0))
}

fn utf16<const N: usize>(text: &str) -> [u16; N] {
    let mut out = [0u16; N];
    for (slot, unit) in out.iter_mut().zip(text.encode_utf16()) {
        *slot = unit;
    }
    out
}

/// Replaces the table with a copy that has five more rows, all hidden for now.
unsafe fn inject(header: usize, owner_lookup: usize) -> Result<(), String> {
    let file_slot = (header + 0x80) as *mut usize;
    let size_slot = (header + 0x78) as *mut i64;
    let old = *file_slot;
    let old_rows = memory::read::<u16>(old + 0x0A).ok_or("unreadable table")? as usize;
    let type_offset = memory::read::<u64>(old + 0x10).ok_or("unreadable table")? as usize;
    let type_name = std::ffi::CStr::from_ptr((old + type_offset) as *const std::ffi::c_char).to_bytes_with_nul().to_vec();

    // Our rows go after the last id in use, which keeps the copy sorted.
    let mut rows: Vec<(u64, *const u8)> = (0..old_rows)
        .map(|i| {
            let locator = old + 0x40 + i * 24;
            ((locator as *const u64).read(), (old + ((locator + 8) as *const u64).read() as usize) as *const u8)
        })
        .collect();
    let last_id = rows.iter().map(|r| r.0).max().unwrap_or(0);
    // Every icon the game's own map uses, so the icon picker can step between real ones.
    let mut used: Vec<u16> = rows.iter().map(|r| (r.1.add(0x0C) as *const u16).read_unaligned()).filter(|&i| i != 0).collect();
    used.sort_unstable();
    used.dedup();
    log::write(&format!("The game's map uses {} different icons: {used:?}", used.len()));
    *GAME_ICONS.lock().unwrap_or_else(|p| p.into_inner()) = used;
    let template = row_template();
    for n in 0..MARKERS {
        rows.push((last_id + 1 + n as u64, template.as_ptr()));
    }
    rows.sort_by_key(|r| r.0);
    let total = rows.len();

    const WRAPPER: usize = 0x10;
    let data_start = 0x40 + total * 24;
    let type_start = data_start + total * ROW_SIZE;
    let locators_start = (type_start + type_name.len() + 0xF) & !0xF;
    let file_size = locators_start + total * 8;

    let block = game_alloc(owner_lookup, old, WRAPPER + file_size).ok_or(
        "could not get memory from the game's own pool, which it needs to free the table safely at exit",
    )?;
    std::ptr::write_bytes(block, 0, WRAPPER + file_size);
    // The engine's own wrapper: where the id-sorted locators start, and how many there are.
    (block as *mut u32).write(locators_start as u32);
    (block.add(4) as *mut i32).write(total as i32);
    let file = block.add(WRAPPER);
    std::ptr::copy_nonoverlapping(old as *const u8, file, 0x40);
    (file as *mut u32).write(type_start as u32);
    (file.add(0x04) as *mut u16).write(data_start as u16);
    (file.add(0x0A) as *mut u16).write(total as u16);
    (file.add(0x10) as *mut u64).write(type_start as u64);
    (file.add(0x30) as *mut u64).write(data_start as u64);
    std::ptr::copy_nonoverlapping(type_name.as_ptr(), file.add(type_start), type_name.len());

    let mut ours = 0;
    for (i, (id, data)) in rows.iter().enumerate() {
        let offset = data_start + i * ROW_SIZE;
        let locator = file.add(0x40 + i * 24) as *mut u64;
        locator.write(*id);
        locator.add(1).write(offset as u64);
        locator.add(2).write((type_start + type_name.len()) as u64);
        std::ptr::copy_nonoverlapping(*data, file.add(offset), ROW_SIZE);
        let wrapper_locator = file.add(locators_start + i * 8) as *mut i32;
        wrapper_locator.write(*id as i32);
        wrapper_locator.add(1).write(i as i32);
        if *id > last_id {
            ROWS[ours].store(file.add(offset) as usize, Ordering::SeqCst);
            ours += 1;
        }
    }
    *file_slot = file as usize;
    *size_slot = file_size as i64;
    Ok(())
}

/// Memory from the pool that owns `neighbour`, through that pool's own aligned allocate.
unsafe fn game_alloc(owner_lookup: usize, neighbour: usize, size: usize) -> Option<*mut u8> {
    type FindOwner = unsafe extern "system" fn(usize) -> usize;
    type AllocateAligned = unsafe extern "system" fn(usize, usize, usize) -> usize;
    let owner = std::mem::transmute::<usize, FindOwner>(owner_lookup)(neighbour);
    let vtable = memory::read::<usize>(owner).filter(|&v| v != 0)?;
    let allocate = memory::read::<usize>(vtable + 0x50).filter(|&a| a != 0)?;
    let block = std::mem::transmute::<usize, AllocateAligned>(allocate)(owner, size, 16);
    (block != 0 && block % 16 == 0).then_some(block as *mut u8)
}

/// A hidden icon row with every text slot empty.
fn row_template() -> [u8; ROW_SIZE] {
    let mut row = [0u8; ROW_SIZE];
    for slot in 0..8 {
        row[0x30 + slot * 12..0x34 + slot * 12].copy_from_slice(&(-1i32).to_le_bytes());
    }
    for at in [0x98, 0xA8, 0xAC, 0xB0] {
        row[at..at + 4].copy_from_slice(&(-1i32).to_le_bytes()); // distant-view ids: none
    }
    row[0x20] = HIDDEN_AREA;
    row
}

/// Shows the current targets on the map. Cheap when nothing changed.
pub fn update(targets: &[Target]) {
    let wanted: Vec<(i32, String)> = targets.iter().map(|t| (t.number, t.id.clone())).collect();
    let mut shown = SHOWN.lock().unwrap_or_else(|p| p.into_inner());
    if *shown == wanted {
        return;
    }
    // Names go in first, so a row never shows with an old name.
    {
        let mut texts = TEXTS.lock().unwrap_or_else(|p| p.into_inner());
        for target in targets {
            if let Some(slot) = slot_of(target.number) {
                let text = format!("Target {}: {}", target.number, target.name);
                let buffer = &mut texts.0[slot];
                buffer.fill(0);
                for (cell, unit) in buffer.iter_mut().zip(text.encode_utf16().take(TEXT_LENGTH - 1)) {
                    *cell = unit;
                }
            }
        }
    }
    if ROWS[0].load(Ordering::SeqCst) != 0 {
        write_rows(&wanted);
    }
    *shown = wanted;
}

/// The icon ids the game's own map rows use, in order; empty until the markers are ready.
pub fn game_icons() -> Vec<u16> {
    GAME_ICONS.lock().unwrap_or_else(|p| p.into_inner()).clone()
}

/// Changes the icon every target row uses, in place.
pub fn set_icon(icon: u16) {
    ICON.store(icon as usize, Ordering::SeqCst);
    for row in &ROWS {
        let row = row.load(Ordering::SeqCst) as *mut u8;
        if !row.is_null() {
            unsafe { (row.add(0x0C) as *mut u16).write_volatile(icon) };
        }
    }
}

fn slot_of(number: i32) -> Option<usize> {
    (1..=MARKERS as i32).contains(&number).then(|| number as usize - 1)
}

fn write_rows(targets: &[(i32, String)]) {
    let Some(pins) = PINS.get() else { return };
    for slot in 0..MARKERS {
        let row = ROWS[slot].load(Ordering::SeqCst) as *mut u8;
        if row.is_null() {
            continue;
        }
        let place = targets
            .iter()
            .find(|(number, _)| slot_of(*number) == Some(slot))
            .and_then(|(_, id)| pins.get(id))
            .and_then(placement);
        unsafe {
            // Hide first, then fill in, then reveal, so the map never draws a half-written row.
            row.add(0x20).write_volatile(HIDDEN_AREA);
            let Some(place) = place else { continue };
            (row.add(0x0C) as *mut u16).write_volatile(ICON.load(Ordering::SeqCst) as u16);
            (row.add(0x30) as *mut i32).write_volatile(TEXT_ID_BASE + slot as i32 + 1);
            row.add(0x18).write_volatile(place.layer);
            row.add(0x21).write_volatile(place.grid_x);
            row.add(0x22).write_volatile(place.grid_z);
            (row.add(0x24) as *mut f32).write_volatile(place.position.x);
            (row.add(0x28) as *mut f32).write_volatile(place.position.y.unwrap_or(0.0));
            (row.add(0x2C) as *mut f32).write_volatile(place.position.z);
            row.add(0x20).write_volatile(place.area);
        }
    }
}

struct Placement {
    area: u8,
    grid_x: u8,
    grid_z: u8,
    /// dispMask bits: 1 = the surface, 2 = underground, 4 = the Land of Shadow.
    layer: u8,
    position: Point,
}

/// "m60_46_36_00" and a position on that tile, as the map's own rows give them.
fn placement(pin: &Pin) -> Option<Placement> {
    let parts: Vec<u8> = pin.map_id.as_ref()?.trim_start_matches('m').split('_').filter_map(|p| p.parse().ok()).collect();
    let [area, grid_x, grid_z, ..] = parts[..] else { return None };
    let layer = match area {
        12 => 2,
        20 | 21 | 22 | 25 | 28 | 40 | 41 | 42 | 43 | 61 => 4,
        _ => 1,
    };
    Some(Placement { area, grid_x, grid_z, layer, position: pin.local? })
}

unsafe extern "system" fn lookup_entry(repository: *mut c_void, unknown: u32, bank: u32, id: i32) -> *const u16 {
    if bank == PLACE_NAME_BANK && (TEXT_ID_BASE + 1..=TEXT_ID_BASE + MARKERS as i32).contains(&id) {
        if let Some(pointers) = TEXT_POINTERS.get() {
            return pointers[(id - TEXT_ID_BASE - 1) as usize] as *const u16;
        }
    }
    let original = LOOKUP_ORIGINAL.get().expect("text hook without its original");
    original(repository, unknown, bank, id)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn places_pins_on_their_tile_and_layer() {
        let pin = |map: &str| Pin { id: "x".into(), map_id: Some(map.into()), local: Some(Point { x: 1.0, y: Some(2.0), z: 3.0 }), world: None, map: None };
        let surface = placement(&pin("m60_46_36_00")).unwrap();
        assert_eq!((surface.area, surface.grid_x, surface.grid_z, surface.layer), (60, 46, 36, 1));
        assert_eq!(placement(&pin("m12_01_00_00")).unwrap().layer, 2);
        assert_eq!(placement(&pin("m61_48_41_00")).unwrap().layer, 4);
        assert_eq!(placement(&pin("m10_00_00_00")).unwrap().area, 10);
        assert!(placement(&Pin { id: "x".into(), map_id: None, local: None, world: None, map: None }).is_none());
    }

    #[test]
    fn reads_the_real_map_data() {
        let text = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../Data/map_data.json")).unwrap();
        let data: MapData = serde_json::from_str(&text).expect("map_data.json parses");
        assert!(data.pins.len() > 300);
        let patches = data.pins.iter().find(|p| p.id == "merchant_patches_limgrave").unwrap();
        assert!(patches.world.is_some_and(|w| w.y.is_none()), "a pin with no height still loads");
    }

    #[test]
    fn the_template_is_hidden_with_no_text() {
        let row = row_template();
        assert_eq!(row[0x20], HIDDEN_AREA);
        assert_eq!(i32::from_le_bytes(row[0x30..0x34].try_into().unwrap()), -1);
        assert_eq!(i32::from_le_bytes(row[0x84..0x88].try_into().unwrap()), -1);
    }
}
