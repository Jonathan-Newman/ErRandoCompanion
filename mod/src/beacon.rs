//! The game's own beacon (the column of light you can place from the map) on the active target.
//!
//! The map's player markers live in one container: 10 beacon slots, then the stamps, 16 bytes each:
//! { index: i32, x: f32, z: f32, kind: u16, pad: u16 }, with an empty slot reading { -1, 0, 0, 0x0100 }.
//! A beacon on the surface map is kind 0x0101, and its x and z are map coordinates:
//! x = world x - 7042, z = 16511 - world z. The container's location, layout and these numbers come
//! from Map for Goblins - DLL by VirusAlex (MIT licence).
//!
//! Only the first beacon slot is used. Whatever the player had there is kept and put back when the
//! companion's beacon is removed.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

use crate::{log, memory};

const CHAIN_PATTERN: &str = "48 8B 0D ?? ?? ?? ?? 48 8B 49 30 48 8D 55 5F";
const CONTAINER_TABLE_PATTERN: &str = "48 8D 05 ?? ?? ?? ?? 48 89 07 48 8D 5F 10 48 8D 05 ?? ?? ?? ??";
const CONTAINER_OFFSET: usize = 0x68;
const BEACONS_OFFSET: usize = 0x118;
pub const SLOTS: usize = 10;
const SURFACE_BEACON: u16 = 0x0101;
const EMPTY: Slot = Slot { index: -1, x: 0.0, z: 0.0, kind: 0x0100, pad: 0 };

static CHAIN: AtomicUsize = AtomicUsize::new(0);
static CONTAINER_TABLE: AtomicUsize = AtomicUsize::new(0);
/// What was in slot 1 before the companion put its beacon there, and where the companion's is.
static STATE: Mutex<State> = Mutex::new(State { saved: None, placed: None });

struct State {
    saved: Option<Slot>,
    placed: Option<Slot>,
}

#[repr(C)]
#[derive(Clone, Copy, Default, PartialEq, Debug)]
pub struct Slot {
    pub index: i32,
    pub x: f32,
    pub z: f32,
    pub kind: u16,
    pub pad: u16,
}

impl Slot {
    pub fn is_empty(&self) -> bool {
        self.index == -1 && self.x == 0.0 && self.z == 0.0
    }
}

pub fn install() {
    let chain = memory::find_global(&memory::pattern(CHAIN_PATTERN), 3, 7);
    let table = memory::find_global(&memory::pattern(CONTAINER_TABLE_PATTERN), 3, 7);
    match (chain, table) {
        (Some(chain), Some(table)) => {
            CHAIN.store(chain, Ordering::SeqCst);
            CONTAINER_TABLE.store(table, Ordering::SeqCst);
        }
        _ => log::write("Could not find the game's map markers, so the beacon is off."),
    }
}

/// Where the beacon slots are, once a character is loaded. Checks the container really is the
/// marker container before trusting it.
fn beacons() -> Option<usize> {
    let chain = CHAIN.load(Ordering::SeqCst);
    if chain == 0 {
        return None;
    }
    let root = memory::read::<usize>(chain).filter(|&r| r != 0)?;
    let container = memory::read::<usize>(root + CONTAINER_OFFSET).filter(|&c| c != 0)?;
    (memory::read::<usize>(container)? == CONTAINER_TABLE.load(Ordering::SeqCst)).then_some(container + BEACONS_OFFSET)
}

/// The beacon slots as the game has them, or None while no character is loaded.
pub fn slots() -> Option<[Slot; SLOTS]> {
    memory::read::<[Slot; SLOTS]>(beacons()?)
}

/// Map coordinates for a surface world position.
pub fn map_position(world_x: f32, world_z: f32) -> (f32, f32) {
    (world_x - 7042.0, 16511.0 - world_z)
}

/// Puts the companion's beacon in slot 1 at a surface world position.
pub fn place(world_x: f32, world_z: f32) -> Result<(), String> {
    let address = beacons().ok_or("load a character first")?;
    let current = memory::read::<Slot>(address).ok_or("the beacon could not be read")?;
    let (x, z) = map_position(world_x, world_z);
    let mut state = STATE.lock().unwrap_or_else(|p| p.into_inner());
    // Keep the player's own beacon the first time ours goes in, so it can come back.
    if state.placed.is_none_or(|placed| placed != current) {
        state.saved = Some(current);
    }
    let index = if current.is_empty() { 0 } else { current.index };
    let slot = Slot { index, x, z, kind: SURFACE_BEACON, pad: 0 };
    write(address, slot)?;
    state.placed = Some(slot);
    Ok(())
}

/// Takes the companion's beacon away and puts back whatever the player had there.
pub fn remove() -> Result<(), String> {
    let mut state = STATE.lock().unwrap_or_else(|p| p.into_inner());
    let Some(placed) = state.placed.take() else { return Ok(()) };
    let address = beacons().ok_or("load a character first")?;
    // If the player has since moved the beacon themselves, leave theirs alone.
    if memory::read::<Slot>(address) != Some(placed) {
        state.saved = None;
        return Ok(());
    }
    write(address, state.saved.take().unwrap_or(EMPTY))
}

/// The target the beacon was last put on, so it is placed once per target change. A player who then
/// moves or removes the beacon is not overruled until the target changes again.
static FOLLOWED: Mutex<Option<(f32, f32)>> = Mutex::new(None);

/// Keeps the beacon on `target` (a surface world position), or removes it for None.
pub fn follow(target: Option<(f32, f32)>) -> Result<(), String> {
    let mut followed = FOLLOWED.lock().unwrap_or_else(|p| p.into_inner());
    if *followed == target {
        return Ok(());
    }
    match target {
        Some((x, z)) => place(x, z)?,
        None => remove()?,
    }
    *followed = target;
    Ok(())
}

fn write(address: usize, slot: Slot) -> Result<(), String> {
    // Read first: an address that cannot be read must not be written either.
    memory::read::<Slot>(address).ok_or("the beacon could not be read")?;
    unsafe { (address as *mut Slot).write_volatile(slot) };
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slots_are_sixteen_bytes_and_convert_to_map_coordinates() {
        assert_eq!(std::mem::size_of::<Slot>(), 16);
        assert!(EMPTY.is_empty());
        // Limgrave's Dectus medallion pin: world (11771.3, 9178.86).
        let (x, z) = map_position(11771.3, 9178.86);
        assert!((x - 4729.3).abs() < 0.01 && (z - 7332.14).abs() < 0.01);
    }
}
