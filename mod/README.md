# In-game overlay

The checklist from the Elden Ring Randomizer Companion desktop app, drawn inside Elden Ring. It reads the game to tick
checks automatically, and it **never shows where items are**.

- [Install](#install)
- [Keys](#keys)
- [Controller](#controller)
- [What it shows](#what-it-shows)
- [Runs, saves and backups](#runs-saves-and-backups)
- [Tabs](#tabs)
- [Files it writes](#files-it-writes)
- [Licences](#licences)

## Install

1. Copy the whole `ErRandoCompanion` folder into your randomizer folder, so you have:

   ```text
   ...\randomizer\ErRandoCompanion\ErRandoCompanionMod.dll
   ...\randomizer\ErRandoCompanion\ErRandoCompanion.Native.dll
   ...\randomizer\ErRandoCompanion\Data\
   ```

2. In the randomizer program, click **All dll mod** and add `ErRandoCompanionMod.dll`. The randomizer remembers it and
   loads it every time you click **Launch Elden Ring**.

<details>
<summary>Using Mod Engine 2 or me3 by hand instead</summary>

Mod Engine 2: add it to `external_dlls` in `config_eldenring.toml`:

```toml
external_dlls = [ "randomizer\\ErRandoCompanion\\ErRandoCompanionMod.dll" ]
```

me3: add it to your profile:

```toml
[[natives]]
path = "randomizer/ErRandoCompanion/ErRandoCompanionMod.dll"
```

</details>

The DLLs can also sit straight in the randomizer folder; the overlay finds `spoiler_logs` either way.

> [!NOTE]
> Mods only run with Easy Anti-Cheat off, which launching through the randomizer already does.

## Keys

| Key | What it does |
| --- | --- |
| **F9** | Open or close the checklist. |
| **F7** | Skip the nearest available check for now. It leaves the open counts and the nearest list; undo it with right-click (or **Y**) on the check. |
| **F6** | Swap the small display between its **Play** and **Plan** layouts. Pick what each shows in the Settings tab. |
| **F10** | Hide everything the overlay draws, and press again to bring it back (for recording). **F9** also brings it back. |
| **Right-click** a check | Make it target 1 to 5. The Targets tab picks which one the guide follows. |
| Click a check's **box** | Tick it by hand. |
| Click a check's **name** | Make it a target, or clear it if it already is one. |

Every key, and a controller combo for each, can be changed in `ErRandoCompanionMod.ini`.

While the checklist is open the game shows its own mouse cursor, and your character and camera ignore the keyboard,
mouse and controller.

## Controller

| Button | What it does |
| --- | --- |
| **L3 + R3** (click both sticks) | Open or close the checklist (`pad_toggle` in the .ini). |
| **D-pad** or **left stick** | Move between rows. |
| **A** | Make the selected check a target (the first free number), or stop tracking it if it is one. Also opens a region or presses a button. |
| Hold **A** | Tick a check by hand (checks are normally ticked automatically). |
| **B** | Back out of a menu or list; at the top of the panel, close it. |
| **Y** | Pick which target number (1-5) the selected check gets. |
| **LB** / **RB** | Previous or next tab. |
| **Right stick** | Scroll. |

The search box needs a keyboard; the filter above the list works with the controller. The hints on screen follow
whichever you used last.

## What it shows

**Small display (top right).** Your counts and the active target while the checklist is closed. It also lists the
nearest available checks that are not targets yet, each with a small arrow and its distance (`nearby_checks`, 0 to 10;
0 hides the list).

**Gold arrow.** Points from where the camera faces toward the active target (straight up = ahead). Turn it off with
`guide_arrow=false` or in the Settings tab, which also shows which way the camera faces so you can compare it with the
game's compass.

**Route.** The target line picks the shorter way: walking from where you are, or warping to a Site of Grace you have
found and walking from there ("warp to Church of Elleh, then 300 m"). A warp counts as 250 m, for the menus and loading.
A check inside a dungeon is reached through its entrance, then the walk inside; a target in the other realm through a
grace you have found there. When walking, it also says how far up or down the target is. Each walk is a straight line
between those points, so cliffs and rivers are not known.

**Nearest grace.** Under the target line: the Site of Grace you have found that is closest to the active target, so you
know where to warp. Graces you have not found are never suggested.

**Game map.** Targets 1-5 get a gold numbered badge over the game's world map, on whichever map is showing (surface,
underground, Land of Shadow); the active target's badge pulses (`map_icons=false` turns them off). The game's own map
also gets a hoverable entry per target, named "Target N: check" (`map_markers`). Pick which built-in icon sits under the
badge in the Settings tab, or with `map_icon`.

> [!WARNING]
> Do not use the map markers together with Map for Goblins, which also rewrites that table.

**Beacon.** The game's own beacon (the column of light, also on the compass) goes on the active target, for targets on
the surface map. A beacon you placed yourself comes back when the target is cleared, and moving the beacon yourself is
respected until the target changes. Turn it off in Settings or with `beacon_on_target=false`.

**Check found.** When auto-tracking ticks a check, a Steam-style notification slides up in the bottom-right corner
naming it, its region and any checks it made available, with a chime. The Settings tab picks the sound (Chime, Bell,
Sparkle, the Windows notification, or your own `ErRandoCompanionMod.wav` beside the DLL) and its volume, and has a Test
button. `found_popup` / `found_sound` turn them off. Finds during a boss fight show after it.

**Boss fights.** While a boss health bar is on screen the overlay hides and will not open, so it never gets in the way
(`hide_in_boss_fights=false` turns this off).

**Graces, shops and cutscenes.** While you sit at a Site of Grace, use a shop or talk to an NPC, or watch a cutscene,
the overlay hides itself and the checklist closes, the same as the game's own HUD. It comes back when the game's HUD
does. `hide_in_menus=false` turns this off.

**Main menu.** On the title screen and loading screens (no character loaded) nothing else shows and the checklist will
not open. If a game update ever stops the overlay appearing at all, set `hide_on_main_menu=false`.

**Title screen.** The companion adds its name, version and author to the game's own version text in the bottom right
corner, on the line above "App Ver.", as other mods such as Seamless Co-op do. Turn it off with `title_credit=false`.
If a game update ever stops that working, the overlay draws the line in a corner instead (`title_credit_corner`).

**Small display size.** Long lines (a target's name and route, the nearest grace) wrap at the box's usual width, two
lines at most, ending in "..." if longer, so the box never grows across the screen.

**Auto-tracking status.** The top of the checklist says whether auto-tracking is on, and if not, why (no spoiler log,
the desktop app has the run, the game version is not supported, Easy Anti-Cheat). The small display warns too when it
is off for a reason you can fix. After a game update the patterns in `Data\signatures.json` may need updating; the
overlay says so instead of waiting forever.

## Runs, saves and backups

**Which run.** When the game starts, the overlay opens the run for the randomizer's newest seed, or carries on the run
you had if you re-randomized the same seed. Runs are the same files the desktop app uses
(`%LOCALAPPDATA%\ErRandoCompanion\Seeds`).

**With the desktop app.** If the app already has the run open, the overlay shows it read-only (it says so at the top of
the checklist): nothing is saved or auto-tracked there, the app's progress shows as the app saves, and the overlay takes
over as soon as the app closes the run. It works the same the other way round, so the two never overwrite each other.

**Backups.** A copy of each run is kept every 10 minutes of play and before it is deleted or restored over (the last 10
per run, in `Seeds\Backups`). The Runs tab lists them, deleted runs included, and puts one back; what was there is
backed up first, so a restore can be undone. The desktop app has the same list behind its Backups button.

**Game save.** Each backup also keeps a compressed copy of the game's own save (the one written last in
`%APPDATA%\EldenRing`: the randomizer's `.RD2`, or `.sl2` / `.co2`). Backups that have one say "+ game save". Put a save
back from the desktop app's Backups window, with Elden Ring closed; the save there now is kept as
`<save>.before-restore` first. A save holds every character on it, not just the run's.

**Run timer.** Each run counts its played time (only while a character is in the world, so menus and loading screens
do not count). The top of the checklist shows it with your pace in checks an hour; the small display shows the time too
(`show_timer=false` hides it). Whichever of the overlay and the desktop app owns the run does the counting.

**Deaths.** Counted from the game's own death count, by the area you died in. The first look after loading in is only a
starting point.

## Tabs

**Runs.** Pick what finishes the run: beat the game (the Elden Beast), beat the DLC (Promised Consort Radahn), defeat
some or all major bosses, find some or all key items, tick every check, or defeat bosses you tick yourself. With enemy
randomization a ticked boss counts when you defeat that boss wherever the randomizer put it; the overlay only ever shows
"defeated", never where a boss is. Only what this run's randomizer settings include counts. When the goal is met a
"Run complete!" notification shows and the timer stops. The Runs tab also lists finished runs, newest first, with time,
checks and pace, and marks the best time for each goal; Summary shows a finished run's numbers.

**Splits.** Each part of the goal gets a time when you reach it (each Great Rune for the Elden Beast, each chosen or
major boss, each key item, each quarter of all checks), compared step by step with your best finished run with the
same goal.

**Progress.** What's left in each region (open, skipped, locked, done), and the run in numbers: time, checks, bosses
defeated, deaths by place, the slowest stretch between checks, splits, and key items in the order you found them. With
3 or fewer open checks, the header and small display say which missing key items would open the most (logic only,
never where they are).

**Items.** The top lists each key or shortcut item you are still missing and how many locked checks it would open right
now, with their regions. It only asks the logic "what if I had it", so it never says where an item is.

**All.** Every check the randomizer could fill in this seed, not just the main checklist: every pickup, chest, enemy
drop, boss reward, quest reward and shop item (about 4,800 with the DLC). They are grouped by place, with done, total
and open counts; pick a place to see its checks. Open and locked follow the randomizer's own logic. Each check says
where it is and what the unrandomized game had there ("was Iron Whetblade"), never what it holds now. Most are ticked
automatically; the few the game keeps no flag for (unlimited shop stock, some drops) say "tick by hand". Ticks here
are kept apart from the Checks tab, so they do not change your goal or counts there. Drops that any enemy of a kind can give ("Dropped by any
Imp") are listed under "Enemy drops anywhere". Shop items that need an unlock (a bell bearing, a prayer book, a scroll)
open once that item shows up in the Found tab.

**Kills and drop luck.** The companion counts every enemy you kill, by kind (Progress tab, "Kills"). For each drop
under "Enemy drops anywhere" it shows how many enemies that could drop it you have killed, the chance each kill had,
and how likely it is that it still has not dropped. When that falls under 10% the drop is "overdue" (the All tab's
Overdue filter lists them), so you know whether you are just unlucky or farming the wrong enemy. A drop is ticked
automatically when its item arrives within a few minutes of killing an enemy that can drop it. Kills are counted from
the enemies' health, so only kills made while the companion is running count.

**Found.** Everything you pick up in a run (weapons, armour, talismans, ashes of war, spells, spirit ashes and other
items), newest first, with where it was and when, filterable by kind and searchable.

<details>
<summary>How the Found tab knows what you picked up</summary>

It reads each pickup's flag, so only pickups you have made ever show; pickups from before the first scan are marked
"Before tracking started". It needs the run's spoiler log (for the seed's regulation and the randomizer's slot
descriptions).

Random enemy drops have no flag, though unique drops (bosses, named enemies) do. Random drops are caught another way:
the inventory is compared every few seconds, and anything that arrives without a pickup to explain it is logged as
"Drop or other" with the map area you were in. It can also be a purchase or crafting; items taken from the storage
chest are not counted. Pickups and the inventory are checked every 3 seconds. If the log says the inventory could not
be found, drops are not logged in that game version.

</details>

## Files it writes

These go next to the DLL:

| File | What it holds |
| --- | --- |
| `ErRandoCompanionMod.ini` | Hotkeys, controller combos, text size, the two small display layouts. |
| `ErRandoCompanionMod.imgui.ini` | Where you left the window. |
| `ErRandoCompanionMod.log` | Problems, if any. Look here if the overlay never appears. |

## Licences

Elden Ring Randomizer Companion, Copyright (c) 2026 Moordoor, GPL-3.0 (see `LICENSE`): part of its game reader is
ported from SoulSplitter by Frank van der Stam (GPL-3.0). Memory layouts come from fromsoftware-rs by Vincent Swarte
(MIT/Apache, non-AI-training licences); the map markers follow Map for Goblins - DLL by VirusAlex (MIT; based on
Goblin-ERR by Gacsam); camera and cursor addresses are those published by The Grand Archives' Elden Ring table and the
Elden Ring practice tool (no code from either). hudhook, MinHook and Dear ImGui draw the overlay.
`THIRD_PARTY_NOTICES.md` lists everyone, and the `licenses` folder holds the licence texts.

Not affiliated with FromSoftware, Bandai Namco or the randomizer's author.
