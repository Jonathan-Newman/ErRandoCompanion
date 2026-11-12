//! The in-game panel, drawn by Dear ImGui inside the game's own frames (hudhook's DX12 hook).
//! It shows only what the desktop app shows: check names, statuses and counts, never item placements.

use std::sync::atomic::{AtomicBool, Ordering};
use std::path::PathBuf;
use std::sync::mpsc::Sender;
use std::time::Instant;

use hudhook::imgui::{
    BackendFlags, Condition, ConfigFlags, Context, FontConfig, FontGlyphRanges, FontSource, Io, Key, MouseButton, StyleColor,
    TabItem, TabItemFlags, TreeNodeFlags, Ui, WindowFlags,
};
use hudhook::{ImguiRenderLoop, MessageFilter, RenderContext};
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::Input::KeyboardAndMouse::GetAsyncKeyState;
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};

use crate::config::{pad, Config, LAYOUT_NAMES};
use crate::{camera, game_input};
use crate::model::Check;
use crate::worker::{Command, Shared, Snapshot};

const GREEN: [f32; 4] = [0.45, 0.85, 0.45, 1.0];
const GOLD: [f32; 4] = [1.0, 0.82, 0.35, 1.0];
const GREY: [f32; 4] = [0.6, 0.6, 0.6, 1.0];
const RED: [f32; 4] = [1.0, 0.45, 0.4, 1.0];
const YELLOW: [f32; 4] = [1.0, 0.85, 0.3, 1.0];
const DETAILS: [f32; 4] = [0.62, 0.64, 0.68, 1.0];

const FILTERS: [&str; 4] = ["Available", "Locked", "Checked", "All"];
const TABS: [&str; 9] = ["Checks", "Targets", "Items", "Randomizer", "Settings", "Runs", "Found", "Progress", "All"];
const PROGRESS_TAB: usize = 7;
const ALL_TAB: usize = 8;
const ALL_FILTERS: [&str; 5] = ["Open", "Locked", "Checked", "Overdue", "All"];
/// Where the full list puts drops any enemy of a kind gives; the only place with overdue drops.
const ANY_AREA: &str = "Enemy drops anywhere";
const ALL_KINDS: [&str; 7] = ["All", "Pickup", "Chest", "Enemy drop", "Boss", "Quest", "Shop"];
const FOUND_KINDS: [&str; 9] = ["All", "Weapons", "Armour", "Talismans", "Ashes of War", "Spells", "Spirit Ashes", "Items", "Drop or other"];

/// Stick travel ignored around the centre, as XInput suggests for the left stick.
const DEAD_ZONE: f32 = 7849.0;

/// How long a "check found" popup stays up, in seconds.
const TOAST_SECONDS: f32 = 5.0;

/// A "check found" line, and when it first went on screen (None while it waits out a boss fight).
struct Toast {
    /// "Check found", or "Run complete!".
    title: String,
    name: String,
    region: String,
    /// Checks this find made available.
    unlocked: Vec<String>,
    shown: Option<Instant>,
    /// No sound: for the overlay's own notes (a skip, a layout change), not finds.
    quiet: bool,
}

/// "A, B and 3 more" for the "Now available" line, naming as many as fit in `room` pixels; None when the find
/// opened nothing up.
fn unlocked_text(ui: &Ui, names: &[String], room: f32) -> Option<String> {
    if names.is_empty() {
        return None;
    }
    let say = |named: usize| {
        let rest = names.len() - named;
        let list = match &names[..named] {
            [one] if rest == 0 => one.clone(),
            [one, two] if rest == 0 => format!("{one} and {two}"),
            shown if rest == 0 => format!("{} and {}", shown[..shown.len() - 1].join(", "), shown[shown.len() - 1]),
            shown => format!("{} and {rest} more", shown.join(", ")),
        };
        list
    };
    let text = (1..=names.len()).rev().map(say).find(|text| ui.calc_text_size(text)[0] <= room).unwrap_or_else(|| say(1));
    Some(fit(ui, &text, room))
}

/// What goes inside the companion's round gold badge.
enum Badge {
    Tick,
    Number(i32),
}

/// The companion's icon: a gold disc with a dark rim and a dark tick or number. Used on the map and in
/// the "check found" notification. `alpha` fades the whole badge.
fn draw_badge(ui: &Ui, draw: &hudhook::imgui::DrawListMut, centre: [f32; 2], radius: f32, content: Badge, alpha: f32) {
    let [x, y] = centre;
    let ink = [0.12, 0.08, 0.02, alpha];
    draw.add_circle([x, y + radius * 0.12], radius * 1.12, [0.0, 0.0, 0.0, 0.45 * alpha]).filled(true).build();
    draw.add_circle(centre, radius * 1.06, [0.16, 0.11, 0.03, alpha]).filled(true).build();
    draw.add_circle(centre, radius * 0.92, [0.96, 0.76, 0.28, alpha]).filled(true).build();
    draw.add_circle([x - radius * 0.18, y - radius * 0.22], radius * 0.5, [1.0, 0.93, 0.68, 0.4 * alpha]).filled(true).build();
    draw.add_circle(centre, radius * 0.92, [0.55, 0.38, 0.08, alpha]).thickness((radius * 0.1).max(1.0)).build();
    match content {
        Badge::Tick => {
            let points = vec![[x - radius * 0.42, y + radius * 0.02], [x - radius * 0.12, y + radius * 0.34], [x + radius * 0.46, y - radius * 0.34]];
            draw.add_polyline(points, ink).thickness(radius * 0.2).build();
        }
        Badge::Number(number) => {
            // The overlay's own font, drawn twice a pixel apart for weight.
            let text = number.to_string();
            let [w, h] = ui.calc_text_size(&text);
            let at = [x - w / 2.0, y - h / 2.0];
            draw.add_text(at, ink, &text);
            draw.add_text([at[0] + 1.0, at[1]], ink, &text);
        }
    }
}

/// Shortens `text` with an ellipsis so it fits in `room` pixels.
fn fit(ui: &Ui, text: &str, room: f32) -> String {
    if ui.calc_text_size(text)[0] <= room {
        return text.to_string();
    }
    let mut cut: String = text.to_string();
    while !cut.is_empty() && ui.calc_text_size(format!("{cut}...").as_str())[0] > room {
        cut.pop();
    }
    format!("{}...", cut.trim_end())
}

/// Word-wraps `text` to `room` pixels in at most `max_lines` lines, ending the last with an ellipsis if it is cut short.
fn wrap_lines(ui: &Ui, text: &str, room: f32, max_lines: usize) -> Vec<String> {
    let words: Vec<&str> = text.split_whitespace().collect();
    let mut lines: Vec<String> = Vec::new();
    let mut i = 0;
    while i < words.len() && lines.len() < max_lines {
        if lines.len() + 1 == max_lines {
            // The last line takes the rest, shortened if it does not fit.
            lines.push(fit(ui, &words[i..].join(" "), room));
            break;
        }
        let mut line = words[i].to_string();
        i += 1;
        while i < words.len() {
            let longer = format!("{line} {}", words[i]);
            if ui.calc_text_size(&longer)[0] > room {
                break;
            }
            line = longer;
            i += 1;
        }
        lines.push(fit(ui, &line, room));
    }
    lines
}

/// The widest a line of the small display may be, so a long target name wraps instead of stretching the box.
fn mini_room(ui: &Ui) -> f32 {
    ui.current_font_size() * 21.0
}

/// Seconds A must be held on a check to tick it by hand.
const HOLD_TO_TICK: f32 = 0.6;

// Basic Latin, Latin-1 and Extended-A/B, and general punctuation for names with curly quotes and dashes.
static GLYPHS: [u32; 5] = [0x0020, 0x024F, 0x2000, 0x206F, 0];

pub struct Overlay {
    shared: Shared,
    commands: Sender<Command>,
    directory: PathBuf,
    config: Config,
    visible: bool,
    key_down: bool,
    filter: usize,
    search: String,
    show_all_items: bool,
    tab: usize,
    select_tab: Option<usize>,
    /// Controller buttons held last frame, and the ones newly pressed this frame.
    pad_held: u16,
    pad_pressed: u16,
    pad_released: u16,
    /// When A went down, and whether this hold has already ticked something.
    a_since: Option<Instant>,
    hold_done: AtomicBool,
    pad_connected: bool,
    /// Whether the controller, rather than the keyboard and mouse, was used last.
    using_pad: bool,
    keyboard_mouse_seen: u64,
    /// Whether the panel was at its top level last frame, where B closes it.
    at_base: bool,
    /// Hides B from the game until it is let go, after B closed the panel.
    hide_b: bool,
    frame: u32,
    /// A boss health bar is showing and the setting to stay out of the way is on.
    in_boss_fight: bool,
    /// At a grace, in a shop or an NPC's menu, or in a cutscene: the game has put its HUD away, so does the overlay.
    in_menu: bool,
    /// No character is loaded, and the setting to stay hidden then is on.
    out_of_game: bool,
    /// The last find already turned into a popup, and the popups up now.
    last_found: u64,
    toasts: Vec<Toast>,
    /// The open run and whether it was finished last frame, to spot the goal being met.
    last_finished: Option<(String, bool)>,
    /// The backup the restore question is about: run, backup id, when it was saved.
    restore: Option<(String, String, String)>,
    /// How long the overlay's own work takes each frame (only measured when ER_MOD_PROFILE is set).
    cost: crate::profile::FrameCost,
    /// Whether the game window has focus, refreshed every few frames.
    focused: bool,
    /// What the cursor was last set to, so it is only set again when that changes.
    cursor_shown: bool,
    /// The camera's facing this frame, when anything drawn needs it.
    facing: Option<(f32, f32)>,
    /// Test-only settings, read once at start.
    tests: TestHooks,
    /// The Checks tab's regions, and what they were built from (list, local edits, filter, search).
    region_cache: Option<((u64, u64, usize, String), Vec<RegionGroup>)>,
    list_edits: u64,
    /// Each check row's height when last laid out, by check index, for the width it was laid out at.
    row_heights: std::collections::HashMap<usize, f32>,
    row_heights_width: f32,
    /// Whether the boss picker lists every boss, not only the major ones.
    all_bosses: AtomicBool,
    /// The Found tab's filter, search, and the rows they show (by index, until the list or either changes).
    found_kind: usize,
    found_shown: bool,
    found_search: String,
    found_cache: Option<((u64, usize, String), Vec<usize>)>,
    /// When the overlay started, for animations.
    started: Instant,
    /// Why the beacon could not be placed, shown in Settings.
    beacon_error: Option<String>,
    /// Whether the game shows its own cursor, so ImGui need not draw one.
    game_cursor: bool,
    /// Everything hidden by the hide key, until it is pressed again.
    hidden: bool,
    /// The skip, layout and hide keys held last frame.
    hotkeys_down: [bool; 3],
    /// Whether the Progress tab was on screen last frame.
    progress_shown: bool,
    all_shown: bool,
    /// The place expanded in the All tab.
    all_place: Option<String>,
    all_filter: usize,
    all_kind: usize,
    /// A finished run's summary was asked for, so opening the Progress tab must not load the open run's over it.
    report_requested: bool,
}

impl Overlay {
    pub fn new(directory: PathBuf, shared: Shared, commands: Sender<Command>) -> Self {
        Self {
            shared,
            commands,
            config: Config::load(&directory),
            directory,
            // For testing outside the game, where the hotkey cannot be pressed.
            visible: std::env::var_os("ER_MOD_START_OPEN").is_some(),
            key_down: false,
            filter: 0,
            search: std::env::var("ER_MOD_START_SEARCH").unwrap_or_default(),
            show_all_items: false,
            tab: 0,
            // For testing outside the game: open on this tab (by its index).
            select_tab: std::env::var("ER_MOD_START_TAB").ok().and_then(|t| t.parse().ok()),
            pad_held: 0,
            pad_pressed: 0,
            pad_released: 0,
            a_since: None,
            hold_done: AtomicBool::new(false),
            pad_connected: false,
            using_pad: false,
            keyboard_mouse_seen: 0,
            at_base: false,
            hide_b: false,
            frame: 0,
            in_boss_fight: false,
            in_menu: false,
            out_of_game: false,
            last_found: 0,
            // For testing outside the game: a find to show at once.
            started: Instant::now(),
            found_kind: 0,
            found_shown: false,
            found_search: String::new(),
            found_cache: None,
            all_bosses: AtomicBool::new(false),
            region_cache: None,
            list_edits: 0,
            row_heights: std::collections::HashMap::new(),
            row_heights_width: 0.0,
            focused: false,
            cursor_shown: false,
            facing: None,
            tests: TestHooks::read(),
            cost: crate::profile::FrameCost::new(),
            last_finished: None,
            restore: None,
            toasts: std::env::var("ER_MOD_TEST_FOUND")
                .map(|name| {
                    let unlocked = ["Sacred Tear", "Golden Seed", "Stonesword Key", "Bell Bearing"].map(String::from).to_vec();
                    vec![Toast { title: "Check found".into(), name, region: "Liurnia of the Lakes".into(), unlocked, shown: None, quiet: false }]
                })
                .unwrap_or_default(),
            beacon_error: None,
            game_cursor: false,
            hidden: false,
            hotkeys_down: [false; 3],
            progress_shown: false,
            all_shown: false,
            // For testing outside the game: a place of the All tab to start with open.
            all_place: std::env::var("ER_MOD_START_PLACE").ok().inspect(|p| crate::worker::set_all_place(Some(p.clone()))),
            all_filter: 0,
            all_kind: 0,
            report_requested: false,
        }
    }

