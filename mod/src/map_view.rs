//! Where a world position appears on screen while the game's world map is open, so the overlay can
//! draw its own target icons exactly over the map.
//!
//! While the map is open the game calls its map "hover" routine every frame with the live map area
//! (CS::WorldMapArea) in its third argument. A light hook keeps that pointer; from it:
//!   pan at +0x378 (2 floats), zoom at +0x380, the on-screen viewport rect at +0x340 (4 floats),
//!   the whole canvas rect at +0x350, and the displayed map at +0x904
//!   (0 surface, 1 underground, 10 and up the Land of Shadow).
//! The map is drawn on a 1920x1080 canvas scaled to the screen:
//!   map    = (world x - 7040, 16512 - world z)
//!   centre = (pan + viewport midpoint) / zoom
//!   screen = (map - centre) * zoom * (screen size / canvas size) + screen size / 2
//! All of this, and both patterns, are from Map for Goblins - DLL by VirusAlex (MIT licence).

use std::ffi::c_void;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::OnceLock;
use std::time::Instant;

use hudhook::mh::{MH_ApplyQueued, MH_Initialize, MhHook, MH_STATUS};

use crate::{log, memory};

const HOVER_PATTERN: &str = "40 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 D9 48 81 EC B0 00 00 00 \
    48 C7 45 B7 FE FF FF FF 48 89 9C 24 08 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 1F 49 8B F8";
const MAP_AREA_TABLE_PATTERN: &str = "48 8D 05 ?? ?? ?? ?? 48 89 01 48 8D 79 70 48 8B 07 48 8B CF FF 50 08";

/// The map counts as closed once the hook has not run for this long.
const CLOSED_AFTER_MS: u64 = 300;

type Hover = unsafe extern "system" fn(*mut c_void, *mut c_void, *mut c_void) -> *mut c_void;
static HOVER_ORIGINAL: OnceLock<Hover> = OnceLock::new();
static MAP_AREA_TABLE: AtomicUsize = AtomicUsize::new(0);
static MAP_AREA: AtomicUsize = AtomicUsize::new(0);
static LAST_SEEN_MS: AtomicU64 = AtomicU64::new(0);
static START: OnceLock<Instant> = OnceLock::new();

fn now_ms() -> u64 {
    START.get_or_init(Instant::now).elapsed().as_millis() as u64 + 1
}

pub fn install() {
    if let Err(e) = unsafe { hook() } {
        log::write(&format!("Target icons on the map are off: {e}"));
    }
}

unsafe fn hook() -> Result<(), String> {
    let table = memory::find_global(&memory::pattern(MAP_AREA_TABLE_PATTERN), 3, 7).ok_or("the map's layout was not found")?;
    MAP_AREA_TABLE.store(table, Ordering::SeqCst);
    let pattern = memory::pattern(HOVER_PATTERN);
    // The pattern is the routine's own first bytes, so the match is its start.
    let hover = memory::find_in_code(&pattern).ok_or("the map's hover routine was not found")?;
    match MH_Initialize() {
        MH_STATUS::MH_OK | MH_STATUS::MH_ERROR_ALREADY_INITIALIZED => {}
        status => return Err(format!("MinHook: {status:?}")),
    }
    let hook = MhHook::new(hover as *mut c_void, hover_detour as *mut c_void).map_err(|s| format!("{s:?}"))?;
    let _ = HOVER_ORIGINAL.set(std::mem::transmute::<*mut c_void, Hover>(hook.trampoline()));
    hook.queue_enable().map_err(|s| format!("{s:?}"))?;
    MH_ApplyQueued().ok().map_err(|s| format!("{s:?}"))?;
    now_ms();
    Ok(())
}

