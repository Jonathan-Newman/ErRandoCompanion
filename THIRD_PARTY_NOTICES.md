# Third-party notices

Elden Ring Randomizer Companion is Copyright (c) 2026 Moordoor and is licensed under the GNU General Public
License, version 3 (see `LICENSE`). It is GPL-3.0 because part of it is a port of GPL-3.0 code (SoulSplitter, below).

It builds on the work of the people below. Licence texts for code that is bundled into the built files are in the
`licenses` folder.

## Code and research this project is based on

| Project | Author | Licence | What is used |
| --- | --- | --- | --- |
| [SoulSplitter](https://github.com/FrankvdStam/SoulSplitter) | Frank van der Stam | GPL-3.0 | `GameReader/EventFlagReader.cs` is ported from its Elden Ring event flag reader; the player position pointer path and offsets in `GameReader/PlayerReader.cs` come from it. |
| [fromsoftware-rs](https://github.com/vswarte/eldenring-rs) | Vincent Swarte | MIT NON-AI / Apache-2.0 NON-AI (`licenses/fromsoftware-rs-MIT-NON-AI.txt`) | Memory layouts: GameDataMan (inventory, death count), the boss health bars, the HUD state, and WorldChrMan's characters (for counting kills). Its licence forbids using the software, or derivatives of it, to train machine learning models; that restriction carries over to those parts. |
| Map for Goblins - DLL | VirusAlex | MIT (`licenses/MapForGoblins-MIT.txt`) | The approach for adding markers to the game's world map, the map projection and the byte patterns used to find them (`mod/src/map_markers.rs`, `map_view.rs`, `beacon.rs`, `boss.rs`). |
| Goblin-ERR | Gacsam | MIT (`licenses/MapForGoblins-MIT.txt`) | What Map for Goblins - DLL is based on. |
| [The Grand Archives Elden Ring table](https://github.com/The-Grand-Archives/Elden-Ring-CT-TGA) | The Grand Archives contributors | — | The locations of the camera and the menu cursor flag (addresses only, no code). |
| [Elden Ring practice tool](https://github.com/veeenu/eldenring-practice-tool) | veeenu | AGPL-3.0 | Ideas and published addresses only; no code from it is included. |

## Data

| Source | Author | What is used |
| --- | --- | --- |
| Elden Ring Randomizer Checklist (spreadsheet), shared in the Steam guide [Elden Ring Randomizer Checklist](https://steamcommunity.com/sharedfiles/filedetails/?id=3786372097) | Rosemagwin | `Data/locations.json`, `regions.json`, `items.json` and `settings.json` were converted from it by `tools/convert_checklist.py`: the check list, the logic and the location descriptions. No licence is given with it; ask the author's permission before publishing this data. |
| [Elden Ring Item and Enemy Randomizer](https://www.nexusmods.com/eldenring/mods/428) | thefifthmatt | Read at run time from the player's own randomizer folder (spoiler logs, `diste` name and slot tables); nothing from it is included. `tools/build_merchant_shops.py` took only ShopLineupParam row numbers from its `itemslots.txt`. Place descriptions shown in the Found tab come from that file on the player's machine. |
| ELDEN RING | FromSoftware, Inc. / Bandai Namco Entertainment | Game data is read from the player's own installation while they play. `Data/map_data.json` holds check positions computed from the game's map data by `tools/build_map_data.py`. The world map art is FromSoftware's and is **not** part of this repository or its releases: the desktop app makes it from the player's own game (`Core/MapArt`), and `tools/build_map_data.py` does the same for development. |

This project is a fan-made tool. It is not affiliated with or endorsed by FromSoftware, Bandai Namco or the
randomizer's author. ELDEN RING is a trademark of FromSoftware, Inc. / Bandai Namco Entertainment Inc.

## Bundled libraries

The in-game overlay DLL (`ErRandoCompanionMod.dll`) contains:

| Library | Licence | Text |
| --- | --- | --- |
| [hudhook](https://github.com/veeenu/hudhook) (Andrea Venuta) | MIT | `licenses/hudhook-MIT.txt` |
| [MinHook](https://github.com/TsudaKageyu/minhook) (Tsuda Kageyu), inside hudhook | BSD-2-Clause | `licenses/MinHook-BSD-2-Clause.txt` |
| [Dear ImGui](https://github.com/ocornut/imgui) (Omar Cornut) | MIT | `licenses/DearImGui-MIT.txt` |
| [imgui-rs](https://github.com/imgui-rs/imgui-rs) | MIT or Apache-2.0 | `licenses/imgui-rs-MIT.txt` |
| [windows-rs](https://github.com/microsoft/windows-rs) (Microsoft) | MIT or Apache-2.0 | `licenses/windows-rs-MIT.txt` |
| [serde](https://github.com/serde-rs/serde), [serde_json](https://github.com/serde-rs/json) | MIT or Apache-2.0 | `licenses/serde-MIT.txt`, `licenses/serde_json-MIT.txt` |
| Smaller crates (parking_lot, once_cell, tracing, memchr, aho-corasick, regex-automata, …) | MIT, Apache-2.0, Unlicense or Zlib, chosen as MIT where offered | `cargo metadata` lists each one |

The desktop app and the tracker library are built on .NET (Microsoft, MIT licence). The tests use xUnit
(Apache-2.0) and coverlet (MIT), which are not distributed.

The overlay uses the Segoe UI font installed with Windows. The game's own Oodle library (`oo2core_6_win64.dll`)
is loaded from the player's game or randomizer folder to read game files; it is not included. The "check found" sounds in
`mod/assets` are generated by `tools/make_found_sound.py` and are part of this project.