    fn send(&self, command: Command) {
        let _ = self.commands.send(command);
    }

    /// The hotkey and the controller combo only count while the game is the focused window.
    fn poll_hotkey(&mut self) {
        // Which window has focus changes rarely: ask every eighth frame.
        if self.frame % 8 == 0 {
            self.focused = unsafe {
                let mut process = 0;
                GetWindowThreadProcessId(GetForegroundWindow(), Some(&mut process));
                process == GetCurrentProcessId()
            };
        }
        let focused = self.focused;
        let was_visible = self.visible;
        let down = focused && unsafe { (GetAsyncKeyState(self.config.toggle_vk().0 as i32) as u16 & 0x8000) != 0 };
        if down && !self.key_down {
            self.visible = !self.visible;
            self.hidden = false;
        }
        self.key_down = down;

        let pad = game_input::pad();
        self.pad_connected = pad.is_some();
        let held = if focused { pad.map_or(0, |p| p.wButtons.0) } else { 0 };

        // Hints follow whichever was used last: the controller, or the keyboard and mouse.
        let pad_used = pad.is_some_and(|p| {
            let off_centre = |v: i16| (v as f32).abs() > DEAD_ZONE;
            p.wButtons.0 != 0
                || [p.sThumbLX, p.sThumbLY, p.sThumbRX, p.sThumbRY].into_iter().any(off_centre)
                || p.bLeftTrigger > 30
                || p.bRightTrigger > 30
        });
        let activity = game_input::keyboard_mouse_activity();
        // The full key scan is a fallback for games that skip DirectInput: only needed to switch back from
        // controller hints, and a key held for an eighth of a second is still caught.
        let keyboard_used = std::mem::replace(&mut self.keyboard_mouse_seen, activity) != activity
            || (self.using_pad && self.frame % 8 == 0 && any_key_down());
        if focused && pad_used {
            self.using_pad = true;
        } else if focused && keyboard_used {
            self.using_pad = false;
        } else if pad.is_none() {
            self.using_pad = false;
        }
        self.pad_pressed = held & !self.pad_held;
        self.pad_released = self.pad_held & !held;
        if self.pad_pressed & pad::A != 0 {
            self.a_since = Some(Instant::now());
            self.hold_done.store(false, Ordering::Relaxed);
        }
        self.pad_held = held;
        // Skip the nearest check, switch layouts, hide everything: a key, or a controller combo if one is set.
        let mut extra_mask = 0;
        for (index, (key, buttons)) in self.config.hotkeys().into_iter().enumerate() {
            let key_down = focused && key.0 != 0 && unsafe { (GetAsyncKeyState(key.0 as i32) as u16 & 0x8000) != 0 };
            let pad_down = buttons != 0 && held & buttons == buttons;
            if pad_down {
                extra_mask |= buttons;
            }
            let down = key_down || pad_down;
            if down && !self.hotkeys_down[index] {
                self.hotkey(index);
            }
            self.hotkeys_down[index] = down;
        }
        let combo = self.config.pad_toggle_buttons();
        let combo_down = held & combo == combo;
        if combo_down && self.pad_pressed & combo != 0 {
            self.visible = !self.visible;
        }
        // B closes the panel from its top level; deeper in, ImGui uses it to back out of menus and lists.
        if self.visible && self.at_base && self.pressed(pad::B) {
            self.visible = false;
            self.hide_b = true;
        }
        if self.visible && !was_visible {
            self.at_base = false;
        }
        self.hide_b &= held & pad::B != 0;
        // Hide the combo's buttons from the game while they are held, so closing the panel does not crouch
        // or lock on, and the B that closed it, so the character does not dodge.
        let mut mask = extra_mask | if combo_down { combo } else { 0 };
        if self.hide_b {
            mask |= pad::B;
        }
        game_input::set_mask(mask);
    }

    /// A hotkey was pressed: 0 skips the nearest check, 1 switches layouts, 2 hides or shows everything.
    fn hotkey(&mut self, index: usize) {
        match index {
            0 if !self.hidden => {
                let name = self.shared.lock().poll.as_ref().and_then(|p| p.nearby.first()).map(|c| c.name.clone());
                if let Some(name) = name {
                    self.send(Command::SkipNearest);
                    self.note("Skipped for now", name, "Undo it in the Checks tab (right-click or Y on the check)".into());
                }
            }
            1 if !self.hidden => {
                self.config.switch_layout();
                self.note("Layout", format!("{} layout", LAYOUT_NAMES[self.config.layout]), "Change what it shows in Settings".into());
            }
            2 => {
                self.hidden = !self.hidden;
                if self.hidden {
                    self.visible = false;
                    self.toasts.clear();
                }
            }
            _ => {}
        }
    }

    /// A quiet notification from the overlay itself.
    fn note(&mut self, title: &str, name: String, region: String) {
        self.toasts.push(Toast { title: title.into(), name, region, unlocked: Vec::new(), shown: None, quiet: true });
    }

    fn pressed(&self, button: u16) -> bool {
        self.pad_pressed & button != 0
    }

    /// Hands the controller to ImGui's navigation: D-pad or left stick to move, A to press, B to go back,
    /// right stick to scroll.
    fn feed_pad(&self, io: &mut Io) {
        let Some(state) = game_input::pad().filter(|_| self.visible) else {
            return;
        };
        let buttons = state.wButtons.0;
        let stick = |value: i16, sign: f32| ((value as f32 * sign - DEAD_ZONE) / (32767.0 - DEAD_ZONE)).clamp(0.0, 1.0);
        let moves = [
            (Key::GamepadDpadLeft, pad::LEFT, stick(state.sThumbLX, -1.0)),
            (Key::GamepadDpadRight, pad::RIGHT, stick(state.sThumbLX, 1.0)),
            (Key::GamepadDpadUp, pad::UP, stick(state.sThumbLY, 1.0)),
            (Key::GamepadDpadDown, pad::DOWN, stick(state.sThumbLY, -1.0)),
        ];
        for (key, button, analog) in moves {
            io.add_key_event(key, buttons & button != 0 || analog > 0.5);
        }
        io.add_key_event(Key::GamepadFaceDown, buttons & pad::A != 0);
        io.add_key_event(Key::GamepadFaceRight, buttons & pad::B != 0);
        io.add_key_event(Key::GamepadFaceLeft, buttons & pad::X != 0);
        let scrolls = [
            (Key::GamepadLStickLeft, stick(state.sThumbRX, -1.0)),
            (Key::GamepadLStickRight, stick(state.sThumbRX, 1.0)),
            (Key::GamepadLStickUp, stick(state.sThumbRY, 1.0)),
            (Key::GamepadLStickDown, stick(state.sThumbRY, -1.0)),
        ];
        for (key, value) in scrolls {
            io.add_key_analog_event(key, value > 0.1, value);
        }
    }

    fn panel(&mut self, ui: &Ui, snapshot: &mut Snapshot) {
        let mut open = true;
        ui.window("Rando Companion")
            .size([520.0, 640.0], Condition::FirstUseEver)
            .position([60.0, 80.0], Condition::FirstUseEver)
            .opened(&mut open)
            .build(|| {
                self.at_base = ui.is_window_focused();
                if let Some(fatal) = &snapshot.fatal {
                    ui.text_colored(RED, "The tracker did not start:");
                    ui.text_wrapped(fatal);
                    ui.text_disabled(format!("Details are in {}", self.directory.join("ErRandoCompanionMod.log").display()));
                    return;
                }
                if !snapshot.ready {
                    ui.text("Loading the tracker...");
                    return;
                }
                self.header(ui, snapshot);
                ui.separator();
                if self.using_pad {
                    ui.text_disabled(format!(
                        "{} or B: close   LB/RB: tabs   A: track or untrack   hold A: tick by hand   Y: pick target number",
                        self.config.pad_toggle
                    ));
                } else {
                    ui.text_disabled(format!(
                        "{}: close   click a name: track or untrack   click the box: tick by hand   right-click: pick target number",
                        self.config.toggle_key
                    ));
                }
                if self.pressed(pad::LB) {
                    self.select_tab = Some((self.tab + TABS.len() - 1) % TABS.len());
                } else if self.pressed(pad::RB) {
                    self.select_tab = Some((self.tab + 1) % TABS.len());
                }
                if let Some(_bar) = ui.tab_bar("tabs") {
                    for (index, name) in TABS.iter().enumerate() {
                        let flags = if self.select_tab == Some(index) { TabItemFlags::SET_SELECTED } else { TabItemFlags::empty() };
                        let Some(_tab) = TabItem::new(name).flags(flags).begin(ui) else { continue };
                        // The backups list costs disk reads, so it is fetched when the Runs tab is opened.
                        if index == 5 && self.tab != 5 {
                            self.send(Command::LoadBackups);
                        }
                        self.tab = index;
                        match index {
                            0 => self.checks_tab(ui, snapshot),
                            1 => self.targets_tab(ui, snapshot),
                            2 => self.items_tab(ui, snapshot),
                            3 => self.randomizer_tab(ui, snapshot),
                            4 => self.settings_tab(ui, snapshot),
                            5 => self.runs_tab(ui, snapshot),
                            6 => self.found_tab(ui, snapshot),
                            7 => self.progress_tab(ui, snapshot),
                            _ => self.all_tab(ui, snapshot),
                        }
                    }
                }
                self.select_tab = None;
                // The top level: the panel itself has focus (not a list inside it), nothing is being
                // edited, and no menu or dropdown is open.
                let popup_open = unsafe {
                    hudhook::imgui::sys::igIsPopupOpen(
                        std::ptr::null(),
                        (hudhook::imgui::sys::ImGuiPopupFlags_AnyPopupId | hudhook::imgui::sys::ImGuiPopupFlags_AnyPopupLevel) as i32,
                    )
                };
                self.at_base = ui.is_window_focused() && !ui.is_any_item_active() && !popup_open;
            });
        if !open {
            self.visible = false;
        }
    }

    fn header(&self, ui: &Ui, snapshot: &mut Snapshot) {
        let summary = &snapshot.summary;
        ui.text(&summary.seed_name);
        ui.same_line();
        ui.text_disabled(format!(
            "{} / {} checked, {} available",
            summary.checked, summary.total, summary.available
        ));
        let played = played_seconds(snapshot);
        if played >= 1.0 {
            let pace = per_hour(summary.checked, played).map_or(String::new(), |p| format!(", {p:.1} checks an hour"));
            let state = if summary.finished { " (finished)" } else { "" };
            ui.text_disabled(format!("Played {}{state}{pace}", clock_text(played)));
        }
        // Whether auto-tracking is working, and if not, why.
        match snapshot.poll.as_ref().filter(|p| !p.tracking_message.is_empty()) {
            Some(poll) => {
                let colour = match poll.tracking.as_str() {
                    "on" => GREEN,
                    "waiting" | "readOnly" => GREY,
                    "unsupported" | "blocked" | "unavailable" => RED,
                    _ => YELLOW,
                };
                let _colour = ui.push_style_color(StyleColor::Text, colour);
                ui.text_wrapped(&poll.tracking_message);
            }
            None if !summary.spoiler.loaded => ui.text_colored(YELLOW, "No spoiler log loaded: tracking by hand only."),
            None => {}
        }
        if let Some(goal) = &summary.goal {
            if goal.met {
                ui.text_colored(GOLD, format!("Run complete: {}", goal.description));
            } else {
                ui.text_colored(GOLD, format!("Goal: {}  {} / {}", goal.description, goal.done, goal.needed));
            }
        }
        if let Some(text) = guide_text(snapshot) {
            if let Some(turn) = self.arrow_turn(snapshot) {
                draw_arrow(ui, turn, ui.text_line_height() * 1.4);
                ui.same_line();
            }
            ui.text_colored(GOLD, text);
        }
        if let Some(text) = grace_text(snapshot) {
            ui.text_disabled(text);
        }
        if let Some(text) = dead_end_text(snapshot, 3) {
            let _colour = ui.push_style_color(StyleColor::Text, YELLOW);
            ui.text_wrapped(text);
        }
        let summary = &snapshot.summary;
        // Wrapped: these can be long, such as the read-only notice when the desktop app owns the run.
        for (colour, text) in [(RED, &summary.problem), (YELLOW, &summary.warning)] {
            if let Some(text) = text {
                let _colour = ui.push_style_color(StyleColor::Text, colour);
                ui.text_wrapped(text);
            }
        }
        let mut dismiss = false;
        if let Some(notice) = &snapshot.notice {
            ui.text_colored(YELLOW, notice);
            ui.same_line();
            dismiss = ui.small_button("OK");
        }
        if dismiss {
            snapshot.notice = None;
        }
    }

