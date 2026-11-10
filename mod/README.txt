ErRandoCompanion in-game overlay
================================

The checklist from the ErRandoCompanion desktop app, drawn inside Elden Ring. It reads the
game to tick checks automatically, and it never shows where items are.

Install
-------
1. Copy this whole ErRandoCompanion folder into your randomizer folder, so you have
     ...\randomizer\ErRandoCompanion\ErRandoCompanionMod.dll
     ...\randomizer\ErRandoCompanion\ErRandoCompanion.Native.dll
     ...\randomizer\ErRandoCompanion\Data\
2. In the randomizer program, click "All dll mod" and add ErRandoCompanionMod.dll.
   The randomizer remembers it and loads it every time you click "Launch Elden Ring".

   With a manual Mod Engine 2 setup instead, add it to external_dlls in config_eldenring.toml:
     external_dlls = [ "randomizer\\ErRandoCompanion\\ErRandoCompanionMod.dll" ]
   With me3, add it to your profile:
     [[natives]]
     path = "randomizer/ErRandoCompanion/ErRandoCompanionMod.dll"

Use
---
F9            open or close the checklist (change it in ErRandoCompanionMod.ini)
F7            skip the nearest available check for now (it leaves the open counts and the nearest list;
              undo it with right-click, or Y, on the check)
F6            swap the small display between its Play and Plan layouts (pick what each shows in Settings)
F10           hide everything the overlay draws at once, and again to bring it back (for recording);
              F9 also brings it back. These keys, and controller combos for them, are in the .ini
Right-click   a check to make it target 1 to 5; the Targets tab picks which one the guide follows
Top right     a small display of your counts and the active target while the checklist is closed
Clicking a check's box ticks it by hand; clicking its name makes it a target, or clears it if it is one.
Game map      targets 1-5 get the companion's own gold numbered badge over the game's world map, on
              whichever map is showing (surface, underground, Land of Shadow); the active target's
              badge pulses. map_icons=false turns the badges off.
              The game's own map also gets a hoverable entry per target, named "Target N: check"
              (map_markers). Pick which built-in icon sits under the badge in the Settings tab, or with
              map_icon in the ini. Do not use this with Map for Goblins, which also rewrites that table.
Nearby        the small display also lists the nearest available checks that are not targets yet, each
              with a small arrow and its distance (nearby_checks, 0 to 10; 0 hides the list).
Check found   when auto-tracking ticks a check, a Steam-style notification slides up in the bottom-right
              corner naming it, its region, and any checks it made available, with a chime. Put your
              The Settings tab picks the sound (Chime, Bell, Sparkle, the Windows notification, or your
              own ErRandoCompanionMod.wav beside the DLL), its volume, and has a Test button.
              found_popup / found_sound turn them off. Finds during a boss fight show after it.
Main menu     on the title screen and loading screens (no character loaded) nothing shows and the
              checklist will not open. If a game update ever stops the overlay appearing at all, set
              hide_on_main_menu=false.
Boss fights   while a boss health bar is on screen, the overlay hides and will not open, so it never
              gets in the way of a fight (hide_in_boss_fights=false turns this off).
Beacon        the game's own beacon (column of light, also on the compass) goes on the active target,
              for targets on the surface map. If you had placed a beacon yourself it comes back when
              the target is cleared; moving the beacon yourself is respected until the target changes.
              Turn it off in Settings or with beacon_on_target=false.
Gold arrow    points from where the camera faces toward the active target (straight up = ahead).
              Turn it off with guide_arrow=false or in the Settings tab, which also shows which way
              the camera faces so you can compare it with the game's compass.

While the checklist is open the game shows its own mouse cursor, and your character and camera ignore
the keyboard, mouse and controller.

Controller
  L3+R3 (click both sticks)   open or close the checklist (pad_toggle in ErRandoCompanionMod.ini)
  D-pad or left stick         move between rows
  A                           make the selected check a target (the first free number), or stop
                              tracking it if it already is one; also opens a region or presses a button
  Hold A                      tick a check by hand (checks are normally ticked automatically)
  B                           back out of a menu or list; at the top of the panel, close it
  Y                           pick which target number (1-5) the selected check gets
  LB / RB                     previous or next tab
  Right stick                 scroll
  The search box needs a keyboard; the filter above the list works with the controller.
  The hints on screen follow whichever you used last: controller buttons or keyboard keys.

The DLLs can also sit straight in the randomizer folder; the overlay finds spoiler_logs either way.

When the game starts, the overlay opens the run for the randomizer's newest seed, or carries on the
run you had if you re-randomized the same seed. Runs are the same files the desktop app uses
(%LOCALAPPDATA%\ErRandoCompanion\Seeds). If the desktop app already has the run open, the overlay
shows it read-only (it says so at the top of the checklist): nothing is saved or auto-tracked there,
the app's progress shows as the app saves, and the overlay takes over as soon as the app closes the
run. It works the same the other way round, so the two can never overwrite each other.

Game save: each run backup also keeps a compressed copy of the game's own save (the one written last
in %APPDATA%\EldenRing: the randomizer's .RD2, or .sl2 / .co2). Backups that have one say "+ game save".
Put a save back from the desktop app's Backups window, with Elden Ring closed; the save there now is kept
as <save>.before-restore first. A save holds every character on it, not just the run's.

