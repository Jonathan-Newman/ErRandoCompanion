//! The "check found" sound: one of the built-in sounds, the player's own ErRandoCompanionMod.wav, or
//! the Windows notification sound, at the volume set in the overlay.
//!
//! Windows' PlaySound has no volume of its own, so the samples are scaled here (16-bit PCM WAV,
//! which the built-in sounds are and most WAV files are). A file in any other format plays as it is.

use std::path::Path;
use std::sync::Mutex;

use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::Media::Audio::{PlaySoundW, SND_ALIAS, SND_ASYNC, SND_FILENAME, SND_MEMORY, SND_NODEFAULT};

/// The choices, as stored in the ini and shown in Settings.
pub const CHOICES: [(&str, &str); 5] = [
    ("chime", "Chime"),
    ("bell", "Bell"),
    ("sparkle", "Sparkle"),
    ("windows", "Windows notification"),
    ("custom", "My own (ErRandoCompanionMod.wav)"),
];

static CHIME: &[u8] = include_bytes!("../assets/found_chime.wav");
static BELL: &[u8] = include_bytes!("../assets/found_bell.wav");
static SPARKLE: &[u8] = include_bytes!("../assets/found_sparkle.wav");

/// The sound being played. PlaySound reads it while playing, so it must live until replaced, and the
/// old one is only replaced after playback is stopped.
static PLAYING: Mutex<Vec<u8>> = Mutex::new(Vec::new());

/// Plays `choice` at `volume` (0 to 100). The Windows sound follows the system volume only.
pub fn play(directory: &Path, choice: &str, volume: u32) {
    let volume = volume.min(100) as f32 / 100.0;
    if volume <= 0.0 {
        return;
    }
    let custom = directory.join("ErRandoCompanionMod.wav");
    let wav: Vec<u8> = match choice {
        "windows" => {
            unsafe {
                let _ = PlaySoundW(w!("SystemNotification"), None, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
            }
            return;
        }
        "custom" => match std::fs::read(&custom) {
            Ok(bytes) => bytes,
            // No file yet: fall back to the default, so the setting never means silence by mistake.
            Err(_) => CHIME.to_vec(),
        },
        "bell" => BELL.to_vec(),
        "sparkle" => SPARKLE.to_vec(),
        _ => CHIME.to_vec(),
    };
    match scaled(wav, volume) {
        Some(wav) => {
            let mut playing = PLAYING.lock().unwrap_or_else(|p| p.into_inner());
            unsafe {
                // Stop whatever is playing before its buffer is replaced.
                let _ = PlaySoundW(PCWSTR::null(), None, Default::default());
                *playing = wav;
                let _ = PlaySoundW(PCWSTR(playing.as_ptr() as *const u16), None, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
            }
        }
        // Not a format that can be scaled: play the file itself.
        None => unsafe {
            let _ = PlaySoundW(&HSTRING::from(custom.as_os_str()), None, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        },
    }
}

/// A 16-bit PCM WAV with its samples multiplied by `volume`, or None for any other format.
fn scaled(mut wav: Vec<u8>, volume: f32) -> Option<Vec<u8>> {
    if wav.len() < 12 || &wav[0..4] != b"RIFF" || &wav[8..12] != b"WAVE" {
        return None;
    }
    let mut at = 12;
    let mut pcm16 = false;
    while at + 8 <= wav.len() {
        let id = &wav[at..at + 4];
        let size = u32::from_le_bytes(wav[at + 4..at + 8].try_into().ok()?) as usize;
        let body = at + 8;
        if id == b"fmt " && size >= 16 && body + 16 <= wav.len() {
            let format = u16::from_le_bytes([wav[body], wav[body + 1]]);
            let bits = u16::from_le_bytes([wav[body + 14], wav[body + 15]]);
            pcm16 = format == 1 && bits == 16;
        } else if id == b"data" {
            if !pcm16 {
                return None;
            }
            let end = (body + size).min(wav.len());
            for sample in wav[body..end].chunks_exact_mut(2) {
                let value = i16::from_le_bytes([sample[0], sample[1]]) as f32 * volume;
                sample.copy_from_slice(&(value.round().clamp(-32768.0, 32767.0) as i16).to_le_bytes());
            }
            return Some(wav);
        }
        at = body + size + (size & 1);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scales_the_built_in_sounds() {
        for sound in [CHIME, BELL, SPARKLE] {
            let loud = scaled(sound.to_vec(), 1.0).expect("16-bit PCM");
            let half = scaled(sound.to_vec(), 0.5).unwrap();
            assert_eq!(loud, sound, "full volume leaves it as it is");
            let peak = |w: &[u8]| w[44..].chunks_exact(2).map(|s| i16::from_le_bytes([s[0], s[1]]).unsigned_abs()).max().unwrap();
            assert!((peak(&half) as i32 - peak(&loud) as i32 / 2).abs() <= 1);
        }
        assert!(scaled(b"not a wav file".to_vec(), 0.5).is_none());
    }
}
