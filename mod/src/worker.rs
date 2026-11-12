//! One background thread owns the tracker. The overlay draws from a snapshot and sends commands,
//! so a slow call (loading a seed, reading hundreds of flags) never stalls a frame.

use std::collections::HashSet;
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use serde_json::json;

use crate::log;
use crate::model::{AllCheck, AllPlaces, Backup, KillKind, BossChoice, Check, FinishedRun, FoundEntry, GoalKind, Item, Poll, RegionCount, Report, Setting, Summary, Unlock};
use crate::native::Native;

const POLL_EVERY: Duration = Duration::from_secs(1);

/// How many nearby checks each poll asks the tracker for; the overlay sets it from its settings.
static NEARBY: AtomicUsize = AtomicUsize::new(0);

/// Whether the Found tab is open, so the found list (thousands of entries) is only fetched while it is shown.
static FOUND_OPEN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Whether the Progress tab is open, so the open run's report follows it.
static PROGRESS_OPEN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Whether the All tab is open, and which of its places is expanded, so only that place's checks are fetched.
static ALL_OPEN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
static ALL_PLACE: Mutex<Option<String>> = Mutex::new(None);

/// Whether the Kills tab is open, so its list follows the kills while it shows.
static KILLS_OPEN: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

pub fn set_kills_open(open: bool) {
    KILLS_OPEN.store(open, Ordering::Relaxed);
}

pub fn set_all_open(open: bool) {
    ALL_OPEN.store(open, Ordering::Relaxed);
}

pub fn set_all_place(place: Option<String>) {
    *ALL_PLACE.lock().unwrap_or_else(|p| p.into_inner()) = place;
}

pub fn set_progress_open(open: bool) {
    PROGRESS_OPEN.store(open, Ordering::Relaxed);
}

pub fn set_found_open(open: bool) {
    FOUND_OPEN.store(open, Ordering::Relaxed);
}

pub fn set_nearby(count: usize) {
    NEARBY.store(count, Ordering::Relaxed);
}

pub enum Command {
    SetChecked(String, bool),
    SetTarget(String, Option<i32>),
    SetActiveTarget(i32),
    SetItem(String, i32),
    SetSetting(String, String),
    OpenSeed(String),
    OpenNewestSeed,
    RestoreBackup(String, String),
    SetGoal(String, Option<i32>),
    /// Reads the backups list, which means reading every backup file, so only when the Runs tab shows it.
    LoadBackups,
    ToggleGoalBoss(String),
    /// Reads the found list now (the Found tab has just opened).
    LoadFound,
    /// Puts a check aside for now, or back.
    ToggleSkip(String),
    /// Skips the nearest available check.
    SkipNearest,
    /// Reads a run's report: the open run (None), or a finished one by name.
    LoadReport(Option<String>),
    /// Ticks or unticks a check of the full list by hand.
    SetAllChecked(String, bool),
    /// Reads the full list's places, and the expanded place's checks.
    LoadAll,
    /// Reads the kills by kind, with their drops.
    LoadKills,
}

#[derive(Default)]
pub struct Snapshot {
    /// Why the tracker could not start at all; nothing else is filled in then.
    pub fatal: Option<String>,
    pub ready: bool,
    pub summary: Summary,
    pub checks: Vec<Check>,
    pub items: Vec<Item>,
    pub settings: Vec<Setting>,
    pub seeds: Vec<String>,
    /// Every run's backups, deleted runs included.
    pub backups: Vec<Backup>,
    /// The goals a run can have, read once at start.
    pub goal_kinds: Vec<GoalKind>,
    /// The bosses a "defeat chosen bosses" goal can name, read once at start.
    pub boss_choices: Vec<BossChoice>,
    /// What each missing item would open now; changes with every refresh.
    pub unlocks: Vec<Unlock>,
    /// Finished runs, read with the backups when the Runs tab opens.
    pub history: Vec<FinishedRun>,
    /// Everything picked up in this run, newest first; fetched while the Found tab is open.
    pub found_items: Vec<FoundEntry>,
    /// Each region's checks, refreshed with the lists.
    pub regions: Vec<RegionCount>,
    /// The open run's report while the Progress tab shows it, or a finished run's when asked for.
    pub report: Option<Report>,
    /// The full check list's places while the All tab is open.
    pub all_places: Option<AllPlaces>,
    /// The checks of one place of the full list, and which place they are.
    pub all_checks: Vec<AllCheck>,
    pub all_checks_place: Option<String>,
    /// Kills by kind with the drops each can give, while the Kills tab is open.
    pub kill_kinds: Option<Vec<KillKind>>,
    pub poll: Option<Poll>,
    /// The last command that failed, shown until dismissed.
    pub notice: Option<String>,
    /// Checks auto-tracking ticked, newest last, for the "found" popup. Only the last few are kept.
    pub found: Vec<Found>,
    /// Goes up whenever the lists are replaced, so the overlay can keep what it built from them until then.
    pub generation: u64,
}