    fn checks_tab(&mut self, ui: &Ui, snapshot: &mut Snapshot) {
        ui.set_next_item_width(130.0);
        ui.combo_simple_string("##filter", &mut self.filter, &FILTERS);
        ui.same_line();
        ui.set_next_item_width(-1.0);
        ui.input_text("##search", &mut self.search).hint("Search checks or regions").build();

        // Grouping 338 checks every frame is the panel's biggest cost, so it is kept until the list, the filter
        // or the search changes.
        let key = (snapshot.generation, self.list_edits, self.filter, self.search.clone());
        if self.region_cache.as_ref().map(|(k, _)| k) != Some(&key) {
            self.region_cache = Some((key, group_checks(&snapshot.checks, FILTERS[self.filter], &self.search)));
        }
        let regions = std::mem::take(&mut self.region_cache.as_mut().unwrap().1);
        let search = self.search.clone();

        // A new target takes the first unused number, or replaces the active one when all five are in use.
        let targets = &snapshot.summary.targets;
        let active = targets.iter().find(|t| t.active).map(|t| t.number);
        let free_number = (1..=5).find(|n| !targets.iter().any(|t| t.number == *n)).or(active).unwrap_or(1);

        ui.child_window("checks").build(|| {
            if regions.is_empty() {
                ui.text_disabled("Nothing to show.");
            }
            let open_all = !search.is_empty();
            // Rows well out of view take their remembered height instead of being laid out (wrapped text is the
            // costly part). Rows within a screen of the view are still laid out, so the D-pad can always reach
            // the next one. Heights are forgotten when the width changes, as that changes the wrapping.
            let width = ui.content_region_avail()[0];
            if (width - self.row_heights_width).abs() > 0.5 {
                self.row_heights.clear();
                self.row_heights_width = width;
            }
            let spacing = ui.clone_style().item_spacing[1];
            let margin = ui.window_size()[1];
            for group in &regions {
                let flags = if open_all { TreeNodeFlags::DEFAULT_OPEN } else { TreeNodeFlags::empty() };
                if !ui.collapsing_header(&group.header, flags) {
                    continue;
                }
                for &i in &group.checks {
                    let [x, top] = ui.cursor_screen_pos();
                    if let Some(&height) = self.row_heights.get(&i) {
                        if !ui.is_rect_visible([x, top - margin], [x + 1.0, top + height + margin]) {
                            ui.dummy([1.0, (height - spacing).max(0.0)]);
                            continue;
                        }
                    }
                    if self.check_row(ui, &mut snapshot.checks[i], free_number) {
                        self.list_edits += 1;
                    }
                    self.row_heights.insert(i, ui.cursor_screen_pos()[1] - top);
                }
            }
        });
        if let Some((_, cached)) = self.region_cache.as_mut() {
            *cached = regions;
        }
    }

    /// Clicking the box ticks a check by hand. Pressing the name (A on a controller) makes it a target;
    /// on a controller, holding A ticks it, so a stray press never marks something as found.
    /// Returns true when the row changed the check's status, which can move it in or out of the filter.
    fn check_row(&self, ui: &Ui, check: &mut Check, free_number: i32) -> bool {
        let _id = ui.push_id(&check.id);
        let row_x = ui.cursor_pos()[0];
        let mut ticked = check.status == "checked";
        // A controller press on the box would tick it at once; only the mouse may.
        let pad_a = (self.pad_held | self.pad_released) & pad::A != 0;
        // On a controller the box is not a stop at all, so A only ever lands on the name. Disabled items
        // keep their normal look here because the style's disabled alpha is 1 (see initialize).
        let box_off = ui.begin_disabled(self.using_pad);
        let mut toggled = ui.checkbox("##done", &mut ticked) && !pad_a;
        drop(box_off);
        if !toggled {
            ticked = check.status == "checked";
        }
        let mut focused = ui.is_item_focused();
        ui.same_line();
        if let Some(number) = check.target {
            ui.text_colored(GOLD, format!("[{number}]"));
            ui.same_line();
        }
        let color = match check.status.as_str() {
            "checked" => GREEN,
            "locked" => GREY,
            _ => [1.0, 1.0, 1.0, 1.0],
        };
        let name_x = ui.cursor_pos()[0];
        if check.skipped && check.status == "available" {
            ui.text_colored(GREY, "[skipped]");
            ui.same_line();
        }
        {
            let color = if check.skipped && check.status == "available" { GREY } else { color };
            let _color = ui.push_style_color(StyleColor::Text, color);
            // A mouse click on the name makes it a target; the controller's A is timed below instead.
            if ui.selectable(&check.name) && !pad_a {
                self.make_target(check, free_number);
            }
        }
        focused |= ui.is_item_focused();
        if focused {
            if self.pad_held & pad::A != 0 && !self.hold_done.load(Ordering::Relaxed) {
                let held = self.a_since.map_or(0.0, |at| at.elapsed().as_secs_f32()) / HOLD_TO_TICK;
                // Fill the row from the left while A is held, so the hold is visible.
                let [left, top] = ui.item_rect_min();
                let [right, bottom] = ui.item_rect_max();
                let fill = left + (right - left) * held.min(1.0);
                ui.get_window_draw_list().add_rect([left, top], [fill, bottom], [1.0, 0.82, 0.35, 0.35]).filled(true).build();
                if held >= 1.0 {
                    self.hold_done.store(true, Ordering::Relaxed);
                    ticked = !ticked;
                    toggled = true;
                }
            } else if self.pad_released & pad::A != 0 && !self.hold_done.load(Ordering::Relaxed) {
                self.make_target(check, free_number);
            }
        }
        if toggled {
            self.send(Command::SetChecked(check.id.clone(), ticked));
            // Shown at once; the worker's refresh confirms it a moment later.
            check.status = if ticked { "checked".into() } else { "available".into() };
        }
        if ui.is_item_clicked_with_button(MouseButton::Right) || (focused && self.pressed(pad::Y)) {
            ui.open_popup("target");
        }
        if let Some(_menu) = ui.begin_popup("target") {
            // A pressed in this menu belongs to it. The menu closes on the press and focus goes back to the
            // row, so without this the release would count as A on the row and toggle the target off again.
            if (self.pad_held | self.pad_released) & pad::A != 0 {
                self.hold_done.store(true, Ordering::Relaxed);
            }
            for number in 1..=5 {
                if ui.menu_item_config(format!("Target {number}")).selected(check.target == Some(number)).build() {
                    self.send(Command::SetTarget(check.id.clone(), Some(number)));
                    check.target = Some(number);
                }
            }
            if check.target.is_some() && ui.menu_item("Clear target") {
                self.send(Command::SetTarget(check.id.clone(), None));
                check.target = None;
            }
            if check.status != "checked" {
                ui.separator();
                let label = if check.skipped { "Unskip" } else { "Skip for now" };
                if ui.menu_item(label) {
                    self.send(Command::ToggleSkip(check.id.clone()));
                    check.skipped = !check.skipped;
                }
            }
        }
        // Where the check is, under its name as in the desktop app. It never says what the check holds.
        if !check.details.is_empty() {
            let indent = name_x - row_x;
            ui.indent_by(indent);
            {
                let _color = ui.push_style_color(StyleColor::Text, DETAILS);
                ui.text_wrapped(&check.details);
            }
            ui.unindent_by(indent);
        }
        toggled
    }

    /// Toggles tracking: a check that is a target stops being one; any other becomes the active target
    /// with the first free number.
    fn make_target(&self, check: &mut Check, free_number: i32) {
        match check.target {
            Some(_) => {
                self.send(Command::SetTarget(check.id.clone(), None));
                check.target = None;
            }
            None => {
                self.send(Command::SetTarget(check.id.clone(), Some(free_number)));
                check.target = Some(free_number);
            }
        }
    }

    fn targets_tab(&self, ui: &Ui, snapshot: &Snapshot) {
        let targets = &snapshot.summary.targets;
        if targets.is_empty() {
            ui.text_wrapped(if self.using_pad {
                "No targets. Press A on a check in the Checks tab to make it a target, or Y to pick its number."
            } else {
                "No targets. Click a check's name in the Checks tab to make it a target, or right-click it to pick its number."
            });
            return;
        }
        ui.text_disabled("The active target is the one the guide follows.");
        for target in targets {
            let _id = ui.push_id(target.number.to_string());
            if ui.radio_button_bool(format!("{}  {}", target.number, target.name), target.active) {
                self.send(Command::SetActiveTarget(target.number));
            }
            ui.same_line();
            if ui.small_button("Clear") {
                self.send(Command::SetTarget(target.id.clone(), None));
            }
        }
    }

    fn items_tab(&mut self, ui: &Ui, snapshot: &mut Snapshot) {
        // What each missing item would open: the logic's answer to "what if I had it", never where it is.
        if !snapshot.unlocks.is_empty() {
            ui.text("What each missing item would open now");
            for unlock in snapshot.unlocks.iter().take(8) {
                let regions = unlock.regions.iter().take(2).cloned().collect::<Vec<_>>().join(", ");
                let more = if unlock.regions.len() > 2 { ", ..." } else { "" };
                ui.text_colored(GOLD, &unlock.item);
                ui.same_line();
                ui.text_disabled(format!(
                    "opens {} check{} ({regions}{more})",
                    unlock.opens,
                    if unlock.opens == 1 { "" } else { "s" }
                ));
            }
            ui.separator();
        }
        ui.checkbox("Show items the logic does not use", &mut self.show_all_items);
        ui.child_window("items").build(|| {
            let mut categories: Vec<&str> = Vec::new();
            for item in &snapshot.items {
                if !categories.contains(&item.category.as_str()) {
                    categories.push(&item.category);
                }
            }
            let categories: Vec<String> = categories.into_iter().map(String::from).collect();
            for category in categories {
                let indices: Vec<usize> = (0..snapshot.items.len())
                    .filter(|&i| snapshot.items[i].category == category && (self.show_all_items || snapshot.items[i].relevant))
                    .collect();
                if indices.is_empty() || !ui.collapsing_header(&category, TreeNodeFlags::DEFAULT_OPEN) {
                    continue;
                }
                for i in indices {
                    let item = &mut snapshot.items[i];
                    let _id = ui.push_id(&item.name);
                    if item.max <= 1 {
                        let mut have = item.count > 0;
                        if ui.checkbox(&item.name, &mut have) {
                            item.count = have as i32;
                            self.send(Command::SetItem(item.name.clone(), item.count));
                        }
                    } else {
                        if ui.small_button("-") && item.count > 0 {
                            item.count -= 1;
                            self.send(Command::SetItem(item.name.clone(), item.count));
                        }
                        ui.same_line();
                        if ui.small_button("+") && item.count < item.max {
                            item.count += 1;
                            self.send(Command::SetItem(item.name.clone(), item.count));
                        }
                        ui.same_line();
                        ui.text(format!("{} {}/{}", item.name, item.count, item.max));
                    }
                }
            }
        });
    }

