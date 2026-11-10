"""Builds the overlay's built-in "check found" sounds in mod/assets/:

  found_chime.wav    two soft bell notes a fifth apart (E5 then B5), with a faint sparkle
  found_bell.wav     one deeper, rounder bell (A4) that rings out
  found_sparkle.wav  three quick rising notes (C6, E6, G6), light and short

Each note is a few decaying harmonics with a gentle attack. 44.1 kHz, 16-bit mono, pure Python.
Run it again after changing a number here; the DLL embeds the files at build time.
"""
import math
import struct
import wave
from pathlib import Path

RATE = 44100
ASSETS = Path(__file__).resolve().parent.parent / "mod" / "assets"


def bell(t, start, frequency, loudness, ring=1.0):
    """One bell note: harmonics that die away at different speeds, with a 6 ms fade-in.
    `ring` above 1 makes it ring longer."""
    t -= start
    if t < 0:
        return 0.0
    attack = min(1.0, t / 0.006)
    partials = ((1.0, 1.0, 3.2), (2.0, 0.35, 5.5), (3.01, 0.15, 8.0), (4.2, 0.06, 11.0))
    value = sum(level * math.exp(-decay / ring * t) * math.sin(2 * math.pi * frequency * ratio * t)
                for ratio, level, decay in partials)
    return loudness * attack * value


def chime(t):
    value = bell(t, 0.0, 659.25, 0.55) + bell(t, 0.11, 987.77, 0.5)
    if t > 0.11:
        s = t - 0.11
        value += 0.05 * math.exp(-14 * s) * math.sin(2 * math.pi * 2637.0 * s)
    return value


def deep_bell(t):
    return bell(t, 0.0, 440.0, 0.7, ring=1.8) + bell(t, 0.0, 880.0 * 1.003, 0.12, ring=1.2)


def sparkle(t):
    return sum(bell(t, 0.07 * i, f, 0.4, ring=1.3) for i, f in enumerate((1046.5, 1318.5, 1568.0)))


SOUNDS = {"found_chime.wav": (chime, 0.9), "found_bell.wav": (deep_bell, 1.4), "found_sparkle.wav": (sparkle, 0.75)}


def write(name, shape, length):
    tail = lambda t: max(0.0, min(1.0, (length - t) / 0.15))  # fade to silence: no click at the end
    frames = [shape(i / RATE) * tail(i / RATE) for i in range(int(RATE * length))]
    peak = max(abs(f) for f in frames)
    scale = 0.7 * 32767 / peak  # leaves headroom; the overlay's volume setting scales from here
    path = ASSETS / name
    with wave.open(str(path), "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(b"".join(struct.pack("<h", int(f * scale)) for f in frames))
    print(f"Wrote {path} ({path.stat().st_size} bytes)")


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    for name, (shape, length) in SOUNDS.items():
        write(name, shape, length)


if __name__ == "__main__":
    main()
