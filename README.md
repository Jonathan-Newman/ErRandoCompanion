# Elden Ring Randomizer Companion

By Moordoor. A tracker for runs of thefifthmatt's Elden Ring Item and Enemy Randomizer: which checks you can do with
what you have, auto-tracking from the game while you play, and an in-game overlay. It never shows where the
randomizer put anything.

## What is in here

| Folder | What it is |
| --- | --- |
| `Core` | The tracker: logic, run saves, spoiler log and regulation reading, goals, timeline, routes. Shared by everything below. |
| `GameReader` | Reads the running game (event flags, player position, inventory, death count) from outside or inside its process. |
| `App` | The Windows desktop app (WPF, .NET 8). |
| `Native` | The tracker as a NativeAOT DLL with a small JSON interface, used by the overlay. |
| `mod` | The in-game overlay (Rust, hudhook + Dear ImGui, DirectX 12). Its `README.txt` is the player's guide. |
| `Data` | Checks, regions, items, randomizer settings, map positions and the game memory signatures. |
| `Tests` | xUnit tests for the C# side; `cargo test` covers the overlay. |
| `tools` | Scripts that build the data files and package the mod. |

`ROADMAP.md` lists every feature by phase.

## Building

- Desktop app: `dotnet publish App -p:PublishProfile=win-x64` (output in `publish/`).
- Overlay: `powershell -File tools/package_mod.ps1` builds the NativeAOT tracker and the Rust DLL and puts the mod in
  `dist/ErRandoCompanion`. Needs the .NET 8 SDK with the C++ build tools, and Rust.
- Tests: `dotnet test Tests` and `cargo test --release` in `mod`.
- Map art: the world map images are FromSoftware's and are not in this repository. Generate them from your own game
  install with `python tools/extract_map_art.py`.

Tests that need real game or randomizer files are skipped unless their environment variables are set, and nothing in
the tests reads or writes your real runs or game save.

## Licence

GPL-3.0 (see `LICENSE`), because part of the game reader is ported from SoulSplitter, which is GPL-3.0. The people
and projects this builds on are credited in `THIRD_PARTY_NOTICES.md`.

Not affiliated with or endorsed by FromSoftware, Bandai Namco or the randomizer's author.