    fn settings_tab(&mut self, ui: &Ui, snapshot: &mut Snapshot) {
        ui.text("Overlay");
        if self.using_pad {
            ui.text_disabled(format!("Open or close: {} (change pad_toggle in ErRandoCompanionMod.ini)", self.config.pad_toggle));
        } else {
            ui.text_disabled(format!("Open or close: {} (change toggle_key in ErRandoCompanionMod.ini)", self.config.toggle_key));
        }
        // Two layouts of the small display, for playing and for planning; the settings below are the active one's.
        let layout = LAYOUT_NAMES[self.config.layout];
        ui.text(format!("Small display layout: {layout}"));
        ui.same_line();
        if ui.small_button(format!("Switch to {}", LAYOUT_NAMES[self.config.layout ^ 1])) {
            self.config.switch_layout();
        }
        let key_name = |key: &str, combo: &str| match (key.is_empty(), combo.is_empty()) {
            (true, true) => "not set".to_string(),
            (false, true) => key.to_string(),
            (true, false) => combo.to_string(),
            (false, false) => format!("{key} or {combo}"),
        };
        ui.text_disabled(format!(
            "Switch layout: {}   Skip the nearest check: {}   Hide everything (for recording): {}",
            key_name(&self.config.layout_key, &self.config.pad_layout),
            key_name(&self.config.skip_key, &self.config.pad_skip),
            key_name(&self.config.hide_key, &self.config.pad_hide)
        ));
        ui.text_disabled("The keys and controller combos are set in ErRandoCompanionMod.ini. The checkboxes below are this layout's.");
        let mut changed = ui.checkbox("Show the small display while this panel is closed", &mut self.config.mini_hud);
        changed |= ui.checkbox("Show the goal in the small display", &mut self.config.show_goal);
        changed |= ui.checkbox("Show the active target's line in the small display", &mut self.config.show_guide);
        changed |= ui.checkbox("Show the nearest found grace to the target (where to warp)", &mut self.config.show_grace);
        changed |= ui.checkbox("Show the latest split, against your best run", &mut self.config.show_splits);
        changed |= ui.checkbox("Show the death count", &mut self.config.show_deaths);
        changed |= ui.checkbox("Show an arrow toward the active target", &mut self.config.guide_arrow);
        match camera::facing() {
            Some((east, north)) => {
                let degrees = east.atan2(north).to_degrees().rem_euclid(360.0);
                ui.text_disabled(format!(
                    "Camera faces {} ({degrees:.0} degrees). If this disagrees with the game's compass, tell the developer.",
                    compass(east as f64, north as f64)
                ));
            }
            None => ui.text_disabled("The camera cannot be read right now, so there is no arrow."),
        }
        ui.set_next_item_width(200.0);
        changed |= ui.slider("Text size", 0.5, 3.0, &mut self.config.text_scale);

        // Which of the game's own map icons the targets use. The game has no list of what each number
        // looks like, so this steps through them; the map shows the change the next time it opens.
        if self.config.map_markers {
            ui.text(format!("Map icon for targets: {}", self.config.map_icon));
            let mut icon = self.config.map_icon as i32;
            // Previous and next step between the icons the game's own map uses; the others go one by one.
            let known = crate::map_markers::game_icons();
            if !known.is_empty() {
                if ui.small_button("Previous game icon") {
                    icon = known.iter().rev().find(|&&i| (i as i32) < icon).or(known.last()).map_or(icon, |&i| i as i32);
                }
                ui.same_line();
                if ui.small_button("Next game icon") {
                    icon = known.iter().find(|&&i| (i as i32) > icon).or(known.first()).map_or(icon, |&i| i as i32);
                }
                ui.same_line();
            }
            for (label, step) in [("-1", -1), ("+1", 1)] {
                if ui.small_button(label) {
                    icon = (icon + step).clamp(0, 999);
                }
                ui.same_line();
            }
            if ui.small_button("Reset") {
                icon = 393;
            }
            if icon != self.config.map_icon as i32 {
                self.config.map_icon = icon as u16;
                crate::map_markers::set_icon(self.config.map_icon);
                changed = true;
            }
            ui.text_disabled("Close and reopen the game's map to see the new icon. Some numbers show nothing.");
        }

        let mut nearby = self.config.nearby_checks as i32;
        ui.set_next_item_width(200.0);
        if ui.slider("Nearest available checks in the small display", 0, 10, &mut nearby) {
            self.config.nearby_checks = nearby.clamp(0, 10) as usize;
            changed = true;
        }
        changed |= ui.checkbox("Hide everything during boss fights (while a boss health bar shows)", &mut self.config.hide_in_boss_fights);
        changed |= ui.checkbox("Hide everything at graces, in shops and menus, and in cutscenes", &mut self.config.hide_in_menus);
        changed |= ui.checkbox("Show a popup when a check is ticked automatically", &mut self.config.found_popup);
        changed |= ui.checkbox("Play a sound when a check is ticked automatically", &mut self.config.found_sound);
        if self.config.found_sound {
            let names: Vec<&str> = crate::sound::CHOICES.iter().map(|c| c.1).collect();
            let mut index = crate::sound::CHOICES.iter().position(|c| c.0 == self.config.found_sound_choice).unwrap_or(0);
            ui.set_next_item_width(260.0);
            if ui.combo_simple_string("Sound", &mut index, &names) {
                self.config.found_sound_choice = crate::sound::CHOICES[index].0.to_string();
                changed = true;
            }
            let mut volume = self.config.found_volume as i32;
            ui.set_next_item_width(200.0);
            if ui.slider("Volume", 0, 100, &mut volume) {
                self.config.found_volume = volume.clamp(0, 100) as u32;
                changed = true;
            }
            ui.same_line();
            if ui.small_button("Test") {
                crate::sound::play(&self.directory, &self.config.found_sound_choice, self.config.found_volume);
            }
            if self.config.found_sound_choice == "custom" && !self.directory.join("ErRandoCompanionMod.wav").is_file() {
                ui.text_disabled(format!("Put a WAV file named ErRandoCompanionMod.wav in {} to use it.", self.directory.display()));
            }
            if self.config.found_sound_choice == "windows" {
                ui.text_disabled("The Windows sound follows the Windows volume, not this slider.");
            }
        }
        changed |= ui.checkbox("Draw the companion's target icons on the game's map", &mut self.config.map_icons);
        changed |= ui.checkbox("Stay hidden on the title screen and loading screens", &mut self.config.hide_on_main_menu);
        changed |= ui.checkbox("Show the run's played time in the small display", &mut self.config.show_timer);
        changed |= ui.checkbox("Put the game's beacon (column of light) on the active target", &mut self.config.beacon);
        if self.config.beacon {
            let active = snapshot.summary.targets.iter().find(|t| t.active);
            if let Some(target) = active.filter(|t| crate::map_markers::surface_position(&t.id).is_none()) {
                ui.text_disabled(format!("{} is not on the surface map, so it gets no beacon yet.", target.name));
            }
            match &self.beacon_error {
                // Normal on the title screen, so not shown as a problem.
                Some(error) if error.contains("load a character") => ui.text_disabled(format!("Beacon: {error}")),
                Some(error) => ui.text_colored(YELLOW, format!("Beacon: {error}")),
                None => {}
            }
        }
        // What the game holds in its beacon slots, for checking the beacon against the game.
        match crate::beacon::slots() {
            None => ui.text_disabled("Beacon slots: load a character to see them."),
            Some(slots) => {
                let used: Vec<String> = slots
                    .iter()
                    .enumerate()
                    .filter(|(_, s)| !s.is_empty())
                    .map(|(i, s)| format!("{}: x {:.1} z {:.1} kind {:#06x} index {}", i + 1, s.x, s.z, s.kind, s.index))
                    .collect();
                ui.text_disabled(if used.is_empty() { "Beacon slots: all empty".to_string() } else { format!("Beacon slots: {}", used.join("; ")) });
            }
        }
        if changed {
            self.config.save();
        }
    }

    /// The randomizer's options, filled in from the spoiler log; changing one changes the logic.
    fn randomizer_tab(&self, ui: &Ui, snapshot: &mut Snapshot) {
        ui.text_disabled("Filled in from the spoiler log when one is loaded. They decide which checks count and what unlocks them.");
        for setting in &mut snapshot.settings {
            let _id = ui.push_id(&setting.id);
            match setting.kind.as_str() {
                "bool" => {
                    let mut on = setting.value == "true";
                    if ui.checkbox(&setting.name, &mut on) {
                        setting.value = on.to_string();
                        self.send(Command::SetSetting(setting.id.clone(), setting.value.clone()));
                    }
                }
                "count" => {
                    let value: i32 = setting.value.parse().unwrap_or(0);
                    let mut next = value;
                    if ui.small_button("-") && value > 0 {
                        next = value - 1;
                    }
                    ui.same_line();
                    if ui.small_button("+") && value < setting.max {
                        next = value + 1;
                    }
                    ui.same_line();
                    ui.text(format!("{}: {}", setting.name, next));
                    if next != value {
                        setting.value = next.to_string();
                        self.send(Command::SetSetting(setting.id.clone(), setting.value.clone()));
                    }
                }
                _ => {
                    let names: Vec<&str> = if setting.option_names.len() == setting.options.len() {
                        setting.option_names.iter().map(String::as_str).collect()
                    } else {
                        setting.options.iter().map(String::as_str).collect()
                    };
                    let mut index = setting.options.iter().position(|o| *o == setting.value).unwrap_or(0);
                    ui.set_next_item_width(200.0);
                    if !names.is_empty() && ui.combo_simple_string(&setting.name, &mut index, &names) {
                        setting.value = setting.options[index].clone();
                        self.send(Command::SetSetting(setting.id.clone(), setting.value.clone()));
                    }
                }
            }
        }
    }

    fn runs_tab(&mut self, ui: &Ui, snapshot: &Snapshot) {
        if ui.button("Open the randomizer's newest seed") {
            self.send(Command::OpenNewestSeed);
        }
        ui.text_disabled("Runs are shared with the desktop app. Whichever opens a run first saves it; the other follows it read-only.");
        ui.separator();
        for seed in &snapshot.seeds {
            let _id = ui.push_id(seed);
            let current = *seed == snapshot.summary.seed_name;
            if current {
                ui.text_colored(GREEN, format!("{seed} (open)"));
            } else {
                if ui.small_button("Open") {
                    self.send(Command::OpenSeed(seed.clone()));
                }
                ui.same_line();
                ui.text(seed);
            }
        }

        ui.separator();
        self.goal_picker(ui, snapshot);

        ui.separator();
        ui.text("Finished runs");
        if snapshot.history.is_empty() {
            ui.text_disabled("None yet. A run finishes when its goal is met.");
        }
        for run in &snapshot.history {
            let _id = ui.push_id(format!("history/{}", run.seed_name));
            if ui.small_button("Summary") {
                self.send(Command::LoadReport(Some(run.seed_name.clone())));
                self.select_tab = Some(PROGRESS_TAB);
                self.report_requested = true;
            }
            ui.same_line();
            let deaths = if run.deaths > 0 { format!(", {} deaths", run.deaths) } else { String::new() };
            let pace = run.per_hour.map_or(String::new(), |p| format!(", {p:.1} an hour")) + &deaths;
            let best = if run.best { "  best" } else { "" };
            ui.text(format!("{}  -  {}", run.seed_name, run.goal));
            if run.best {
                ui.same_line();
                ui.text_colored(GOLD, best.trim());
            }
            ui.text_disabled(format!("   {}  -  {}, {} checks{pace}", run.finished_at, clock_text(run.played_seconds), run.checked));
        }

        ui.separator();
        ui.text("Backups");
        ui.text_disabled("A copy of each run every 10 minutes of play, and before it is deleted or restored over; the last 10 are kept.");
        ui.text_disabled("\"+ game save\" copies also hold the game's own save. Put that back from the desktop app's Backups window, with the game closed.");
        if snapshot.backups.is_empty() {
            ui.text_disabled("None yet.");
        }
        let mut runs: Vec<&str> = snapshot.backups.iter().map(|b| b.seed_name.as_str()).collect();
        runs.dedup();
        for run in runs {
            let deleted = !snapshot.seeds.iter().any(|s| s == run);
            let title = if deleted { format!("{run} (deleted)###backups{run}") } else { format!("{run}###backups{run}") };
            if !ui.collapsing_header(&title, TreeNodeFlags::empty()) {
                continue;
            }
            for backup in snapshot.backups.iter().filter(|b| b.seed_name == run) {
                let _id = ui.push_id(format!("{run}/{}", backup.id));
                if ui.small_button("Restore") {
                    self.restore = Some((backup.seed_name.clone(), backup.id.clone(), backup.saved_at.clone()));
                    ui.open_popup("restore");
                }
                ui.same_line();
                let save = if backup.has_game_save { "  + game save" } else { "" };
                ui.text(format!("{}  -  {} checked{save}", backup.saved_at, backup.checked));
                if let Some(_popup) = ui.begin_popup("restore") {
                    if let Some((name, id, when)) = self.restore.clone() {
                        ui.text(format!("Put {name} back as it was on {when}?"));
                        ui.text_disabled("What is saved now is backed up first, so this can be undone.");
                        if ui.button("Restore") {
                            self.send(Command::RestoreBackup(name, id));
                            self.restore = None;
                            ui.close_current_popup();
                        }
                        ui.same_line();
                        if ui.button("Cancel") {
                            self.restore = None;
                            ui.close_current_popup();
                        }
                    }
                }
            }
        }
    }

