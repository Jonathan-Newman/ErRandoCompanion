//! ErRandoCompanionMod.ini beside the DLL: the hotkey, text size and the small always-on display.

use std::path::{Path, PathBuf};

use windows::Win32::UI::Input::KeyboardAndMouse::*;

pub struct Config {
    path: PathBuf,
    pub toggle_key: String,
    pub text_scale: f32,
    pub mini_hud: bool,
    pub guide_arrow: bool,
    /// Targets as icons on the game's own map, and which icon they use.
    pub map_markers: bool,
    pub map_icon: u16,
    /// The game's beacon on the active target.
    pub beacon: bool,
    /// How many of the nearest available checks the corner display lists (0 = none).
    pub nearby_checks: usize,
    /// Hides the overlay, and keeps it closed, while a boss health bar is on screen.
    pub hide_in_boss_fights: bool,
    /// Hides the overlay, and keeps it closed, while the game has put its HUD away: at a grace, in a shop or an NPC's
    /// menu, and in cutscenes.
    pub hide_in_menus: bool,
    /// A popup, and a sound, when auto-tracking ticks a check.
    pub found_popup: bool,
    pub found_sound: bool,
    /// Which sound (see sound::CHOICES) and how loud, 0 to 100.
    pub found_sound_choice: String,
    pub found_volume: u32,
    /// The companion's own icons drawn over the targets on the game's world map.
    pub map_icons: bool,
    /// Keeps the overlay hidden and closed on the title screen and during loading screens.
    pub hide_on_main_menu: bool,
    /// The run's played time in the small display.
    pub show_timer: bool,
    /// The companion's name, version and author on the title screen, and in which corner.
    pub title_credit: bool,
    pub title_credit_corner: String,
    /// Controller buttons held together to open or close the panel, such as "L3+R3".
    pub pad_toggle: String,
    /// The rest of the active layout's small display: the goal, the target line, the grace to warp to, the latest
    /// split, and the death count.
    pub show_goal: bool,
    pub show_guide: bool,
    pub show_grace: bool,
    pub show_splits: bool,
    pub show_deaths: bool,
    /// Which layout is active (0 play, 1 plan), and the other one, kept aside until it is switched to.
    pub layout: usize,
    other_layout: Layout,
    /// Keys and controller combos (empty for none) that skip the nearest check, switch layouts, and hide everything.
    pub skip_key: String,
    pub layout_key: String,
    pub hide_key: String,
    pub pad_skip: String,
    pub pad_layout: String,
    pub pad_hide: String,
    /// Parsed once from the keys and combos, as they are checked every frame.
    toggle_vk: VIRTUAL_KEY,
    pad_combo: u16,
    hotkeys: [(VIRTUAL_KEY, u16); 3],
}

pub const LAYOUT_NAMES: [&str; 2] = ["Play", "Plan"];

/// Where the title screen credit can go.
pub const CORNERS: [&str; 4] = ["top-left", "top-right", "bottom-left", "bottom-right"];

/// What the small display shows. Two are kept, for playing and for planning, and one key swaps them.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct Layout {
    pub hud: bool,
    pub arrow: bool,
    pub timer: bool,
    pub goal: bool,
    pub guide: bool,
    pub grace: bool,
    pub splits: bool,
    pub deaths: bool,
    pub nearby: usize,
}

impl Layout {
    pub const PLAN: Layout = Layout { hud: true, arrow: true, timer: true, goal: true, guide: true, grace: true, splits: true, deaths: true, nearby: 8 };

    /// "hud arrow timer nearby=3": the parts that are on, and the nearby count.
    fn parse(text: &str) -> Layout {
        let words: Vec<&str> = text.split_whitespace().collect();
        let on = |name: &str| words.contains(&name);
        let nearby = words.iter().find_map(|w| w.strip_prefix("nearby=")).and_then(|n| n.parse().ok()).unwrap_or(0usize).min(10);
        Layout {
            hud: on("hud"),
            arrow: on("arrow"),
            timer: on("timer"),
            goal: on("goal"),
            guide: on("guide"),
            grace: on("grace"),
            splits: on("splits"),
            deaths: on("deaths"),
            nearby,
        }
    }

    fn text(&self) -> String {
        let parts = [
            (self.hud, "hud"),
            (self.arrow, "arrow"),
            (self.timer, "timer"),
            (self.goal, "goal"),
            (self.guide, "guide"),
            (self.grace, "grace"),
            (self.splits, "splits"),
            (self.deaths, "deaths"),
        ];
        let mut words: Vec<String> = parts.iter().filter(|p| p.0).map(|p| p.1.to_string()).collect();
        words.push(format!("nearby={}", self.nearby));
        words.join(" ")
    }
}