/// A check auto-tracking just ticked. `number` counts up across the session, so the overlay can tell
/// which ones it has already shown.
#[derive(Clone)]
pub struct Found {
    pub number: u64,
    pub name: String,
    pub region: String,
    /// Checks that became available because of this find (on the last find of a batch).
    pub unlocked: Vec<String>,
}

#[derive(Clone)]
pub struct Shared(Arc<Mutex<Snapshot>>);

impl Shared {
    /// A panic on the worker must not take the overlay, and so the game, down with it.
    pub fn lock(&self) -> MutexGuard<'_, Snapshot> {
        self.0.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

pub fn start(directory: PathBuf) -> (Shared, Sender<Command>) {
    let shared = Shared(Arc::new(Mutex::new(Snapshot::default())));
    let (sender, receiver) = mpsc::channel();
    let worker = shared.clone();
    let started = std::thread::Builder::new()
        .name("ErRandoCompanion tracker".into())
        .spawn(move || run(directory, worker, receiver));
    if let Err(e) = started {
        fail(&shared, format!("Could not start the tracker thread: {e}"));
    }
    (shared, sender)
}

fn run(directory: PathBuf, shared: Shared, commands: Receiver<Command>) {
    let native = match Native::load(&directory.join("ErRandoCompanion.Native.dll")) {
        Ok(native) => native,
        Err(e) => return fail(&shared, e),
    };
    // Data and the Seeds folder default to where the DLLs sit; the randomizer folder is found from there.
    let mut options = json!({ "readOwnProcess": true });
    if let Some(folder) = randomizer_folder(&directory) {
        options["randomizerDirectory"] = json!(folder);
    }
    if let Err(e) = native.init::<Summary>(&options) {
        return fail(&shared, format!("The tracker could not start: {e}"));
    }
    if let Err(e) = native.call::<Summary>(json!({ "op": "openNewestSeed" })) {
        log::write(&format!("Kept the last run open: {e}"));
        shared.lock().notice = Some(format!("Could not open the randomizer's newest seed, so the last run is open. {e}"));
    }
    match native.call::<Vec<GoalKind>>(json!({ "op": "goalKinds" })) {
        Ok(kinds) => shared.lock().goal_kinds = kinds,
        Err(e) => log::write(&format!("Could not read the goals: {e}")),
    }
    match native.call::<Vec<BossChoice>>(json!({ "op": "bossChoices" })) {
        Ok(choices) => shared.lock().boss_choices = choices,
        Err(e) => log::write(&format!("Could not read the bosses: {e}")),
    }
    refresh(&native, &shared);

    let mut last_poll: Option<Instant> = None;
    let mut last_inventory: Option<bool> = None;
    let mut enemies_read: Option<bool> = None;
    let mut kills_logged = 0;
    let mut last_enemy_summary: Option<Instant> = None;
    loop {
        let wait = last_poll.map_or(Duration::ZERO, |at| POLL_EVERY.saturating_sub(at.elapsed()));
        match commands.recv_timeout(wait) {
            Ok(Command::LoadBackups) => load_backups(&native, &shared),
            Ok(Command::LoadFound) => load_found(&native, &shared),
            Ok(Command::LoadReport(name)) => load_report(&native, &shared, name),
            Ok(Command::LoadAll) => load_all(&native, &shared),
            Ok(Command::LoadKills) => load_kills(&native, &shared),
            Ok(command) => {
                let restored = matches!(command, Command::RestoreBackup(..));
                if let Err(e) = apply(&native, command) {
                    shared.lock().notice = Some(e);
                }
                refresh(&native, &shared);
                if restored {
                    load_backups(&native, &shared);
                }
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return,
        }
        if last_poll.map_or(true, |at| at.elapsed() >= POLL_EVERY) {
            last_poll = Some(Instant::now());
            match native.call::<Poll>(json!({ "op": "poll", "nearby": NEARBY.load(Ordering::Relaxed) })) {
                Ok(poll) => {
                    let changed = poll.changed;
                    // Said once each way in the log, so a game where drops cannot be seen is easy to tell.
                    if poll.inventory_readable.is_some() && poll.inventory_readable != last_inventory {
                        last_inventory = poll.inventory_readable;
                        log::write(if poll.inventory_readable == Some(true) {
                            "The inventory was found: drops and other received items are logged in the Found tab."
                        } else {
                            "The inventory could not be found in this game version, so random drops are not logged."
                        });
                    }
                    // Also once each way: whether the loaded enemies can be read, which kill counting needs.
                    let can_read = poll.enemies_loaded.map(|n| n > 0);
                    if can_read.is_some() && can_read != enemies_read {
                        enemies_read = can_read;
                        log::write(&match poll.enemies_loaded {
                            Some(n) if n > 0 => format!("Enemies can be read ({n} loaded): kills are counted."),
                            _ => "No enemies could be read here; if this stays so in places with enemies, kills are not counted in this game version.".to_string(),
                        });
                    }
                    // The first kill, and a line a minute while enemies are loaded: enough to tell from the log whether
                    // kills are being seen.
                    if poll.kills_counted > 0 && kills_logged == 0 {
                        log::write(&format!("First kill counted: {}.", poll.last_kill.as_deref().unwrap_or("an enemy")));
                    }
                    if let Some(loaded) = poll.enemies_loaded.filter(|&n| n > 0) {
                        if last_enemy_summary.map_or(true, |at| at.elapsed() >= Duration::from_secs(60)) {
                            last_enemy_summary = Some(Instant::now());
                            log::write(&format!(
                                "Enemies: {loaded} loaded, {} with no HP left; {} kills counted since starting.",
                                poll.enemies_dead.unwrap_or(0),
                                poll.kills_counted
                            ));
                        }
                    }
                    kills_logged = poll.kills_counted;
                    let before = {
                        let mut snapshot = shared.lock();
                        snapshot.poll = Some(poll);
                        changed.then(|| before(&snapshot))
                    };
                    if let Some(before) = before {
                        refresh(&native, &shared);
                        announce_found(&shared, &before);
                    }
                }
                Err(e) => shared.lock().notice = Some(e),
            }
        }
    }
}

fn apply(native: &Native, command: Command) -> Result<(), String> {
    let request = match command {
        Command::SetChecked(id, checked) => json!({ "op": "setChecked", "id": id, "checked": checked }),
        Command::SetTarget(id, number) => json!({ "op": "setTarget", "id": id, "number": number }),
        Command::SetActiveTarget(number) => json!({ "op": "setActiveTarget", "number": number }),
        Command::SetItem(name, count) => json!({ "op": "setItem", "name": name, "count": count }),
        Command::SetSetting(id, value) => json!({ "op": "setSetting", "id": id, "value": value }),
        Command::OpenSeed(name) => json!({ "op": "openSeed", "name": name }),
        Command::OpenNewestSeed => json!({ "op": "openNewestSeed" }),
        Command::LoadBackups => return Ok(()),
        Command::ToggleGoalBoss(id) => json!({ "op": "toggleGoalBoss", "id": id }),
        Command::LoadFound | Command::LoadReport(_) | Command::LoadAll | Command::LoadKills => return Ok(()),
        Command::SetAllChecked(id, checked) => json!({ "op": "setAllChecked", "id": id, "checked": checked }),
        Command::ToggleSkip(id) => json!({ "op": "toggleSkip", "id": id }),
        Command::SkipNearest => json!({ "op": "skipNearest" }),
        Command::RestoreBackup(name, id) => json!({ "op": "restoreBackup", "name": name, "id": id }),
        Command::SetGoal(kind, count) => json!({ "op": "setGoal", "kind": kind, "count": count }),
    };
    native.call::<serde_json::Value>(request).map(|_| ())
}

/// Reads everything the overlay shows, then swaps it in at once.
fn refresh(native: &Native, shared: &Shared) {
    let read = || -> Result<_, String> {
        Ok((
            native.call::<Summary>(json!({ "op": "summary" }))?,
            native.call::<Vec<Check>>(json!({ "op": "checks", "filter": "all" }))?,
            native.call::<Vec<Item>>(json!({ "op": "items" }))?,
            native.call::<Vec<Setting>>(json!({ "op": "settings" }))?,
            native.call::<Vec<String>>(json!({ "op": "listSeeds" }))?,
            native.call::<Vec<Unlock>>(json!({ "op": "unlocks" }))?,
            native.call::<Vec<RegionCount>>(json!({ "op": "regions" }))?,
        ))
    };
    match read() {
        Ok((summary, checks, items, settings, seeds, unlocks, regions)) => {
            crate::map_markers::update(&summary.targets);
            let mut snapshot = shared.lock();
            snapshot.summary = summary;
            snapshot.checks = checks;
            snapshot.items = items;
            snapshot.settings = settings;
            snapshot.unlocks = unlocks;
            snapshot.regions = regions;
            // The open run's report follows it while shown; a finished run's stays as it is.
            let follow = snapshot.report.as_ref().is_some_and(|r| !r.finished || r.seed_name == snapshot.summary.seed_name);
            drop(snapshot);
            if FOUND_OPEN.load(Ordering::Relaxed) {
                load_found(native, shared);
            }
            if follow && PROGRESS_OPEN.load(Ordering::Relaxed) {
                load_report(native, shared, None);
            }
            if ALL_OPEN.load(Ordering::Relaxed) {
                load_all(native, shared);
            }
            if KILLS_OPEN.load(Ordering::Relaxed) {
                load_kills(native, shared);
            }
            let mut snapshot = shared.lock();
            snapshot.seeds = seeds;
            snapshot.ready = true;
            snapshot.generation += 1;
        }
        Err(e) => shared.lock().notice = Some(e),
    }
}

fn load_report(native: &Native, shared: &Shared, name: Option<String>) {
    match native.call::<Report>(json!({ "op": "report", "name": name })) {
        Ok(report) => {
            let mut snapshot = shared.lock();
            snapshot.report = Some(report);
            snapshot.generation += 1;
        }
        Err(e) => shared.lock().notice = Some(e),
    }
}

fn load_kills(native: &Native, shared: &Shared) {
    match native.call::<Vec<KillKind>>(json!({ "op": "kills" })) {
        Ok(kinds) => {
            let mut snapshot = shared.lock();
            snapshot.kill_kinds = Some(kinds);
            snapshot.generation += 1;
        }
        Err(e) => shared.lock().notice = Some(e),
    }
}

fn load_all(native: &Native, shared: &Shared) {
    let place = ALL_PLACE.lock().unwrap_or_else(|p| p.into_inner()).clone();
    let read = || -> Result<_, String> {
        let places = native.call::<AllPlaces>(json!({ "op": "allPlaces" }))?;
        let checks = match &place {
            Some(name) => native.call::<Vec<AllCheck>>(json!({ "op": "allChecks", "place": name }))?,
            None => Vec::new(),
        };
        Ok((places, checks))
    };
    match read() {
        Ok((places, checks)) => {
            let mut snapshot = shared.lock();
            snapshot.all_places = Some(places);
            snapshot.all_checks = checks;
            snapshot.all_checks_place = place;
            snapshot.generation += 1;
        }
        Err(e) => shared.lock().notice = Some(e),
    }
}

fn load_found(native: &Native, shared: &Shared) {
    match native.call::<Vec<FoundEntry>>(json!({ "op": "found" })) {
        Ok(found) => {
            let mut snapshot = shared.lock();
            snapshot.found_items = found;
            snapshot.generation += 1;
        }
        Err(e) => shared.lock().notice = Some(e),
    }
}

/// What the Runs tab shows from disk: every run's backups and the finished runs. Both read every save, so only
/// when that tab opens.
fn load_backups(native: &Native, shared: &Shared) {
    match native.call::<Vec<Backup>>(json!({ "op": "backups" })) {
        Ok(backups) => shared.lock().backups = backups,
        Err(e) => shared.lock().notice = Some(e),
    }
    match native.call::<Vec<FinishedRun>>(json!({ "op": "history" })) {
        Ok(history) => shared.lock().history = history,
        Err(e) => shared.lock().notice = Some(e),
    }
}

/// The open run, and which of its checks were ticked and which available, before a refresh.
struct Before {
    seed: String,
    checked: HashSet<String>,
    available: HashSet<String>,
}

fn before(snapshot: &Snapshot) -> Before {
    let with = |status: &str| snapshot.checks.iter().filter(|c| c.status == status).map(|c| c.id.clone()).collect();
    Before { seed: snapshot.summary.seed_name.clone(), checked: with("checked"), available: with("available") }
}

/// Adds the checks auto-tracking just ticked to the "found" list, if the same run is still open, with
/// the checks they made available on the last of them.
fn announce_found(shared: &Shared, before: &Before) {
    let mut snapshot = shared.lock();
    if snapshot.summary.seed_name != before.seed {
        return;
    }
    let new: Vec<(String, String)> = snapshot
        .checks
        .iter()
        .filter(|c| c.status == "checked" && !before.checked.contains(&c.id))
        .map(|c| (c.name.clone(), c.region.clone()))
        .collect();
    let unlocked: Vec<String> = snapshot
        .checks
        .iter()
        .filter(|c| c.status == "available" && !before.available.contains(&c.id) && !before.checked.contains(&c.id))
        .map(|c| c.name.clone())
        .collect();
    let count = new.len();
    for (i, (name, region)) in new.into_iter().enumerate() {
        let number = snapshot.found.last().map_or(1, |f| f.number + 1);
        let unlocked = if i + 1 == count { unlocked.clone() } else { Vec::new() };
        snapshot.found.push(Found { number, name, region, unlocked });
    }
    let excess = snapshot.found.len().saturating_sub(20);
    snapshot.found.drain(..excess);
}

/// The randomizer folder holds spoiler_logs. The DLLs may sit in it or in a folder inside it.
fn randomizer_folder(directory: &Path) -> Option<PathBuf> {
    directory.ancestors().take(3).find(|folder| folder.join("spoiler_logs").is_dir()).map(Path::to_path_buf)
}

fn fail(shared: &Shared, message: String) {
    log::write(&message);
    shared.lock().fatal = Some(message);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn announces_new_finds_and_what_they_made_available() {
        let check = |id: &str, status: &str| Check { id: id.into(), name: id.to_uppercase(), region: "Limgrave".into(), status: status.into(), ..Default::default() };
        let shared = Shared(Arc::new(Mutex::new(Snapshot::default())));
        {
            let mut s = shared.lock();
            s.summary.seed_name = "run".into();
            s.checks = vec![check("a", "checked"), check("b", "available"), check("c", "locked"), check("d", "locked")];
        }
        let earlier = before(&shared.lock());
        // b was found, and that opened c up; d stays locked.
        shared.lock().checks = vec![check("a", "checked"), check("b", "checked"), check("c", "available"), check("d", "locked")];
        announce_found(&shared, &earlier);
        let found: Vec<(u64, String, Vec<String>)> = shared.lock().found.iter().map(|f| (f.number, f.name.clone(), f.unlocked.clone())).collect();
        assert_eq!(found, [(1, "B".to_string(), vec!["C".to_string()])]);

        // Another run opened in between: nothing is announced.
        shared.lock().summary.seed_name = "other".into();
        shared.lock().checks[3].status = "checked".into();
        announce_found(&shared, &earlier);
        assert_eq!(shared.lock().found.len(), 1);
    }

    fn wait_for(shared: &Shared, what: &str, done: impl Fn(&Snapshot) -> bool) {
        let started = Instant::now();
        while started.elapsed() < Duration::from_secs(60) {
            {
                let snapshot = shared.lock();
                assert!(snapshot.fatal.is_none(), "tracker failed: {:?}", snapshot.fatal);
                if done(&snapshot) {
                    return;
                }
            }
            std::thread::sleep(Duration::from_millis(50));
        }
        panic!("timed out waiting for {what}");
    }

    /// Runs against a staged mod folder (set ER_MOD_TEST_DIR) holding ErRandoCompanion.Native.dll and Data,
    /// with ER_COMPANION_SEEDS_DIR pointing at a throwaway copy of the Seeds folder.
    #[test]
    #[ignore = "needs a staged mod folder"]
    fn drives_the_tracker_like_the_overlay_does() {
        let directory = PathBuf::from(std::env::var("ER_MOD_TEST_DIR").expect("set ER_MOD_TEST_DIR"));
        assert!(std::env::var("ER_COMPANION_SEEDS_DIR").is_ok(), "use a copy of the Seeds folder");
        let (shared, commands) = start(directory);

        wait_for(&shared, "the first refresh", |s| s.ready && s.poll.is_some());
        let (id, before) = {
            let s = shared.lock();
            println!(
                "open: {} {}/{} checked, {} available, {} runs, poll: {}, notice: {}",
                s.summary.seed_name,
                s.summary.checked,
                s.summary.total,
                s.summary.available,
                s.seeds.len(),
                s.poll.as_ref().map_or("", |p| p.status.as_str()),
                s.notice.as_deref().unwrap_or("none")
            );
            assert_eq!(s.checks.len() as i32, s.summary.total);
            assert!(!s.items.is_empty() && !s.settings.is_empty());
            let first = s.checks.iter().find(|c| c.status == "available").expect("an available check");
            (first.id.clone(), s.summary.checked)
        };

        commands.send(Command::SetTarget(id.clone(), Some(2))).unwrap();
        // The run may already have other targets, one of them the active one.
        wait_for(&shared, "the target", |s| s.summary.targets.iter().any(|t| t.number == 2 && t.id == id));
        commands.send(Command::SetChecked(id.clone(), true)).unwrap();
        wait_for(&shared, "the check", |s| s.summary.checked == before + 1 && !s.summary.targets.iter().any(|t| t.id == id));
        commands.send(Command::SetChecked(id.clone(), false)).unwrap();
        wait_for(&shared, "the uncheck", |s| s.summary.checked == before);

        // The full list: its places, then one place's checks, ticked and unticked by hand.
        set_all_open(true);
        commands.send(Command::LoadAll).unwrap();
        wait_for(&shared, "the full list", |s| s.all_places.is_some());
        let place = {
            let s = shared.lock();
            let all = s.all_places.as_ref().unwrap();
            println!("full list: {} / {} checked, {} open, {} places, message: {:?}", all.done, all.total, all.open, all.places.len(), all.message);
            assert!(all.message.is_none() && all.total > 3000);
            all.places.iter().find(|p| p.open > 0).expect("a place with open checks").clone()
        };
        set_all_place(Some(place.name.clone()));
        commands.send(Command::LoadAll).unwrap();
        wait_for(&shared, "a place's checks", |s| s.all_checks_place.as_deref() == Some(place.name.as_str()));
        let check = {
            let s = shared.lock();
            assert_eq!(s.all_checks.len() as i32, place.total);
            s.all_checks.iter().find(|c| c.status == "available").unwrap().clone()
        };
        println!("{}: {} checks; ticking '{}' ({}, originally {})", place.name, place.total, check.description, check.kind, check.replaces);
        commands.send(Command::SetAllChecked(check.id.clone(), true)).unwrap();
        wait_for(&shared, "the full-list tick", |s| {
            s.all_checks.iter().any(|c| c.id == check.id && c.status == "checked")
                && s.all_places.as_ref().is_some_and(|a| a.places.iter().any(|p| p.name == place.name && p.done == place.done + 1))
        });
        commands.send(Command::SetAllChecked(check.id.clone(), false)).unwrap();
        wait_for(&shared, "the full-list untick", |s| s.all_checks.iter().any(|c| c.id == check.id && c.status != "checked"));
        set_all_open(false);

        commands.send(Command::SetTarget("nowhere".into(), Some(1))).unwrap();
        wait_for(&shared, "an error notice", |s| s.notice.as_deref().is_some_and(|n| n.contains("nowhere")));
        println!("bad command notice: {}", shared.lock().notice.as_deref().unwrap_or(""));
    }
}