unsafe extern "system" fn hover_detour(panel: *mut c_void, item: *mut c_void, area: *mut c_void) -> *mut c_void {
    let table = MAP_AREA_TABLE.load(Ordering::Relaxed);
    if !area.is_null() && memory::read::<usize>(area as usize) == Some(table) {
        MAP_AREA.store(area as usize, Ordering::Relaxed);
        LAST_SEEN_MS.store(now_ms(), Ordering::Relaxed);
    }
    let original = HOVER_ORIGINAL.get().expect("map hook without its original");
    original(panel, item, area)
}

/// Which of the game's maps is on screen.
#[derive(Clone, Copy, PartialEq, Debug)]
pub enum Layer {
    Surface,
    Underground,
    LandOfShadow,
}

/// The open map's view, or None while the map is closed.
pub struct View {
    pan: [f32; 2],
    zoom: f32,
    viewport_mid: [f32; 2],
    pub layer: Layer,
}

pub fn view() -> Option<View> {
    if now_ms().saturating_sub(LAST_SEEN_MS.load(Ordering::Relaxed)) > CLOSED_AFTER_MS {
        return None;
    }
    let area = MAP_AREA.load(Ordering::Relaxed);
    let pan = memory::read::<[f32; 2]>(area + 0x378)?;
    let zoom = memory::read::<f32>(area + 0x380)?;
    let [min_x, min_z, max_x, max_z] = memory::read::<[f32; 4]>(area + 0x340)?;
    let [full_min_x, full_min_z, full_max_x, full_max_z] = memory::read::<[f32; 4]>(area + 0x350)?;
    let displayed = memory::read::<i32>(area + 0x904)?;
    let sane = zoom > 0.01 && max_x - min_x > 1.0 && max_z - min_z > 1.0 && full_max_x - full_min_x > 1.0 && full_max_z - full_min_z > 1.0;
    let layer = match displayed {
        0 => Layer::Surface,
        1 => Layer::Underground,
        10.. => Layer::LandOfShadow,
        _ => return None,
    };
    sane.then_some(View { pan, zoom, viewport_mid: [(min_x + max_x) / 2.0, (min_z + max_z) / 2.0], layer })
}

impl View {
    /// Where a world position (east, north) is on a screen of `screen` pixels.
    pub fn project(&self, world_x: f32, world_z: f32, screen: [f32; 2]) -> [f32; 2] {
        project(world_x, world_z, self.pan, self.zoom, self.viewport_mid, screen)
    }
}

fn project(world_x: f32, world_z: f32, pan: [f32; 2], zoom: f32, viewport_mid: [f32; 2], screen: [f32; 2]) -> [f32; 2] {
    let map = [world_x - 7040.0, 16512.0 - world_z];
    let scale = [screen[0] / 1920.0, screen[1] / 1080.0];
    std::array::from_fn(|i| {
        let centre = (pan[i] + viewport_mid[i]) / zoom;
        (map[i] - centre) * zoom * scale[i] + screen[i] / 2.0
    })
}

#[cfg(test)]
mod tests {
    use super::project;

    #[test]
    fn the_view_centre_lands_in_the_middle_of_the_screen() {
        // A view whose centre is the Dectus medallion pin (world 11771.3, 9178.86 -> map 4731.3, 7333.14).
        let zoom = 0.5;
        let mid = [960.0, 540.0];
        let pan = [4731.3 * zoom - mid[0], 7333.14 * zoom - mid[1]];
        let at = project(11771.3, 9178.86, pan, zoom, mid, [2560.0, 1440.0]);
        assert!((at[0] - 1280.0).abs() < 0.01 && (at[1] - 720.0).abs() < 0.01, "{at:?}");
        // 100 m further east is 100 * zoom canvas pixels to the right, scaled to the screen.
        let east = project(11871.3, 9178.86, pan, zoom, mid, [2560.0, 1440.0]);
        assert!((east[0] - at[0] - 100.0 * zoom * 2560.0 / 1920.0).abs() < 0.01);
        // North is up the screen.
        assert!(project(11771.3, 9278.86, pan, zoom, mid, [2560.0, 1440.0])[1] < at[1]);
    }
}