impl Config {
    pub fn load(directory: &Path) -> Self {
        let mut config = Self {
            path: directory.join("ErRandoCompanionMod.ini"),
            toggle_key: "F9".into(),
            text_scale: 1.0,
            mini_hud: true,
            guide_arrow: true,
            map_markers: true,
            map_icon: 393,
            beacon: true,
            nearby_checks: 3,
            hide_in_boss_fights: true,
            hide_in_menus: true,
            found_popup: true,
            found_sound: true,
            found_sound_choice: "chime".into(),
            found_volume: 80,
            map_icons: true,
            hide_on_main_menu: true,
            show_timer: true,
            title_credit: true,
            title_credit_corner: "bottom-left".into(),
            pad_toggle: "L3+R3".into(),
            show_goal: true,
            show_guide: true,
            show_grace: true,
            show_splits: false,
            show_deaths: false,
            layout: 0,
            other_layout: Layout::PLAN,
            skip_key: "F7".into(),
            layout_key: "F6".into(),
            hide_key: "F10".into(),
            pad_skip: String::new(),
            pad_layout: String::new(),
            pad_hide: String::new(),
            toggle_vk: VK_F9,
            pad_combo: pad::L3 | pad::R3,
            hotkeys: [(VIRTUAL_KEY(0), 0); 3],
        };
        let mut layouts: [Option<Layout>; 2] = [None, None];
        match std::fs::read_to_string(&config.path) {
            Ok(text) => {
                for line in text.lines() {
                    let Some((key, value)) = line.split_once('=') else { continue };
                    let value = value.trim();
                    match key.trim() {
                        "toggle_key" if key_code(value).is_some() => config.toggle_key = value.to_uppercase(),
                        "text_scale" => config.text_scale = value.parse().unwrap_or(1.0f32).clamp(0.5, 3.0),
                        "mini_hud" => config.mini_hud = value != "false",
                        "guide_arrow" => config.guide_arrow = value != "false",
                        "map_markers" => config.map_markers = value != "false",
                        "map_icon" => config.map_icon = value.parse().unwrap_or(393),
                        "beacon_on_target" => config.beacon = value != "false",
                        "nearby_checks" => config.nearby_checks = value.parse::<usize>().unwrap_or(3).min(10),
                        "hide_in_boss_fights" => config.hide_in_boss_fights = value != "false",
                        "hide_in_menus" => config.hide_in_menus = value != "false",
                        "found_popup" => config.found_popup = value != "false",
                        "found_sound" => config.found_sound = value != "false",
                        "found_sound_choice" if crate::sound::CHOICES.iter().any(|c| c.0 == value) => config.found_sound_choice = value.to_string(),
                        "found_volume" => config.found_volume = value.parse::<u32>().unwrap_or(80).min(100),
                        "map_icons" => config.map_icons = value != "false",
                        "hide_on_main_menu" => config.hide_on_main_menu = value != "false",
                        "show_timer" => config.show_timer = value != "false",
                        "pad_toggle" if pad_buttons(value).is_some() => config.pad_toggle = value.to_uppercase(),
                        "title_credit" => config.title_credit = value != "false",
                        "title_credit_corner" if CORNERS.contains(&value) => config.title_credit_corner = value.to_string(),
                        "layout" => config.layout = usize::from(value.eq_ignore_ascii_case("plan")),
                        "play_layout" => layouts[0] = Some(Layout::parse(value)),
                        "plan_layout" => layouts[1] = Some(Layout::parse(value)),
                        "skip_key" if value.is_empty() || key_code(value).is_some() => config.skip_key = value.to_uppercase(),
                        "layout_key" if value.is_empty() || key_code(value).is_some() => config.layout_key = value.to_uppercase(),
                        "hide_key" if value.is_empty() || key_code(value).is_some() => config.hide_key = value.to_uppercase(),
                        "pad_skip" if value.is_empty() || pad_buttons(value).is_some() => config.pad_skip = value.to_uppercase(),
                        "pad_layout" if value.is_empty() || pad_buttons(value).is_some() => config.pad_layout = value.to_uppercase(),
                        "pad_hide" if value.is_empty() || pad_buttons(value).is_some() => config.pad_hide = value.to_uppercase(),
                        _ => {}
                    }
                }
            }
            Err(_) => {}
        }
        // Older settings files have no layouts: their small display settings become the play layout.
        let play = layouts[0].unwrap_or_else(|| config.capture());
        let plan = layouts[1].unwrap_or(Layout::PLAN);
        let (active, other) = if config.layout == 1 { (plan, play) } else { (play, plan) };
        config.apply(active);
        config.other_layout = other;
        config.toggle_vk = key_code(&config.toggle_key).unwrap_or(VK_F9);
        config.pad_combo = pad_buttons(&config.pad_toggle).unwrap_or(pad::L3 | pad::R3);
        config.hotkeys = [&config.skip_key, &config.layout_key, &config.hide_key]
            .map(|k| key_code(k).unwrap_or(VIRTUAL_KEY(0)))
            .into_iter()
            .zip([&config.pad_skip, &config.pad_layout, &config.pad_hide].map(|p| pad_buttons(p).unwrap_or(0)))
            .collect::<Vec<_>>()
            .try_into()
            .unwrap();
        // Writes the file back so settings added by a newer version show up in it.
        config.save();
        config
    }

