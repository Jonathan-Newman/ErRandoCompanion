//! Whether a boss health bar is on screen, so the overlay can keep out of the way during a fight.
//!
//! The front-end manager (CSFeManImp) keeps three boss health bar entries of 0x20 bytes at +0x5BF0:
//! the boss name's text id at +0x0 and the boss's handle at +0x8. An unused entry has no name and an
//! empty handle. The layout is the one fromsoftware-rs documents (Vincent Swarte; MIT/Apache, non-AI
//! training licences); the pattern that finds the manager is the one Map for Goblins - DLL uses.

use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::OnceLock;
use std::time::Instant;

use crate::{log, memory};

const FE_MAN_PATTERN: &str = "48 89 05 ?? ?? ?? ?? 48 8B 8B 80 00 00 00 48 85 C9 74 05 E8";
const BARS_OFFSET: usize = 0x5BF0;
const BAR_SIZE: usize = 0x20;
const BARS: usize = 3;

static FE_MAN: AtomicUsize = AtomicUsize::new(0);
static WAS_FIGHTING: AtomicBool = AtomicBool::new(false);
/// The answer is kept for this long: health bars come and go over seconds, and reading them costs a few syscalls.
const RECHECK_MS: u64 = 100;
static LAST_CHECK_MS: AtomicU64 = AtomicU64::new(0);
static START: OnceLock<Instant> = OnceLock::new();

pub fn install() {
    match memory::find_global(&memory::pattern(FE_MAN_PATTERN), 3, 7) {
        Some(address) => FE_MAN.store(address, Ordering::SeqCst),
        None => log::write("Could not find the game's health bars, so the overlay stays up in boss fights."),
    }
}

/// True while any boss health bar is showing (checked at most ten times a second).
pub fn in_fight() -> bool {
    let now = START.get_or_init(Instant::now).elapsed().as_millis() as u64 + 1;
    if now.saturating_sub(LAST_CHECK_MS.load(Ordering::Relaxed)) < RECHECK_MS {
        return WAS_FIGHTING.load(Ordering::Relaxed);
    }
    LAST_CHECK_MS.store(now, Ordering::Relaxed);
    let global = FE_MAN.load(Ordering::Relaxed);
    let Some(manager) = (global != 0).then(|| memory::read::<usize>(global)).flatten().filter(|&m| m != 0) else {
        WAS_FIGHTING.store(false, Ordering::Relaxed);
        return false;
    };
    let names: Vec<i32> = (0..BARS)
        .filter_map(|i| memory::read::<[u8; BAR_SIZE]>(manager + BARS_OFFSET + i * BAR_SIZE))
        .filter(|bar| {
            let name = i32::from_le_bytes(bar[0..4].try_into().unwrap());
            let handle = u64::from_le_bytes(bar[8..16].try_into().unwrap());
            name > 0 && handle != 0 && handle != u64::MAX
        })
        .map(|bar| i32::from_le_bytes(bar[0..4].try_into().unwrap()))
        .collect();
    let fighting = !names.is_empty();
    // Logged once per fight, so the log shows the detection working (or not) in each fight.
    if fighting != WAS_FIGHTING.swap(fighting, Ordering::Relaxed) {
        log::write(&if fighting { format!("Boss fight: health bar showing (name ids {names:?}); overlay hidden.") } else { "Boss fight over; overlay back.".into() });
    }
    fighting
}