    /// A split: its number, name and time, and how it compares with the best run at the same step.
    fn split_line(&self, ui: &Ui, split: &crate::model::Split) {
        let time = if split.seconds < 0.0 { "before timing".to_string() } else { clock_text(split.seconds) };
        ui.text_disabled(format!("{}. {}  {time}", split.number, split.name));
        if let Some(best) = split.best.filter(|_| split.seconds >= 0.0) {
            let delta = split.seconds - best;
            ui.same_line();
            let sign = if delta < 0.0 { "-" } else { "+" };
            ui.text_colored(if delta <= 0.0 { GREEN } else { RED }, format!("{sign}{}", clock_text(delta.abs())));
        }
    }

    /// What is left where, the run's splits and deaths, and the key items in the order they were found. Also shows a
    /// finished run's summary, opened from the Runs tab.
    fn progress_tab(&mut self, ui: &Ui, snapshot: &Snapshot) {
        ui.child_window("progress").build(|| {
            let report = snapshot.report.as_ref();
            let other_run = report.filter(|r| r.seed_name != snapshot.summary.seed_name);
            if let Some(report) = other_run {
                ui.text_colored(GOLD, format!("Summary of {}", report.seed_name));
                ui.same_line();
                if ui.small_button("Back to this run") {
                    self.send(Command::LoadReport(None));
                }
                self.report_body(ui, report);
                return;
            }

            ui.text("What's left");
            ui.text_disabled("Open checks by region (available and not skipped), then locked, and done of all.");
            // Columns by position: the region, then its counts lined up on the right.
            let width = ui.content_region_avail()[0];
            let columns = [width * 0.52, width * 0.70, width * 0.84];
            let header = |x: f32, text: &str| {
                ui.same_line_with_pos(x);
                ui.text_disabled(text);
            };
            ui.text_disabled("Region");
            header(columns[0], "Open");
            header(columns[1], "Locked");
            header(columns[2], "Done");
            for region in &snapshot.regions {
                if region.done == region.total {
                    continue;
                }
                ui.text(fit(ui, &region.region, columns[0] - 8.0));
                ui.same_line_with_pos(columns[0]);
                let open = if region.skipped > 0 { format!("{} (+{})", region.open, region.skipped) } else { region.open.to_string() };
                if region.open > 0 {
                    ui.text_colored(GREEN, open);
                } else {
                    ui.text_disabled(open);
                }
                header(columns[1], &region.locked.to_string());
                header(columns[2], &format!("{} / {}", region.done, region.total));
            }
            if snapshot.regions.iter().any(|r| r.skipped > 0) {
                ui.text_disabled("(+N) is how many there are skipped.");
            }
            let finished = snapshot.regions.iter().filter(|r| r.total > 0 && r.done == r.total).count();
            if finished > 0 {
                ui.text_disabled(format!("{finished} regions finished."));
            }
            if let Some(text) = dead_end_text(snapshot, 3) {
                ui.separator();
                let _colour = ui.push_style_color(StyleColor::Text, YELLOW);
                ui.text_wrapped(text);
            }
            ui.separator();
            match report {
                Some(report) => self.report_body(ui, report),
                None => ui.text_disabled("Loading..."),
            }
        });
    }

    /// A run in numbers: time, checks, bosses, deaths by place, the slowest stretch, splits, key items in order.
    fn report_body(&self, ui: &Ui, report: &crate::model::Report) {
        ui.text(format!(
            "{}{}: {}, {} / {} checks, {} bosses defeated, {} deaths",
            report.goal,
            if report.finished { " (finished)" } else { "" },
            clock_text(report.played_seconds),
            report.checked,
            report.total,
            report.bosses_defeated,
            report.deaths
        ));
        if let Some(slowest) = &report.slowest {
            ui.text_disabled(format!(
                "Slowest stretch: {} without a check, {} to {}, ended by {}",
                clock_text(slowest.to - slowest.from),
                clock_text(slowest.from),
                clock_text(slowest.to),
                slowest.ended_with
            ));
        }
        if !report.splits.is_empty() {
            ui.separator();
            ui.text("Splits");
            if report.splits.iter().any(|s| s.best.is_some()) {
                ui.same_line();
                ui.text_disabled("(against your best run with this goal)");
            }
            for split in &report.splits {
                self.split_line(ui, split);
            }
        }
        if !report.death_places.is_empty() {
            ui.separator();
            ui.text("Deaths by place");
            for place in &report.death_places {
                ui.text_disabled(format!("{}  {}", place.count, place.place));
            }
        }
        if !report.kills.is_empty() {
            ui.separator();
            ui.text(format!("Kills ({})", report.kills.iter().map(|k| k.count).sum::<i32>()));
            for kind in report.kills.iter().take(25) {
                ui.text_disabled(format!("{}  {}", kind.count, kind.place));
            }
            if report.kills.len() > 25 {
                ui.text_disabled(format!("and {} other kinds", report.kills.len() - 25));
            }
        }
        if !report.key_items.is_empty() {
            ui.separator();
            ui.text("Key items, in the order found");
            for item in &report.key_items {
                let time = if item.seconds < 0.0 { "before timing".to_string() } else { clock_text(item.seconds) };
                ui.text_disabled(format!("{time}  {}", item.name));
            }
        }
    }

    /// Everything picked up in this run, newest first, with where. Rows are all one height, so only those on screen
    /// are laid out, however long the list grows.
    /// Every check the randomizer could fill in this seed, by place. Says where each is and what the unrandomized
    /// game had there, never what it holds now. Ticks here are apart from the Checks tab.
    fn all_tab(&mut self, ui: &Ui, snapshot: &Snapshot) {
        let Some(all) = snapshot.all_places.as_ref() else {
            ui.text_disabled("Reading the full list...");
            return;
        };
        if let Some(message) = &all.message {
            ui.text_wrapped(message);
            return;
        }
        ui.text_colored(GOLD, format!("{} / {} checked, {} open now", all.done, all.total, all.open));
        ui.same_line();
        ui.text_disabled(format!("({} ticked by hand)", all.by_hand));
        if all.overdue > 0 {
            ui.same_line();
            ui.text_colored(YELLOW, format!("{} overdue drops", all.overdue));
        }
        ui.set_next_item_width(110.0);
        ui.combo_simple_string("##allfilter", &mut self.all_filter, &ALL_FILTERS);
        ui.same_line();
        ui.set_next_item_width(130.0);
        ui.combo_simple_string("##allkind", &mut self.all_kind, &ALL_KINDS);
        ui.same_line();
        ui.text_disabled("Every slot of the seed, by the randomizer's own logic.");

        let filter = ALL_FILTERS[self.all_filter];
        let kind = ALL_KINDS[self.all_kind];
        ui.child_window("allchecks").build(|| {
            for place in &all.places {
                let shown = match filter {
                    "Open" => place.open > 0,
                    "Checked" => place.done > 0,
                    "Locked" => place.total - place.done - place.open > 0,
                    "Overdue" => place.name == ANY_AREA && all.overdue > 0,
                    _ => true,
                };
                if !shown {
                    continue;
                }
                let _id = ui.push_id(&place.name);
                let expanded = self.all_place.as_deref() == Some(place.name.as_str());
                let open = if place.open > 0 { format!(", {} open", place.open) } else { String::new() };
                let label = format!("{} {}  ({} / {}{})", if expanded { "-" } else { "+" }, place.name, place.done, place.total, open);
                let colour = if place.done == place.total { GREY } else if place.open > 0 { [1.0, 1.0, 1.0, 1.0] } else { GREY };
                let clicked = {
                    let _c = ui.push_style_color(StyleColor::Text, colour);
                    ui.selectable_config(&label).selected(expanded).build()
                };
                if clicked {
                    self.all_place = if expanded { None } else { Some(place.name.clone()) };
                    crate::worker::set_all_place(self.all_place.clone());
                    self.send(Command::LoadAll);
                }
                if !expanded {
                    continue;
                }
                if snapshot.all_checks_place.as_deref() != Some(place.name.as_str()) {
                    ui.text_disabled("    Reading...");
                    continue;
                }
                ui.indent_by(16.0);
                let mut any = false;
                for check in &snapshot.all_checks {
                    let status_shown = match filter {
                        "Open" => check.status == "available",
                        "Locked" => check.status == "locked",
                        "Checked" => check.status == "checked",
                        "Overdue" => check.overdue,
                        _ => true,
                    };
                    if !status_shown || (kind != "All" && check.kind != kind) {
                        continue;
                    }
                    any = true;
                    let _cid = ui.push_id(&check.id);
                    let mut ticked = check.status == "checked";
                    if ui.checkbox("##tick", &mut ticked) {
                        self.send(Command::SetAllChecked(check.id.clone(), ticked));
                    }
                    ui.same_line();
                    let colour = match check.status.as_str() {
                        "checked" => GREEN,
                        "locked" => GREY,
                        _ => [1.0, 1.0, 1.0, 1.0],
                    };
                    ui.group(|| {
                        ui.text_colored(colour, if check.description.is_empty() { "Somewhere here" } else { &check.description });
                        let mut detail = format!("{}  -  was {}", check.kind, check.replaces);
                        if check.missable {
                            detail.push_str("  -  missable");
                        }
                        if check.by_hand {
                            detail.push_str("  -  tick by hand");
                        }
                        let _d = ui.push_style_color(StyleColor::Text, DETAILS);
                        ui.text_wrapped(detail);
                        if let Some(odds) = &check.odds {
                            ui.text_colored(if check.overdue { YELLOW } else { DETAILS }, odds);
                        }
                    });
                }
                if !any {
                    ui.text_disabled("Nothing here with this filter.");
                }
                ui.unindent_by(16.0);
            }
        });
    }