    pub fn save(&self) {
        let active = self.capture();
        let (play, plan) = if self.layout == 1 { (self.other_layout, active) } else { (active, self.other_layout) };
        let text = format!(
            "; ErRandoCompanion overlay settings.\n\
             ; toggle_key: F1-F12, Insert, Delete, Home, End, PageUp, PageDown, Pause or ScrollLock\n\
             ; pad_toggle: controller buttons held together, joined by +, from A B X Y LB RB Back Start L3 R3 Up Down Left Right\n\
             ; map_markers: targets as icons on the game's own map (takes effect next launch); map_icon: which icon\n\
             toggle_key={}\npad_toggle={}\ntext_scale={:.2}\nmap_markers={}\nmap_icon={}\n\
             ; skip_key / pad_skip: marks the nearest available check \"skipped for now\"\n\
             ; layout_key / pad_layout: swaps the small display between the play and plan layouts\n\
             ; hide_key / pad_hide: hides everything the overlay draws at once, and again to show it (for recording)\n\
             ; (pad_ combos are off when empty; keys can be emptied too)\n\
             skip_key={}\nlayout_key={}\nhide_key={}\npad_skip={}\npad_layout={}\npad_hide={}\n\
             ; layout: play or plan. Each layout lists what the small display shows: hud (the display itself), arrow,\n\
             ; timer, goal, guide (the target line), grace (the grace to warp to), splits, deaths, and nearby=N checks\n\
             layout={}\nplay_layout={}\nplan_layout={}\n\
             ; beacon_on_target: puts the game's beacon (column of light) on the active target\n\
             beacon_on_target={}\n\
             ; hide_in_boss_fights: hides the overlay and keeps it closed while a boss health bar is showing\n\
             hide_in_boss_fights={}\n\
             ; hide_in_menus: hides the overlay and keeps it closed at a grace, in shops and NPC menus, and in cutscenes\n\
             hide_in_menus={}\n\
             ; found_popup / found_sound: a popup and a sound when a check is ticked automatically\n\
             ; (put a ErRandoCompanionMod.wav beside the DLL to use your own sound)\n\
             found_popup={}\nfound_sound={}\n\
             ; found_sound_choice: chime, bell, sparkle, windows or custom; found_volume: 0 to 100\n\
             found_sound_choice={}\nfound_volume={}\n\
             ; map_icons: the companion's own icons drawn over the targets on the game's world map\n\
             map_icons={}\n\
             ; hide_on_main_menu: hidden and closed on the title screen and loading screens (turn off if the\n\
             ; overlay never appears after a game update)\n\
             hide_on_main_menu={}\n\
             ; title_credit: the companion's name, version and author in the title screen's version text, bottom right;\n\
             ; title_credit_corner (top-left, top-right, bottom-left or bottom-right) is only used if that text cannot be changed\n\
             title_credit={}\ntitle_credit_corner={}\n\
             ",
            self.toggle_key, self.pad_toggle, self.text_scale, self.map_markers, self.map_icon,
            self.skip_key, self.layout_key, self.hide_key, self.pad_skip, self.pad_layout, self.pad_hide,
            LAYOUT_NAMES[self.layout].to_lowercase(), play.text(), plan.text(),
            self.beacon, self.hide_in_boss_fights, self.hide_in_menus, self.found_popup, self.found_sound, self.found_sound_choice, self.found_volume, self.map_icons, self.hide_on_main_menu,
            self.title_credit, self.title_credit_corner
        );
        let _ = std::fs::write(&self.path, text);
    }

    /// The active layout, from the settings the overlay reads.
    fn capture(&self) -> Layout {
        Layout {
            hud: self.mini_hud,
            arrow: self.guide_arrow,
            timer: self.show_timer,
            goal: self.show_goal,
            guide: self.show_guide,
            grace: self.show_grace,
            splits: self.show_splits,
            deaths: self.show_deaths,
            nearby: self.nearby_checks,
        }
    }

