# ErRandoCompanion roadmap

In-game overlay plan. The desktop app stays alongside the mod.

| # | Phase | Status |
|---|-------|--------|
| 1 | Delete runs | Done |
| 2 | Detect the seed and settings automatically | Done |
| 3 | Targets 1–5 | Done |
| 4 | World coordinates for each map pin | Done |
| 5 | Tracker DLL with player position and guidance (`Native/`) | Done |
| 6 | In-game overlay mod (`mod/`), with mouse, controller and check descriptions | Done; tested in game |
| 7 | Guide arrow toward the active target, turning with the camera | Done |
| 8 | Target markers on the game's own map, plus the game's beacon on the active target | Markers done; icon to pick; beacon needs an in-game test |
| 9 | Corner display lists the nearest available checks, not just counts; overlay hides during boss fights | Built; needs an in-game test |
| 10 | Steam-style "check found" notification with a built-in chime, listing what the find made available (covers 14); companion badge icons drawn over the game's map | Built; checked in the test window, needs an in-game test |
| 11 | Rebuild the desktop app's exe (it predated the same-seed fix); overlay hidden and closed on the title screen and loading screens | Done; exe rebuilt and launched; menu hiding needs an in-game check |
| 12 | Stop the desktop app and the mod from overwriting each other: the second one to open a run goes read-only and says why | Done; tested with both running on a copy of the runs |
| 13 | Automatic backups: keep the last few copies of each run file, with a way to restore one | Done; every 10 minutes of play plus before delete or restore, last 10 kept; restore in the overlay's Runs tab and the app's Backups button |
| 14 | "What just opened up": after an item unlocks checks, show which (check names only, never contents) | Built into phase 10's notification |
| 15 | Auto-tracking status in the overlay ("working" or "off, the game was updated") | Done; also stops the endless rescan when a game update breaks the patterns |
| 16 | Run timer and pace: time played on the seed and checks per hour; the timer stops when the run's goal (phase 17) is met | Done; counts in-world time only, saved with the run; stops at FinishedAt, which phase 17 will set |
| 17 | Run goals: pick what finishes the run, such as find X key items, defeat X major bosses, or beat the game by defeating the final boss; shows progress toward it and ends the run (and its timer) when met | Done; Elden Beast, Promised Consort Radahn, major bosses (some or all), key items (some or all), every check |
| 18 | Merchant checks never tick automatically: find out which event flags shop purchases set and fix auto-tracking for them | Done; a merchant ticks at the first purchase of a limited item from it (spoiler-safe); shop rows from the randomizer's itemslots.txt via tools/build_merchant_shops.py |
| 19 | Run goal "defeat bosses I choose": tick bosses; each counts wherever enemy randomization put it (spoiler log boss placements, never shown) | Done |
| 20 | What unlocks what: each missing key or shortcut item and how many locked checks it would open now (logic only) | Done; overlay Items tab, app Items tab |
| 21 | Run history and best times: finished runs with goal, time, checks and pace; best per goal marked | Done; overlay Runs tab, app History tab |
| 22 | Found items log: every pickup (weapons, armour, talismans, ashes, spells, spirit ashes, other items) with where and when, for chaotic seeds | Done; overlay and app Found tabs (random drops: see 23) |
| 23 | Random enemy drops (and purchases, crafting) in the Found log, by comparing the inventory | Built; inventory found by its shape, needs an in-game check |
| 24 | What's left here: open, skipped, locked and done checks per region | Done; overlay Progress tab, app Progress tab and region headers |
| 25 | Dead-end warning: with 3 or fewer open checks, the key items that would open the most | Done; overlay header and small display, app banner |
| 26 | Grace hint: the nearest found Site of Grace to the active target, to warp to | Built; graces from BonfireWarpParam (checked against the real regulation), needs an in-game check |
| 27 | Run summary: time, checks, bosses, deaths, slowest stretch, key items in order; kept in run history | Done; overlay Progress tab and Runs > Summary, app Progress and History tabs |
| 28 | Deaths by area, from the game's own death count | Built; GameDataMan+0x94, needs an in-game check |
| 29 | Split timer: a step per goal part (Great Runes, chosen bosses, major bosses, key items, quarters of all checks), against your best run | Done |
| 30 | Skip for now: right-click (or Y) a check, or a hotkey (F7) for the nearest; skipped checks leave the open counts and nearest list | Done |
| 31 | Overlay layouts: play and plan versions of the small display, swapped with F6 | Done |
| 32 | Hide everything at once (F10), for recording or streaming | Done |
| 33 | Route distance: walk from here or warp to a found grace, whichever is shorter; dungeon checks through their entrance then inside; how far up or down (straight lines between those points, not the terrain; real navmeshes were tested and set aside) | Built; needs an in-game check |
| 34 | Found tab places: the randomizer's placeholder text ("aaaa...") replaced by the nearest grace, distance and height; saved entries mended | Done |
| 35 | Game save backed up (compressed) with each run backup; restored from the app's Backups window with the game closed | Done |
| 36 | Map pictures made in the app from the player's own game (archives, Oodle, BC7/BC1 tiles), so no FromSoftware art ships | Done; decoders match Pillow, checked against the real game |

## Ongoing
- After a game update, check `ErRandoCompanionMod.log`. If auto-tracking or the cursor stops working, the memory patterns need updating (`Data\signatures.json` and `mod/src/game_input.rs`).