    fn found_tab(&mut self, ui: &Ui, snapshot: &Snapshot) {
        ui.set_next_item_width(150.0);
        ui.combo_simple_string("##kind", &mut self.found_kind, &FOUND_KINDS);
        ui.same_line();
        ui.set_next_item_width(-1.0);
        ui.input_text("##findsearch", &mut self.found_search).hint("Search items or places").build();

        let key = (snapshot.generation, self.found_kind, self.found_search.clone());
        if self.found_cache.as_ref().map(|(k, _)| k) != Some(&key) {
            let kind = FOUND_KINDS[self.found_kind];
            let search = self.found_search.to_lowercase();
            let shown = snapshot
                .found_items
                .iter()
                .enumerate()
                .filter(|(_, f)| match kind {
                    "All" => true,
                    "Drop or other" => f.source == "Drop or other",
                    _ => f.kinds.iter().any(|k| k == kind),
                })
                .filter(|(_, f)| {
                    search.is_empty()
                        || f.items.iter().any(|i| i.to_lowercase().contains(&search))
                        || f.r#where.to_lowercase().contains(&search)
                        || f.map.to_lowercase().contains(&search)
                })
                .map(|(i, _)| i)
                .collect();
            self.found_cache = Some((key, shown));
        }
        let shown = &self.found_cache.as_ref().unwrap().1;
        if snapshot.poll.as_ref().and_then(|p| p.inventory_readable) == Some(false) {
            ui.text_colored(YELLOW, "Random drops cannot be seen in this game version (the inventory was not found); pickups still are.");
        }
        ui.text_disabled(if snapshot.found_items.is_empty() {
            "Nothing yet. Pickups and other items received show here (needs the run's spoiler log).".to_string()
        } else {
            format!("{} of {} pickups", shown.len(), snapshot.found_items.len())
        });

        ui.child_window("found").build(|| {
            let line = ui.text_line_height_with_spacing();
            let clipper = hudhook::imgui::ListClipper::new(shown.len() as i32).items_height(line * 3.0 + 4.0);
            let mut clipper = clipper.begin(ui);
            while clipper.step() {
                for row in clipper.display_start()..clipper.display_end() {
                    let found = &snapshot.found_items[shown[row as usize]];
                    ui.text_colored(GOLD, found.items.join(", "));
                    let place = match (found.map.is_empty(), found.r#where.is_empty()) {
                        (true, _) => found.r#where.clone(),
                        (false, true) => found.map.clone(),
                        _ => format!("{}: {}", found.map, found.r#where),
                    };
                    ui.text(place);
                    let mut when = match (&found.at, &found.played_time) {
                        (Some(at), Some(time)) => format!("{at} (at {time})"),
                        _ => "Before tracking started".to_string(),
                    };
                    if found.source == "Drop or other" {
                        when.push_str("  -  drop, purchase or crafting");
                    }
                    ui.text_disabled(when);
                    ui.dummy([1.0, 4.0 - ui.clone_style().item_spacing[1]]);
                }
            }
        });
    }

    /// What finishes this run: a goal from the list, and how many for goals that count.
    fn goal_picker(&self, ui: &Ui, snapshot: &Snapshot) {
        ui.text("Goal for this run");
        ui.text_disabled("When it is met the run is marked complete and its timer stops; untick something and it carries on.");
        let kinds = &snapshot.goal_kinds;
        if kinds.is_empty() {
            return;
        }
        let goal = snapshot.summary.goal.as_ref();
        let current = goal.map_or("none", |g| g.kind.as_str());
        let mut index = kinds.iter().position(|k| k.kind == current).unwrap_or(0);
        let names: Vec<&str> = kinds.iter().map(|k| k.name.as_str()).collect();
        ui.set_next_item_width(320.0);
        if ui.combo_simple_string("##goal", &mut index, &names) {
            let count = goal.and_then(|g| g.count);
            self.send(Command::SetGoal(kinds[index].kind.clone(), count));
        }
        if let Some(goal) = goal {
            if kinds.iter().any(|k| k.kind == goal.kind && k.counts) {
                // Buttons rather than typing, so a controller can set it.
                let mut all = goal.count.is_none();
                let mut count = goal.count.unwrap_or(goal.needed);
                let mut changed = false;
                if ui.checkbox("All of them", &mut all) {
                    changed = true;
                }
                if !all {
                    ui.same_line();
                    if ui.small_button("-") && count > 1 {
                        count -= 1;
                        changed = true;
                    }
                    ui.same_line();
                    ui.text(count.to_string());
                    ui.same_line();
                    if ui.small_button("+") {
                        count += 1;
                        changed = true;
                    }
                }
                if changed {
                    self.send(Command::SetGoal(goal.kind.clone(), (!all).then_some(count)));
                }
            }
            let progress = if goal.met { "complete".to_string() } else { format!("{} / {}", goal.done, goal.needed) };
            ui.text_colored(GOLD, format!("{}: {progress}", goal.description));
            if goal.kind == "chosenBosses" {
                self.boss_picker(ui, snapshot, goal);
            }
        }
    }

    /// The bosses a "defeat chosen bosses" goal can name, to tick. A chosen boss shows "defeated" once beaten,
    /// wherever the seed put it; where it is never shows.
    fn boss_picker(&self, ui: &Ui, snapshot: &Snapshot, goal: &crate::model::Goal) {
        ui.text_disabled("Tick the bosses this run needs. Each counts when you defeat that boss, wherever the randomizer put it.");
        let mut all = self.all_bosses.load(Ordering::Relaxed);
        if ui.checkbox("Show other bosses too", &mut all) {
            self.all_bosses.store(all, Ordering::Relaxed);
        }
        let height = ui.text_line_height_with_spacing() * 12.0;
        ui.child_window("bosses").size([0.0, height]).border(true).build(|| {
            for choice in snapshot.boss_choices.iter().filter(|c| all || c.major) {
                let _id = ui.push_id(&choice.id);
                let chosen = goal.bosses.iter().find(|b| b.id == choice.id);
                let mut ticked = chosen.is_some();
                if ui.checkbox(format!("{}  ({})", choice.name, choice.region), &mut ticked) {
                    self.send(Command::ToggleGoalBoss(choice.id.clone()));
                }
                if chosen.is_some_and(|b| b.defeated) {
                    ui.same_line();
                    ui.text_colored(GREEN, "defeated");
                }
            }
        });
    }

    /// A one-line display in the corner that ignores the mouse, for while the panel is closed.
    fn mini_hud(&self, ui: &Ui, snapshot: &Snapshot) {
        if !snapshot.ready {
            return;
        }
        let summary = &snapshot.summary;
        // Top right: the game's health bars are top left, and its compass top centre.
        let width = ui.io().display_size[0];
        ui.window("##mini")
            .position([width - 16.0, 16.0], Condition::Always)
            .position_pivot([1.0, 0.0])
            .bg_alpha(0.45)
            .flags(
                WindowFlags::NO_DECORATION
                    | WindowFlags::NO_INPUTS
                    | WindowFlags::ALWAYS_AUTO_RESIZE
                    | WindowFlags::NO_SAVED_SETTINGS
                    | WindowFlags::NO_FOCUS_ON_APPEARING
                    | WindowFlags::NO_NAV,
            )
            .build(|| {
                if let Some(turn) = self.arrow_turn(snapshot) {
                    draw_arrow(ui, turn, ui.text_line_height_with_spacing() * 3.0);
                    ui.same_line();
                }
                ui.group(|| {
                    let time = if self.config.show_timer && played_seconds(snapshot) >= 1.0 {
                        format!("   {}", clock_text(played_seconds(snapshot)))
                    } else {
                        String::new()
                    };
                    let skipped = if summary.skipped > 0 { format!(", {} skipped", summary.skipped) } else { String::new() };
                    ui.text(format!("{} / {}  ({} open{skipped}){time}", summary.checked, summary.total, summary.open));
                    if let Some(goal) = summary.goal.as_ref().filter(|_| self.config.show_goal) {
                        let text = if goal.met { "Run complete".to_string() } else { format!("Goal {} / {}", goal.done, goal.needed) };
                        ui.text_disabled(text);
                    }
                    if self.config.show_splits {
                        if let Some(split) = summary.splits.last() {
                            self.split_line(ui, split);
                        }
                    }
                    if self.config.show_deaths && summary.deaths > 0 {
                        ui.text_disabled(format!("Deaths: {}", summary.deaths));
                    }
                    // Long lines wrap at the box's usual width, two lines at most, so the box never grows to fit them.
                    let room = mini_room(ui);
                    let wrapped = |colour: [f32; 4], text: &str| {
                        for line in wrap_lines(ui, text, room, 2) {
                            ui.text_colored(colour, line);
                        }
                    };
                    if self.config.show_guide {
                        if let Some(text) = self.tests.guide.clone().or_else(|| guide_text(snapshot)) {
                            wrapped(GOLD, &text);
                        }
                    }
                    if self.config.show_grace {
                        if let Some(text) = grace_text(snapshot) {
                            wrapped(GREY, &text);
                        }
                    }
                    if let Some(text) = dead_end_text(snapshot, 1) {
                        wrapped(YELLOW, &text);
                    }
                    if let Some(problem) = snapshot.poll.as_ref().and_then(|p| tracking_problem(&p.tracking)) {
                        wrapped(YELLOW, &problem);
                    }
                    if self.using_pad {
                        ui.text_disabled(format!("{}: checklist", self.config.pad_toggle));
                    } else {
                        ui.text_disabled(format!("{}: checklist", self.config.toggle_key));
                    }
                });
                self.nearby_list(ui, snapshot);
            });
    }

    /// The nearest available checks that are not targets already, each with a small arrow (or a compass
    /// point when the camera cannot be read) and its distance.
    fn nearby_list(&self, ui: &Ui, snapshot: &Snapshot) {
        if self.config.nearby_checks == 0 {
            return;
        }
        let Some(poll) = &snapshot.poll else { return };
        let targets = &snapshot.summary.targets;
        let nearby: Vec<_> = poll.nearby.iter().filter(|c| !targets.iter().any(|t| t.id == c.id)).take(self.config.nearby_checks).collect();
        if nearby.is_empty() {
            return;
        }
        ui.separator();
        ui.text_disabled("Nearest available");
        let facing = self.facing;
        for check in nearby {
            let way = match facing {
                Some(facing) => {
                    draw_arrow(ui, camera::turn(facing, check.dx, check.dz), ui.text_line_height());
                    ui.same_line();
                    String::new()
                }
                None => format!(" {}", compass(check.dx, check.dz)),
            };
            let entrance = if check.mode == "toEntrance" { ", entrance" } else { "" };
            let after = format!("  {}{way}{entrance}", distance_text(check.distance));
            let name_room = (mini_room(ui) - ui.calc_text_size(&after)[0] - ui.text_line_height()).max(40.0);
            ui.text(format!("{}{after}", fit(ui, &check.name, name_room)));
        }
    }

    /// Steam-style notifications in the bottom-right corner: each find slides up, stays a few seconds
    /// and slides away, newest at the bottom, saying what the find made available. A chime plays when
    /// new ones arrive. Check names only, never what was found.
    fn found_popup(&mut self, ui: &Ui) {
        let now = Instant::now();
        // At most three on screen; the rest wait their turn, as Steam's do.
        let mut fresh = false;
        let mut showing = self.toasts.iter().filter(|t| t.shown.is_some()).count();
        for toast in &mut self.toasts {
            if toast.shown.is_none() && showing < 3 {
                toast.shown = Some(now);
                showing += 1;
                fresh |= !toast.quiet;
            }
        }
        if fresh && self.config.found_sound {
            crate::sound::play(&self.directory, &self.config.found_sound_choice, self.config.found_volume);
        }
        self.toasts.retain(|t| t.shown.is_none_or(|at| now.duration_since(at).as_secs_f32() < TOAST_SECONDS));
        if !self.config.found_popup {
            return;
        }
        let [screen_w, screen_h] = ui.io().display_size;
        let s = screen_h / 1080.0;
        let line = ui.text_line_height();
        let (width, margin, gap) = (360.0 * s, 16.0 * s, 8.0 * s);
        let draw = ui.get_foreground_draw_list();
        let shown: Vec<&Toast> = self.toasts.iter().filter(|t| t.shown.is_some()).collect();
        let mut bottom = screen_h - margin;
        for toast in shown.iter().rev() {
            let unlocked = unlocked_text(ui, &toast.unlocked, width - 92.0 * s);
            let lines = 3.0 + if unlocked.is_some() { 2.0 } else { 0.0 };
            let height = (lines * (line + 3.0 * s) + 18.0 * s).max(70.0 * s);
            let age = now.duration_since(toast.shown.unwrap()).as_secs_f32();
            // Slide in over a quarter second, out over the last 0.4 s, easing at both ends.
            let ease = |x: f32| 1.0 - (1.0 - x.clamp(0.0, 1.0)).powi(3);
            let a = ease(age / 0.25).min(ease((TOAST_SECONDS - age) / 0.4));
            let left = screen_w - margin - width;
            // Fully in: sits on `bottom`. Fully out: just below the bottom edge of the screen.
            let top = bottom - height * a + (1.0 - a) * margin;
            let colour = |rgb: [f32; 3], alpha: f32| [rgb[0], rgb[1], rgb[2], alpha * a];
            let (top_left, bottom_right) = ([left, top], [left + width, top + height]);
            draw.add_rect([left - 1.0, top - 1.0], [left + width + 1.0, top + height + 1.0], colour([0.0, 0.0, 0.0], 0.5)).filled(true).build();
            draw.add_rect_filled_multicolor(
                top_left,
                bottom_right,
                colour([0.19, 0.25, 0.32], 0.97),
                colour([0.19, 0.25, 0.32], 0.97),
                colour([0.11, 0.14, 0.19], 0.97),
                colour([0.11, 0.14, 0.19], 0.97),
            );
            draw.add_rect(top_left, bottom_right, colour([0.32, 0.42, 0.52], 0.9)).thickness(1.0).build();
            let badge_radius = 24.0 * s;
            draw_badge(ui, &draw, [left + 14.0 * s + badge_radius, top + 12.0 * s + badge_radius], badge_radius, Badge::Tick, a);
            let text_left = left + 80.0 * s;
            let room = width - 80.0 * s - 12.0 * s;
            let step = line + 3.0 * s;
            let mut y = top + 9.0 * s;
            draw.add_text([text_left, y], colour([1.0, 1.0, 1.0], 1.0), &toast.title);
            y += step;
            draw.add_text([text_left, y], colour([0.78, 0.84, 0.88], 1.0), fit(ui, &toast.name, room));
            y += step;
            draw.add_text([text_left, y], colour([0.55, 0.62, 0.68], 1.0), fit(ui, &toast.region, room));
            if let Some(unlocked) = unlocked {
                y += step;
                draw.add_text([text_left, y], colour([0.98, 0.80, 0.36], 1.0), "Now available:");
                y += step;
                draw.add_text([text_left, y], colour([0.98, 0.80, 0.36], 1.0), unlocked);
            }
            // Stacks upward: the next (older) one sits above this one, moving with it as it slides.
            bottom = top - gap;
        }
    }

    /// The companion's own icon over each target on the open world map, on whichever map is showing.
    fn map_icons(&self, ui: &Ui, snapshot: &Snapshot) {
        if !self.config.map_icons {
            return;
        }
        // For testing outside the game, which has no map: the five badges in a row, the first active.
        if self.tests.badges {
            let [w, h] = ui.io().display_size;
            let radius = 15.0 * h / 1080.0;
            let draw = ui.get_foreground_draw_list();
            let pulse = (self.started.elapsed().as_secs_f32() % 1.2) / 1.2;
            for n in 1..=5 {
                let at = [w / 2.0 + (n as f32 - 3.0) * radius * 4.0, h / 2.0];
                if n == 1 {
                    draw.add_circle(at, radius * (1.2 + pulse * 0.9), [1.0, 0.82, 0.35, 0.8 * (1.0 - pulse)]).thickness(2.5).build();
                }
                draw_badge(ui, &draw, at, radius, Badge::Number(n), 1.0);
            }
            return;
        }
        let Some(view) = crate::map_view::view() else { return };
        let screen = ui.io().display_size;
        let radius = 15.0 * screen[1] / 1080.0;
        let draw = ui.get_foreground_draw_list();
        // A ring that pulses out from the target the guide follows.
        let pulse = (self.started.elapsed().as_secs_f32() % 1.2) / 1.2;
        for target in &snapshot.summary.targets {
            let Some((x, z, layer)) = crate::map_markers::map_position(&target.id) else { continue };
            if layer != view.layer {
                continue;
            }
            let at = view.project(x, z, screen);
            if at[0] < -radius || at[1] < -radius || at[0] > screen[0] + radius || at[1] > screen[1] + radius {
                continue;
            }
            if target.active {
                draw.add_circle(at, radius * (1.2 + pulse * 0.9), [1.0, 0.82, 0.35, 0.8 * (1.0 - pulse)]).thickness(2.5).build();
            }
            draw_badge(ui, &draw, at, radius, Badge::Number(target.number), 1.0);
        }
    }

    /// How far the active target is turned from where the camera faces, if the arrow can point there.
    fn arrow_turn(&self, snapshot: &Snapshot) -> Option<f32> {
        if !self.config.guide_arrow {
            return None;
        }
        // For testing outside the game, which has no camera: a fixed turn in degrees.
        if let Some(degrees) = self.tests.arrow {
            return Some(degrees.to_radians());
        }
        snapshot.summary.targets.iter().find(|t| t.active)?;
        let guide = snapshot.poll.as_ref()?.guide.as_ref()?;
        let points = matches!(guide.mode.as_str(), "sameMap" | "direct" | "toEntrance");
        if !points || guide.distance < 1.0 {
            return None;
        }
        Some(camera::turn(self.facing?, guide.dx, guide.dz))
    }
}

/// A gold arrow in a `size` square at the cursor: straight up means ahead, turned `turn` radians clockwise.
fn draw_arrow(ui: &Ui, turn: f32, size: f32) {
    ui.dummy([size, size]);
    let [left, top] = ui.item_rect_min();
    let centre = [left + size / 2.0, top + size / 2.0];
    let radius = size * 0.45;
    let (sin, cos) = turn.sin_cos();
    let at = |x: f32, y: f32| [centre[0] + (x * cos - y * sin) * radius, centre[1] + (x * sin + y * cos) * radius];
    let draw = ui.get_window_draw_list();
    draw.add_circle(centre, radius * 1.08, [0.0, 0.0, 0.0, 0.35]).filled(true).build();
    // One solid triangle on a slightly larger dark one: no outline to spike at the tip, no seam down the middle.
    let shadow = |s: f32| (at(0.0, -1.0 * s), at(-0.6 * s, 0.7 * s), at(0.6 * s, 0.7 * s));
    let (a, b, c) = shadow(1.12);
    draw.add_triangle(a, b, c, [0.1, 0.07, 0.0, 0.9]).filled(true).build();
    let (a, b, c) = shadow(1.0);
    draw.add_triangle(a, b, c, GOLD).filled(true).build();
}

impl ImguiRenderLoop for Overlay {
    fn initialize<'a>(&'a mut self, ctx: &mut Context, _render_context: &'a mut dyn RenderContext) {
        ctx.set_ini_filename(Some(self.directory.join("ErRandoCompanionMod.imgui.ini")));
        let height = ctx.io().display_size[1].max(720.0);
        let size = (18.0 * height / 1080.0).clamp(14.0, 40.0);