    fn apply(&mut self, layout: Layout) {
        self.mini_hud = layout.hud;
        self.guide_arrow = layout.arrow;
        self.show_timer = layout.timer;
        self.show_goal = layout.goal;
        self.show_guide = layout.guide;
        self.show_grace = layout.grace;
        self.show_splits = layout.splits;
        self.show_deaths = layout.deaths;
        self.nearby_checks = layout.nearby;
    }

    /// Swaps to the other layout and saves.
    pub fn switch_layout(&mut self) {
        let current = self.capture();
        let other = self.other_layout;
        self.apply(other);
        self.other_layout = current;
        self.layout ^= 1;
        self.save();
    }

    /// The skip, layout and hide hotkeys: each a key (0 for none) and controller buttons (0 for none).
    pub fn hotkeys(&self) -> [(VIRTUAL_KEY, u16); 3] {
        self.hotkeys
    }

    pub fn toggle_vk(&self) -> VIRTUAL_KEY {
        self.toggle_vk
    }

    /// The XInput button bits for the controller combo.
    pub fn pad_toggle_buttons(&self) -> u16 {
        self.pad_combo
    }
}

/// XInput button bits.
pub mod pad {
    pub const UP: u16 = 0x0001;
    pub const DOWN: u16 = 0x0002;
    pub const LEFT: u16 = 0x0004;
    pub const RIGHT: u16 = 0x0008;
    pub const START: u16 = 0x0010;
    pub const BACK: u16 = 0x0020;
    pub const L3: u16 = 0x0040;
    pub const R3: u16 = 0x0080;
    pub const LB: u16 = 0x0100;
    pub const RB: u16 = 0x0200;
    pub const A: u16 = 0x1000;
    pub const B: u16 = 0x2000;
    pub const X: u16 = 0x4000;
    pub const Y: u16 = 0x8000;
}

fn pad_buttons(text: &str) -> Option<u16> {
    let mut buttons = 0;
    for name in text.split('+') {
        buttons |= match name.trim().to_uppercase().as_str() {
            "A" => pad::A,
            "B" => pad::B,
            "X" => pad::X,
            "Y" => pad::Y,
            "LB" => pad::LB,
            "RB" => pad::RB,
            "BACK" | "VIEW" | "SELECT" => pad::BACK,
            "START" | "MENU" => pad::START,
            "L3" => pad::L3,
            "R3" => pad::R3,
            "UP" => pad::UP,
            "DOWN" => pad::DOWN,
            "LEFT" => pad::LEFT,
            "RIGHT" => pad::RIGHT,
            _ => return None,
        };
    }
    (buttons != 0).then_some(buttons)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn layouts_round_trip_and_swap() {
        let layout = Layout { hud: true, arrow: false, timer: true, goal: false, guide: true, grace: false, splits: true, deaths: true, nearby: 4 };
        assert_eq!(Layout::parse(&layout.text()), layout);

        let folder = std::env::temp_dir().join(format!("er_mod_config_test_{}", std::process::id()));
        std::fs::create_dir_all(&folder).unwrap();
        // An older file: its small display settings become the play layout.
        std::fs::write(folder.join("ErRandoCompanionMod.ini"), "mini_hud=true\nnearby_checks=2\nshow_timer=false\nhide_key=F11\npad_hide=LB+RB\n").unwrap();
        let mut config = Config::load(&folder);
        assert_eq!((config.layout, config.nearby_checks, config.show_timer), (0, 2, false));
        assert_eq!(config.hotkeys()[2], (VK_F11, pad::LB | pad::RB));
        config.switch_layout();
        assert_eq!((config.layout, config.nearby_checks, config.show_splits), (1, 8, true));
        config.nearby_checks = 6;
        config.save();
        let config = Config::load(&folder);
        assert_eq!((config.layout, config.nearby_checks), (1, 6));
        assert_eq!(config.other_layout.nearby, 2);
        let _ = std::fs::remove_dir_all(&folder);
    }
}

fn key_code(name: &str) -> Option<VIRTUAL_KEY> {
    let name = name.to_uppercase();
    if let Some(n) = name.strip_prefix('F').and_then(|n| n.parse::<u16>().ok()) {
        return (1..=12).contains(&n).then(|| VIRTUAL_KEY(VK_F1.0 + n - 1));
    }
    Some(match name.as_str() {
        "INSERT" => VK_INSERT,
        "DELETE" => VK_DELETE,
        "HOME" => VK_HOME,
        "END" => VK_END,
        "PAGEUP" => VK_PRIOR,
        "PAGEDOWN" => VK_NEXT,
        "PAUSE" => VK_PAUSE,
        "SCROLLLOCK" => VK_SCROLL,
        _ => return None,
    })
}
