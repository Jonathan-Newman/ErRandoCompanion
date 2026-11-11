# Elden Ring Randomizer Companion

A tracker for runs of [thefifthmatt's Elden Ring Item and Enemy Randomizer](https://www.nexusmods.com/eldenring/mods/428),
by **Moordoor**. It works out which checks you can do with what you have, ticks them off automatically while you play,
and has an in-game overlay.

> [!IMPORTANT]
> It never shows where the randomizer put anything. The spoiler log is only used for logic and auto-tracking.

## Features

- **Logic-aware checklist.** Every check, marked available, locked or done, from the key items you have.
- **Auto-tracking.** Reads the running game and ticks checks, key items and bosses as you get them.
- **In-game overlay.** The checklist inside Elden Ring, with keyboard and controller support, targets, a guide arrow, the
  game's own beacon and map markers on your target, and a "check found" popup.
- **Every check, too.** An All checks tab with every slot the randomizer can fill in your seed (about 4,800: every
  pickup, chest, enemy drop, boss reward, quest and shop item), grouped by place and opened or locked by the
  randomizer's own logic. Built for chaotic seeds.
- **Map.** Every check on the world map, surface, underground and Land of Shadow (map pictures are made from your own
  game, see below).
- **Routing help.** Nearest checks, walk or warp to a Site of Grace you have found, and what's left in each region.
- **Run tracking.** Run goals, a timer and splits against your best run, deaths by area, a log of everything you pick
  up, and run history.
- **Backups.** Runs are backed up as you play, together with a copy of the game save.

## Download

Get the latest release from the [Releases page](https://github.com/Jonathan-Newman/ErRandoCompanion/releases):

| File | What it is |
| --- | --- |
| `ErRandoCompanion-<version>-app.zip` | The desktop app: one exe, no install, no .NET needed. |
| `ErRandoCompanionMod-<version>.zip` | The in-game overlay, loaded by the randomizer. |

Use either one or both. They share the same runs, and never overwrite each other.

### Desktop app

1. Unzip anywhere and run `ErRandoCompanion.exe`.
2. Click **Randomizer folder...** and pick the randomizer's folder (the one with `spoiler_logs` in it).
3. Randomize as usual. The app opens a run for the new seed, and tracks it once Elden Ring is running.

### In-game overlay

Copy the `ErRandoCompanion` folder into your randomizer folder, then add `ErRandoCompanionMod.dll` with the
randomizer's **All dll mod** button. The [overlay guide](mod/README.md) has the keys, the controller and everything it
shows.

### Map pictures

The world map art belongs to FromSoftware, so it is not included. The first time you open the app's **Map** tab, click
**Make the map from my game**: the app reads your own copy of Elden Ring (it never changes it) and makes the pictures in
a few seconds. They are kept in `%LOCALAPPDATA%\ErRandoCompanion\MapArt`.

## Building from source

| Folder | What it is |
| --- | --- |
| `Core` | The tracker: logic, run saves, spoiler log and regulation reading, goals, timeline, routes, map art. |
| `GameReader` | Reads the running game (event flags, position, inventory, death count). |
| `App` | The Windows desktop app (WPF, .NET 8). |
| `Native` | The tracker as a NativeAOT DLL with a small JSON interface, used by the overlay. |
| `mod` | The in-game overlay (Rust, hudhook + Dear ImGui, DirectX 12). |
| `Data` | Checks, regions, items, randomizer settings, map positions and game memory signatures. |
| `Tests` | xUnit tests for the C# side; `cargo test` covers the overlay. |
| `tools` | Scripts that build the data files and package the mod. |

Needs the .NET 8 SDK (with the Visual Studio C++ build tools, for NativeAOT) and Rust.

```bash
dotnet publish App -p:PublishProfile=win-x64 -p:IncludeMapArt=false
```

```bash
powershell -File tools/package_mod.ps1
```

```bash
dotnet test Tests
```

The first builds the desktop app into `publish/`. The second builds the tracker DLL and the overlay into
`dist/ErRandoCompanion`. Tests that need real game or randomizer files are skipped unless their environment variables
are set, and no test reads or writes your real runs or game save. `ROADMAP.md` lists every feature by phase.

## Licence and credits

GPL-3.0 (see [LICENSE](LICENSE)), because part of the game reader is ported from SoulSplitter, which is GPL-3.0.
Everyone this builds on is credited in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), including Rosemagwin's
Elden Ring Randomizer Checklist, which the check list and logic come from.

Not affiliated with or endorsed by FromSoftware, Bandai Namco or the randomizer's author.