        // The system's Segoe UI reads far better than ImGui's built-in pixel font at game resolutions.
        let segoe = std::env::var("WINDIR")
            .map(|w| PathBuf::from(w).join("Fonts").join("segoeui.ttf"))
            .ok()
            .and_then(|p| std::fs::read(p).ok());
        let fonts = ctx.fonts();
        match segoe {
            Some(data) => {
                let data: &'static [u8] = Box::leak(data.into_boxed_slice());
                fonts.add_font(&[FontSource::TtfData {
                    data,
                    size_pixels: size,
                    config: Some(FontConfig {
                        glyph_ranges: FontGlyphRanges::from_slice(&GLYPHS),
                        ..FontConfig::default()
                    }),
                }]);
            }
            None => {
                fonts.add_font(&[FontSource::DefaultFontData {
                    config: Some(FontConfig { size_pixels: size, ..FontConfig::default() }),
                }]);
            }
        }

        let io = ctx.io_mut();
        io.config_flags |= ConfigFlags::NAV_ENABLE_GAMEPAD;
        io.backend_flags |= BackendFlags::HAS_GAMEPAD;

        let style = ctx.style_mut();
        style.window_rounding = 6.0;
        style.frame_rounding = 4.0;
        // The tick boxes are disabled on a controller only to keep A off them; they should not look faded.
        style.disabled_alpha = 1.0;
        style.scale_all_sizes(size / 13.0);
    }

    fn before_render<'a>(&'a mut self, ctx: &mut Context, _render_context: &'a mut dyn RenderContext) {
        let started = self.cost.start();
        self.frame = self.frame.wrapping_add(1);
        self.poll_hotkey();
        // A boss health bar on screen closes the panel and keeps everything hidden until the fight ends.
        self.in_boss_fight = self.config.hide_in_boss_fights && crate::boss::in_fight();
        // The game's own menus and cutscenes do the same: the overlay steps aside until the game's HUD is back.
        self.in_menu = self.config.hide_in_menus && crate::boss::in_menu_or_cutscene();
        // No character loaded (the title screen or a loading screen): nothing shows, nothing opens.
        self.out_of_game = self.config.hide_on_main_menu && out_of_game(&self.shared.lock());
        if self.in_boss_fight || self.in_menu || self.out_of_game || self.hidden {
            self.visible = false;
        }
        // A few extra, since checks that are already targets are left out of the list.
        // The found list is fetched only while its tab is on screen, and caught up when it comes back.
        let found_shown = self.visible && self.tab == 6;
        if found_shown && !self.found_shown {
            self.send(Command::LoadFound);
        }
        self.found_shown = found_shown;
        crate::worker::set_found_open(found_shown);
        let progress_shown = self.visible && self.tab == PROGRESS_TAB;
        if progress_shown && !self.progress_shown && !std::mem::take(&mut self.report_requested) {
            self.send(Command::LoadReport(None));
        }
        self.progress_shown = progress_shown;
        crate::worker::set_progress_open(progress_shown);
        // The full list is thousands of checks: its places are read while the All tab shows, its checks one place at a time.
        let all_shown = self.visible && self.tab == ALL_TAB;
        crate::worker::set_all_open(all_shown);
        if all_shown && !self.all_shown {
            self.send(Command::LoadAll);
        }
        self.all_shown = all_shown;
        let shows_nearby = self.config.mini_hud && self.config.nearby_checks > 0 && !self.out_of_game && !self.in_boss_fight && !self.in_menu && !self.hidden;
        crate::worker::set_nearby(if shows_nearby { self.config.nearby_checks + 5 } else { 0 });
        // While the panel is open the game sees an idle controller, keyboard and mouse, and shows its
        // own cursor as it does in its menus. If it cannot, ImGui draws one.
        game_input::set_blocking(self.visible);
        // The game can reset its cursor while the panel is open (a menu, a cutscene), so recheck now and then.
        if self.visible != self.cursor_shown || (self.visible && self.frame % 30 == 0) {
            self.game_cursor = game_input::show_game_cursor(self.visible);
            self.cursor_shown = self.visible;
        }
        // The camera's facing, read once for everything drawn this frame that needs it.
        let wants_camera = self.visible || (self.config.mini_hud && (self.config.guide_arrow || self.config.nearby_checks > 0));
        self.facing = if wants_camera { camera::facing() } else { None };
        let io = ctx.io_mut();
        io.font_global_scale = self.config.text_scale;
        io.mouse_draw_cursor = self.visible && !self.game_cursor;
        self.feed_pad(io);
        self.cost.add(started);
    }

    fn render(&mut self, ui: &mut Ui) {
        let started = self.cost.start();
        self.draw(ui);
        self.cost.add(started);
        self.cost.end_frame();
    }

    fn message_filter(&self, _io: &Io) -> MessageFilter {
        // While the panel is open, keep the mouse and keys from also moving the camera or the character.
        if self.visible {
            MessageFilter::InputAll
        } else {
            MessageFilter::empty()
        }
    }
}

impl Overlay {
    /// Everything drawn this frame.
    fn draw(&mut self, ui: &Ui) {
        let shared = self.shared.clone();
        let mut snapshot = shared.lock();
        // About twice a second, keep the game's beacon on the active target.
        if self.frame % 30 == 0 {
            let target = snapshot
                .summary
                .targets
                .iter()
                .find(|t| t.active && self.config.beacon)
                .and_then(|t| crate::map_markers::surface_position(&t.id));
            self.beacon_error = crate::beacon::follow(target).err();
        }
        // New finds queue up even during a boss fight; they show once it is over.
        for found in snapshot.found.iter().filter(|f| f.number > self.last_found) {
            if self.config.found_popup || self.config.found_sound {
                self.toasts.push(Toast {
                    title: "Check found".into(),
                    name: found.name.clone(),
                    region: found.region.clone(),
                    unlocked: found.unlocked.clone(),
                    shown: None,
                    quiet: false,
                });
            }
        }
        self.last_found = snapshot.found.last().map_or(self.last_found, |f| f.number.max(self.last_found));
        // The goal met just now, in this run (not a finished run being opened): a notification of its own.
        let finished = snapshot.summary.finished;
        if let Some((seed, was_finished)) = &self.last_finished {
            if *seed == snapshot.summary.seed_name && !was_finished && finished {
                let goal = snapshot.summary.goal.as_ref().map_or(String::new(), |g| g.description.clone());
                self.toasts.push(Toast {
                    title: "Run complete!".into(),
                    name: goal,
                    region: format!("Run time {}", clock_text(played_seconds(&snapshot))),
                    unlocked: Vec::new(),
                    shown: None,
                    quiet: false,
                });
            }
        }
        if snapshot.ready {
            match &mut self.last_finished {
                Some((seed, was)) if *seed == snapshot.summary.seed_name => *was = finished,
                _ => self.last_finished = Some((snapshot.summary.seed_name.clone(), finished)),
            }
        }
        if self.hidden {
            self.toasts.clear();
            return;
        }
        // On the title screen (no character loaded), the companion signs its name, like the game's own version line.
        if self.config.title_credit && !CREDIT_IN_GAME.load(Ordering::Relaxed) && (out_of_game(&snapshot) || self.tests.title) {
            title_credit(ui, &self.config.title_credit_corner);
        }
        if self.in_boss_fight || self.in_menu || self.out_of_game {
            return;
        }
        if self.visible {
            self.panel(ui, &mut snapshot);
        } else if self.config.mini_hud {
            self.mini_hud(ui, &snapshot);
        }
        self.map_icons(ui, &snapshot);
        drop(snapshot);
        self.found_popup(ui);
    }
}