Backups: a copy of each run is kept every 10 minutes of play and before it is deleted or restored
over (the last 10 per run, in Seeds\Backups). The Runs tab lists them, deleted runs included, and
puts one back; what was there is backed up first, so a restore can be undone. The desktop app has
the same list behind its Backups button.

Mods only run with Easy Anti-Cheat off, which launching through the randomizer already does.

Progress tab: what's left in each region (open, skipped, locked, done), and the run in numbers: time,
checks, bosses defeated, deaths by place, the slowest stretch between checks, splits, and key items in
the order you found them. With 3 or fewer open checks, the header and small display say which missing
key items would open the most (logic only, never where they are). In the Runs tab, Summary shows the
same for a finished run.

Splits: each part of the goal gets a time when you reach it (each Great Rune for the Elden Beast, each
chosen or major boss, each key item, each quarter of all checks), compared step by step with your best
finished run with the same goal.

Route: the target line picks the shorter way: walking from where you are, or warping to a Site of
Grace you have found and walking from there ("warp to Church of Elleh, then 300 m"). A warp counts as
250 m, for the menus and loading. A check inside a dungeon is reached through its entrance, then the
walk inside; a target in the other realm through a grace you have found there. When walking, it also
says how far up or down the target is. Each walk is a straight line between those points, so cliffs and
rivers are not known.

Nearest grace: under the target line, the Site of Grace you have found that is closest to the active
target, so you know where to warp. Graces you have not found are never suggested.

Deaths: counted from the game's own death count, by the area you died in. The first look after loading
in is only a starting point.

Files it writes here
--------------------
ErRandoCompanionMod.ini         hotkeys, controller combos, text size, the two small display layouts
ErRandoCompanionMod.imgui.ini   where you left the window
ErRandoCompanionMod.log         problems, if any; look here if the overlay never appears

Run goal: in the Runs tab pick what finishes the run: beat the game (the Elden Beast), beat the DLC
(Promised Consort Radahn), defeat some or all major bosses, find some or all key items, or tick
every check, or defeat bosses you tick yourself. With enemy randomization, a ticked boss counts when
you defeat that boss wherever the randomizer put it (from the spoiler log's boss placements); the
overlay only ever shows "defeated", never where a boss is. Only what this run's randomizer settings
include counts. Progress shows at the top of
the checklist and in the small display; when it is met a "Run complete!" notification shows and the
run's timer stops. The desktop app has the same in its Run goal tab.

Found items: the Found tab logs everything you pick up in a run (weapons, armour, talismans, ashes of
war, spells, spirit ashes and other items), newest first, with where it was and when, filterable by
kind and searchable. It reads each pickup's flag, so only pickups you have made ever show; pickups from
before the first scan are marked "Before tracking started". It needs the run's spoiler log (for the
seed's regulation and the randomizer's slot descriptions). Random enemy drops cannot be seen, as the game
keeps no flag for them; unique drops (bosses, named enemies) do show. Random drops are caught another
way: the inventory is compared every few seconds, and anything that arrives without a pickup to explain
it is logged as "Drop or other" with the map area you were in (it can also be a purchase or crafting;
items taken from the storage chest are not counted). The first look after loading in is only a starting
point. Pickups and the inventory are checked every 3 seconds. If the log says the inventory could not
be found, drops are not logged in that game version. The desktop app has a Found tab too.

What unlocks what: the top of the Items tab lists each key or shortcut item you are still missing and
how many locked checks it would open right now, with their regions. It only asks the logic "what if I
had it", so it never says where an item is.

Run history: the Runs tab lists finished runs (a run finishes when its goal is met), newest first, with
the time, checks and pace, and marks the best time for each goal. The desktop app has a History tab.

Run timer: each run counts its played time (only while a character is in the world, so menus and
loading screens do not count) and saves it with the run. The top of the checklist shows it with
your pace in checks an hour; the small display shows the time too (show_timer=false hides it).
Whichever of the overlay and the desktop app owns the run does the counting.

Auto-tracking status: the top of the checklist says whether auto-tracking is on, and if not, why
(no spoiler log, the desktop app has the run, the game version is not supported, Easy Anti-Cheat).
The small display shows a warning too when it is off for a reason you can fix. After a game update
the patterns in Data\signatures.json may need updating; the overlay says so instead of waiting forever.

Title screen: on the title screen the companion shows its name, version and author in a corner. Turn
it off with title_credit=false in ErRandoCompanionMod.ini, or move it with title_credit_corner
(top-left, top-right, bottom-left or bottom-right).

Licences
--------
Elden Ring Randomizer Companion, Copyright (c) 2026 Moordoor, GPL-3.0 (see LICENSE): part of its game
reader is ported from SoulSplitter by Frank van der Stam (GPL-3.0). Memory layouts come from
fromsoftware-rs by Vincent Swarte (MIT/Apache, non-AI-training licences); the map markers follow Map
for Goblins - DLL by VirusAlex (MIT; based on Goblin-ERR by Gacsam); camera and cursor addresses are
those published by The Grand Archives' Elden Ring table and the Elden Ring practice tool (no code from
either). hudhook, MinHook and Dear ImGui draw the overlay. THIRD_PARTY_NOTICES.md lists everyone, and
the licences folder holds the licence texts. Not affiliated with FromSoftware, Bandai Namco or the
randomizer's author.
