//! Which way the game's camera faces, for the guide arrow.
//!
//! The gameplay camera (CSPersCam1) is at [[[FieldArea] + 0x20] + 0x20]; its rotation matrix starts at +0x10,
//! one row per axis, with the forward row at +0x30. The pattern and offsets are the ones published in
//! The Grand Archives' Elden Ring table. Every read goes through `memory::read`, so a moved
//! structure after a game update makes the arrow disappear rather than crash the game.

use std::sync::atomic::{AtomicUsize, Ordering};

use crate::{log, memory};

const FIELD_AREA_PATTERN: &str = "48 8B 3D ?? ?? ?? ?? 49 8B D8 48 8B F2 4C 8B F1 48 85 FF";
const FORWARD_ROW: usize = 0x30;

static FIELD_AREA: AtomicUsize = AtomicUsize::new(0);

pub fn install() {
    match memory::find_global(&memory::pattern(FIELD_AREA_PATTERN), 3, 7) {
        Some(address) => FIELD_AREA.store(address, Ordering::SeqCst),
        None => log::write("Could not find the game's camera, so the guide arrow is off."),
    }
}

/// The camera's facing on the ground, as a unit (east, north) vector. None while it cannot be read,
/// such as on the title screen, or when the camera looks straight down.
pub fn facing() -> Option<(f32, f32)> {
    let field_area = FIELD_AREA.load(Ordering::SeqCst);
    if field_area == 0 {
        return None;
    }
    let camera = memory::follow(field_area, &[0x20, 0x20])?;
    let [x, _, z] = memory::read::<[f32; 3]>(camera + FORWARD_ROW)?;
    let length = (x * x + z * z).sqrt();
    (length.is_finite() && length > 0.05).then(|| (x / length, z / length))
}

/// How far to turn, in radians clockwise, to face the direction (dx, dz) from a camera facing `facing`.
/// 0 is straight ahead, plus a quarter turn is to the right.
pub fn turn(facing: (f32, f32), dx: f64, dz: f64) -> f32 {
    let (fx, fz) = facing;
    // With +x east and +z north, the camera's right is (fz, -fx).
    let ahead = dx as f32 * fx + dz as f32 * fz;
    let right = dx as f32 * fz - dz as f32 * fx;
    right.atan2(ahead)
}

#[cfg(test)]
mod tests {
    use super::turn;
    use std::f32::consts::FRAC_PI_2;

    #[test]
    fn turns_toward_the_target() {
        let north = (0.0, 1.0);
        assert!(turn(north, 0.0, 100.0).abs() < 1e-5, "straight ahead");
        assert!((turn(north, 100.0, 0.0) - FRAC_PI_2).abs() < 1e-5, "east is to the right when facing north");
        assert!((turn(north, -100.0, 0.0) + FRAC_PI_2).abs() < 1e-5, "west is to the left");
        let east = (1.0, 0.0);
        assert!((turn(east, 0.0, -100.0) - FRAC_PI_2).abs() < 1e-5, "south is to the right when facing east");
    }
}