/// A region in the Checks tab: its header text, made once, and the checks it shows.
struct RegionGroup {
    header: String,
    checks: Vec<usize>,
}

/// The checks a filter and search show, grouped by region, regions in alphabetical order.
fn group_checks(checks: &[Check], filter: &str, search: &str) -> Vec<RegionGroup> {
    let wanted = filter.to_lowercase();
    let search = search.to_lowercase();
    let mut regions: Vec<(&str, Vec<usize>)> = Vec::new();
    for (i, check) in checks.iter().enumerate() {
        let shown = (wanted == "all" || check.status == wanted)
            && (search.is_empty() || check.name.to_lowercase().contains(&search) || check.region.to_lowercase().contains(&search));
        if !shown {
            continue;
        }
        match regions.iter_mut().find(|(name, _)| *name == check.region) {
            Some((_, list)) => list.push(i),
            None => regions.push((&check.region, vec![i])),
        }
    }
    regions.sort_by(|a, b| a.0.cmp(b.0));
    regions
        .into_iter()
        .map(|(region, checks)| RegionGroup { header: format!("{region} ({})###{region}", checks.len()), checks })
        .collect()
}

/// Settings for testing outside the game, from environment variables read once at start.
struct TestHooks {
    /// The five badges drawn in a row, as there is no map.
    badges: bool,
    /// A fixed arrow turn in degrees, as there is no camera.
    arrow: Option<f32>,
    /// Acts as if on the title screen, which the test window cannot be.
    title: bool,
    /// A target line to show, as there is no player to guide.
    guide: Option<String>,
}

impl TestHooks {
    fn read() -> Self {
        Self {
            badges: std::env::var_os("ER_MOD_TEST_BADGES").is_some(),
            arrow: std::env::var("ER_MOD_TEST_ARROW").ok().and_then(|d| d.parse().ok()),
            title: std::env::var_os("ER_MOD_TEST_TITLE").is_some(),
            guide: std::env::var("ER_MOD_TEST_GUIDE").ok(),
        }
    }
}

/// Whether any physical key or mouse button is down, for when the game does not read them through
/// DirectInput. Skips the virtual keys Windows gives game controllers (0xC3 to 0xDA).
fn any_key_down() -> bool {
    (0x01..=0xFEu8)
        .filter(|vk| !(0xC3..=0xDA).contains(vk))
        .any(|vk| unsafe { GetAsyncKeyState(vk as i32) } as u16 & 0x8000 != 0)
}

/// "Target 2, Raya Lucaria Gate: 1.9 km SE, to the entrance". Uses the game's axes: +x east, +z north.
fn guide_text(snapshot: &Snapshot) -> Option<String> {
    let target = snapshot.summary.targets.iter().find(|t| t.active)?;
    let label = format!("Target {}, {}", target.number, target.name);
    let Some(guide) = snapshot.poll.as_ref().and_then(|p| p.guide.as_ref()) else {
        return Some(label);
    };
    Some(format!("{label}: {}", route_text(guide)))
}

/// The way to the target: warp to a grace first when that is shorter, through a dungeon's entrance, and how far up or
/// down it is. Walks are straight-line estimates between those points.
fn route_text(guide: &crate::model::Guide) -> String {
    let distance = distance_text(guide.distance);
    let direction = compass(guide.dx, guide.dz);
    if let (Some(grace), Some(walk)) = (&guide.warp_to, guide.walk) {
        let from_here = match guide.mode.as_str() {
            "otherRealm" => " (in the other realm)".to_string(),
            "unknown" => String::new(),
            _ => format!(" ({distance} {direction} from here)"),
        };
        return format!("warp to {grace}, then {}{from_here}", distance_text(walk));
    }
    let rise = match guide.rise {
        Some(r) if r >= 15.0 => format!(", {} up", distance_text(r)),
        Some(r) if r <= -15.0 => format!(", {} down", distance_text(-r)),
        _ => String::new(),
    };
    match guide.mode.as_str() {
        "toEntrance" => {
            let inside = guide.walk.map(|w| w - guide.distance).filter(|i| *i >= 10.0);
            let inside = inside.map_or(String::new(), |i| format!(", then {} inside", distance_text(i)));
            format!("{distance} {direction} to its entrance{inside}")
        }
        "otherRealm" => "in the other realm (no grace found there yet)".to_string(),
        "unknown" => "no way to point there from here".to_string(),
        _ => format!("{distance} {direction}{rise}"),
    }
}

/// The companion's name, version and author, as the title screen shows it.
pub const CREDIT: &str = concat!("Elden Ring Randomizer Companion v", env!("CARGO_PKG_VERSION"), " by Moordoor");

/// Whether the credit is in the game's own title screen text, so the overlay need not draw it.
static CREDIT_IN_GAME: AtomicBool = AtomicBool::new(false);

pub fn set_credit_in_game(in_game: bool) {
    CREDIT_IN_GAME.store(in_game, Ordering::Relaxed);
}

/// Draws the credit small in a corner of the screen, with a soft shadow so it reads over the title art.
fn title_credit(ui: &Ui, corner: &str) {
    let [w, h] = ui.io().display_size;
    let margin = 18.0 * h / 1080.0;
    let [tw, th] = ui.calc_text_size(CREDIT);
    let x = if corner.ends_with("left") { margin } else { w - margin - tw };
    let y = if corner.starts_with("top") { margin } else { h - margin - th };
    let draw = ui.get_foreground_draw_list();
    draw.add_text([x + 1.0, y + 1.0], [0.0, 0.0, 0.0, 0.7], CREDIT);
    draw.add_text([x, y], [0.86, 0.82, 0.70, 0.85], CREDIT);
}

/// The found grace closest to the active target, to warp to.
fn grace_text(snapshot: &Snapshot) -> Option<String> {
    let guide = snapshot.poll.as_ref()?.guide.as_ref()?;
    // The route already says which grace to warp to.
    if guide.warp_to.is_some() {
        return None;
    }
    let grace = guide.grace.as_ref()?;
    let distance = guide.grace_distance.map_or(String::new(), |d| format!(" ({} from the target)", distance_text(d)));
    Some(format!("Nearest grace: {grace}{distance}"))
}

/// When few checks are open: which missing key items would open the most (logic only, never where they are).
fn dead_end_text(snapshot: &Snapshot, items: usize) -> Option<String> {
    let summary = &snapshot.summary;
    if !summary.dead_end {
        return None;
    }
    let open = match summary.open {
        0 => "Nothing open".to_string(),
        1 => "Only 1 open check".to_string(),
        n => format!("Only {n} open checks"),
    };
    let best: Vec<String> = snapshot.unlocks.iter().take(items).map(|u| format!("{} (opens {})", u.item, u.opens)).collect();
    let skipped = if summary.skipped > 0 { format!(", or try the {} skipped", summary.skipped) } else { String::new() };
    Some(if best.is_empty() {
        format!("{open}. No single key item would open more{skipped}.")
    } else {
        format!("{open}. Most useful to find: {}{skipped}.", best.join(", "))
    })
}

/// True on the title screen and during loading: the game is being read but has no player. When the
/// game cannot be read at all, or the tracker failed, this is false, so the overlay can still explain why.
fn out_of_game(snapshot: &Snapshot) -> bool {
    static START_OPEN: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    if snapshot.fatal.is_some() || *START_OPEN.get_or_init(|| std::env::var_os("ER_MOD_START_OPEN").is_some()) {
        return false;
    }
    match &snapshot.poll {
        None => snapshot.ready,
        // A game version whose player position cannot be found would look like the title screen forever.
        Some(poll) => {
            matches!(poll.status.as_str(), "connected" | "loading") && poll.player.is_none() && poll.position_supported != Some(false)
        }
    }
}

/// A short warning for the corner display when auto-tracking is off for a reason the player can act on.
fn tracking_problem(state: &str) -> Option<&'static str> {
    match state {
        "noLog" => Some("Auto-tracking off: no spoiler log"),
        "unsupported" => Some("Auto-tracking off: game version not supported"),
        "blocked" | "unavailable" => Some("Auto-tracking off: the game cannot be read"),
        _ => None,
    }
}

/// The run's played time: from the latest poll, which comes every second, else from the summary.
fn played_seconds(snapshot: &Snapshot) -> f64 {
    snapshot.poll.as_ref().map_or(0.0, |p| p.played_seconds).max(snapshot.summary.played_seconds)
}

/// "1:02:03", or "2:03" under an hour.
fn clock_text(seconds: f64) -> String {
    let total = seconds.max(0.0) as u64;
    let (hours, minutes, secs) = (total / 3600, total / 60 % 60, total % 60);
    if hours > 0 {
        format!("{hours}:{minutes:02}:{secs:02}")
    } else {
        format!("{minutes}:{secs:02}")
    }
}

/// Checks per hour of play, once there are five minutes to go on.
fn per_hour(checked: i32, seconds: f64) -> Option<f64> {
    (seconds >= 300.0).then(|| checked as f64 / (seconds / 3600.0))
}

fn distance_text(metres: f64) -> String {
    if metres >= 1000.0 {
        format!("{:.1} km", metres / 1000.0)
    } else {
        format!("{metres:.0} m")
    }
}

fn compass(dx: f64, dz: f64) -> &'static str {
    const POINTS: [&str; 8] = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
    if dx.abs() < 1.0 && dz.abs() < 1.0 {
        return "here";
    }
    let bearing = dx.atan2(dz).to_degrees().rem_euclid(360.0);
    POINTS[((bearing + 22.5) / 45.0) as usize % 8]
}

#[cfg(test)]
mod tests {
    use super::{compass, out_of_game, route_text};
    use crate::model::Poll;
    use crate::worker::Snapshot;

    #[test]
    fn formats_played_time_and_pace() {
        assert_eq!(super::clock_text(0.0), "0:00");
        assert_eq!(super::clock_text(125.9), "2:05");
        assert_eq!(super::clock_text(3723.0), "1:02:03");
        assert_eq!(super::per_hour(10, 200.0), None, "too early to mean anything");
        assert_eq!(super::per_hour(30, 7200.0), Some(15.0));
    }

    #[test]
    fn hides_only_while_the_game_has_no_player() {
        let with = |status: &str, player: bool| Snapshot {
            ready: true,
            poll: Some(Poll { status: status.into(), player: player.then(|| serde_json::json!({ "x": 1.0 })), ..Default::default() }),
            ..Default::default()
        };
        assert!(out_of_game(&with("connected", false)), "title screen");
        assert!(out_of_game(&with("loading", false)), "loading screen");
        assert!(!out_of_game(&with("connected", true)), "in the world");
        assert!(!out_of_game(&with("unavailable", false)), "the game cannot be read, so the overlay can say why");
        assert!(!out_of_game(&with("unsupported", false)), "an unsupported game version");
        let mut no_position = with("connected", false);
        no_position.poll.as_mut().unwrap().position_supported = Some(false);
        assert!(!out_of_game(&no_position), "a version whose position cannot be found would otherwise stay hidden");
        assert!(!out_of_game(&Snapshot { fatal: Some("broken".into()), ..Default::default() }));
    }

    #[test]
    fn route_text_says_warp_entrance_and_height() {
        let mut guide = crate::model::Guide { mode: "direct".into(), dx: 0.0, dz: 500.0, distance: 500.0, ..Default::default() };
        assert_eq!(route_text(&guide), "500 m N");
        guide.rise = Some(-40.0);
        assert_eq!(route_text(&guide), "500 m N, 40 m down");
        guide.warp_to = Some("Church of Elleh".into());
        guide.walk = Some(120.0);
        assert_eq!(route_text(&guide), "warp to Church of Elleh, then 120 m (500 m N from here)");
        let entrance = crate::model::Guide { mode: "toEntrance".into(), dz: 200.0, distance: 200.0, walk: Some(450.0), ..Default::default() };
        assert_eq!(route_text(&entrance), "200 m N to its entrance, then 250 m inside");
        let other = crate::model::Guide { mode: "otherRealm".into(), ..Default::default() };
        assert_eq!(route_text(&other), "in the other realm (no grace found there yet)");
    }

    #[test]
    fn compass_uses_plus_z_as_north() {
        assert_eq!(compass(0.0, 100.0), "N");
        assert_eq!(compass(100.0, 0.0), "E");
        assert_eq!(compass(0.0, -100.0), "S");
        assert_eq!(compass(-70.0, -70.0), "SW");
        assert_eq!(compass(0.2, 0.1), "here");
    }
}
