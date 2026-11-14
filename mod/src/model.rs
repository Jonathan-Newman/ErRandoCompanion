//! The tracker's replies, as defined in Native/Contracts.cs. None of them carry item placements.

use serde::Deserialize;

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Target {
    pub number: i32,
    pub id: String,
    pub name: String,
    pub active: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Spoiler {
    pub loaded: bool,
    pub seed: Option<String>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Summary {
    pub seed_name: String,
    pub spoiler: Spoiler,
    pub checked: i32,
    pub total: i32,
    pub available: i32,
    pub targets: Vec<Target>,
    pub warning: Option<String>,
    pub problem: Option<String>,
    /// Time played on the run, in seconds, and whether its goal has been met (the clock stops then).
    pub played_seconds: f64,
    pub finished: bool,
    /// The run's goal and how far along it is; None for no goal.
    pub goal: Option<Goal>,
    /// Available checks that are not skipped, and how many are skipped.
    pub open: i32,
    pub skipped: i32,
    /// So few open checks that the run is close to stuck.
    pub dead_end: bool,
    pub deaths: i32,
    /// The goal's steps so far, each beside the best finished run's time at the same step.
    pub splits: Vec<Split>,
}

/// A step toward the goal. `seconds` is negative when it was reached before timing started.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Split {
    pub number: i32,
    pub name: String,
    pub seconds: f64,
    pub best: Option<f64>,
}

/// A place in the full check list (every slot the randomizer could fill), with its counts.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AllPlace {
    pub name: String,
    pub done: i32,
    pub total: i32,
    pub open: i32,
}

/// The full check list's places and totals; `message` says why there is no list.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AllPlaces {
    pub message: Option<String>,
    pub done: i32,
    pub total: i32,
    pub open: i32,
    pub by_hand: i32,
    pub places: Vec<AllPlace>,
    /// Drops that would usually have dropped by now.
    pub overdue: i32,
}

/// The overlay's update status: this version, the newest on GitHub if known, and what happened.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct UpdateInfo {
    pub current: String,
    pub latest: Option<String>,
    pub available: bool,
    pub page: Option<String>,
    pub message: Option<String>,
    pub installed: bool,
    pub check_enabled: bool,
}

/// An enemy kind killed in this run, how many, and the drops it can give with their odds.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct KillKind {
    pub kind: String,
    pub kills: i32,
    pub drops: Vec<AllCheck>,
}

/// One check of the full list: where it is and what the unrandomized game had there, never what it holds now.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AllCheck {
    pub id: String,
    pub description: String,
    pub kind: String,
    pub replaces: String,
    pub missable: bool,
    pub by_hand: bool,
    pub status: String,
    /// For a drop of every enemy of a kind: kills and odds.
    pub odds: Option<String>,
    pub overdue: bool,
}

/// One region's checks; open means available and not skipped.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RegionCount {
    pub region: String,
    pub open: i32,
    pub skipped: i32,
    pub locked: i32,
    pub done: i32,
    pub total: i32,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Timed {
    pub name: String,
    pub seconds: f64,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PlaceCount {
    pub place: String,
    pub count: i32,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Stretch {
    pub from: f64,
    pub to: f64,
    pub ended_with: String,
}

/// A run in numbers: what the player did, never where items are.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Report {
    pub seed_name: String,
    pub goal: String,
    pub finished: bool,
    pub played_seconds: f64,
    pub checked: i32,
    pub total: i32,
    pub bosses_defeated: i32,
    pub deaths: i32,
    pub death_places: Vec<PlaceCount>,
    pub slowest: Option<Stretch>,
    pub key_items: Vec<Timed>,
    pub splits: Vec<Split>,
    /// Enemies killed, by kind, most first.
    pub kills: Vec<PlaceCount>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Goal {
    pub kind: String,
    pub count: Option<i32>,
    pub description: String,
    pub done: i32,
    pub needed: i32,
    pub met: bool,
    /// For "defeat chosen bosses": each chosen boss and whether it is defeated (never where it is).
    pub bosses: Vec<GoalBoss>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GoalBoss {
    pub id: String,
    pub name: String,
    pub defeated: bool,
}

/// A missing item and how many locked checks it would open now (logic only, no item places).
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Unlock {
    pub item: String,
    pub opens: i32,
    pub regions: Vec<String>,
}

/// Something picked up in this run. `at` is None for pickups made before tracking started.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FoundEntry {
    pub items: Vec<String>,
    pub kinds: Vec<String>,
    pub r#where: String,
    pub map: String,
    pub at: Option<String>,
    pub played_time: Option<String>,
    /// "" for a pickup in the world; "Drop or other" for something seen arriving in the inventory.
    pub source: String,
}

/// A finished run, for the history list.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FinishedRun {
    pub seed_name: String,
    pub goal: String,
    pub finished_at: String,
    pub played_seconds: f64,
    pub checked: i32,
    pub per_hour: Option<f64>,
    pub best: bool,
    pub deaths: i32,
}

/// A boss that can be chosen for the goal.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct BossChoice {
    pub id: String,
    pub name: String,
    pub region: String,
    pub major: bool,
}

/// A goal the player can pick; `counts` says whether it takes a number.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GoalKind {
    pub kind: String,
    pub name: String,
    pub counts: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Check {
    pub id: String,
    pub name: String,
    pub region: String,
    pub status: String,
    pub details: String,
    pub target: Option<i32>,
    /// Put aside for now by the player.
    pub skipped: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Item {
    pub name: String,
    pub category: String,
    pub count: i32,
    pub max: i32,
    pub relevant: bool,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Setting {
    pub id: String,
    pub name: String,
    #[serde(rename = "type")]
    pub kind: String,
    pub value: String,
    pub options: Vec<String>,
    pub option_names: Vec<String>,
    pub max: i32,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Guide {
    pub target_name: String,
    pub mode: String,
    pub dx: f64,
    pub dz: f64,
    pub distance: f64,
    /// The found Site of Grace closest to the target, and how far it is from the target.
    pub grace: Option<String>,
    pub grace_distance: Option<f64>,
    /// The best route: warp to this found grace first (None to walk from here), then walk this far.
    pub warp_to: Option<String>,
    pub walk: Option<f64>,
    /// How far above (positive) or below the player the target is, when known.
    pub rise: Option<f64>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Poll {
    pub status: String,
    pub message: String,
    pub changed: bool,
    pub guide: Option<Guide>,
    pub nearby: Vec<Nearby>,
    /// Where the player is; None on the title screen and during loading.
    pub player: Option<serde_json::Value>,
    /// Whether auto-tracking works: on, waiting, noLog, readOnly, unsupported, blocked or unavailable.
    pub tracking: String,
    pub tracking_message: String,
    /// Some(false) when this game version's player position cannot be found at all.
    pub position_supported: Option<bool>,
    /// The run's played time, sent every second (the summary's only updates when something changes).
    pub played_seconds: f64,
    /// Whether the inventory could be read at the last look (for drops); None before the first look.
    pub inventory_readable: Option<bool>,
    /// How many enemies were loaded at the last look, for counting kills; None when they could not be read.
    pub enemies_loaded: Option<i32>,
    /// How many of those had no HP left.
    pub enemies_dead: Option<i32>,
    /// Kills counted since the tracker started, and the last kind killed.
    pub kills_counted: i32,
    pub last_kill: Option<String>,
}

/// A saved copy of a run, from the tracker's backups.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Backup {
    pub seed_name: String,
    pub id: String,
    pub saved_at: String,
    pub checked: i32,
    /// Whether the game's save was copied with it (put back from the desktop app, with the game closed).
    pub has_game_save: bool,
}

/// An available check near the player, nearest first.
#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Nearby {
    pub id: String,
    pub name: String,
    pub mode: String,
    pub dx: f64,
    pub dz: f64,
    pub distance: f64,
}
