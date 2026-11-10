#![allow(non_snake_case)] // The DLL is named ErRandoCompanionMod to match the tracker DLL beside it.

//! ErRandoCompanion's in-game overlay. Mod Engine 2 or me3 loads this DLL into Elden Ring; it loads the
//! tracker (ErRandoCompanion.Native.dll) from its own folder and draws the checklist inside the game.

mod beacon;
mod boss;
mod camera;
mod config;
mod game_input;
mod log;
mod map_markers;
mod map_view;
mod memory;
mod model;
mod native;
mod sound;
mod overlay;
mod profile;
mod worker;

use std::ffi::c_void;
use std::path::PathBuf;
use std::time::{Duration, Instant};

use hudhook::hooks::dx12::ImguiDx12Hooks;
use hudhook::Hudhook;
use windows::core::BOOL;
use windows::Win32::Foundation::{HINSTANCE, HMODULE, HWND, LPARAM};
use windows::Win32::System::LibraryLoader::GetModuleFileNameW;
use windows::Win32::System::SystemServices::DLL_PROCESS_ATTACH;
use windows::Win32::System::Threading::GetCurrentProcessId;
use windows::Win32::UI::WindowsAndMessaging::{EnumWindows, GetWindowThreadProcessId, IsWindowVisible};

/// Entry point. Only starts a thread: DllMain must not wait, load libraries or touch graphics.
#[no_mangle]
pub unsafe extern "system" fn DllMain(module: HINSTANCE, reason: u32, _: *mut c_void) -> BOOL {
    if reason == DLL_PROCESS_ATTACH {
        let directory = module_directory(module);
        let module_raw = module.0 as usize;
        std::thread::spawn(move || start(directory, HINSTANCE(module_raw as *mut c_void)));
    }
    BOOL(1)
}

fn start(directory: PathBuf, module: HINSTANCE) {
    log::set_directory(&directory);
    let (shared, commands) = worker::start(directory.clone());
    // Before the game first reads the controller, so the overlay sees it from the start.
    game_input::install();
    camera::install();
    let config = config::Config::load(&directory);
    map_markers::load_pins(&directory);
    if config.map_markers {
        map_markers::install(config.map_icon);
    }
    beacon::install();
    boss::install();
    map_view::install();

    // Mod Engine loads mods before the game has made its window; hook once there is something to draw on.
    if !wait_for_game_window(Duration::from_secs(300)) {
        log::write("Elden Ring never opened a window, so the overlay was not started.");
        return;
    }
    let overlay = overlay::Overlay::new(directory, shared, commands);
    if let Err(e) = Hudhook::builder().with::<ImguiDx12Hooks>(overlay).with_hmodule(module).build().apply() {
        log::write(&format!("Could not hook the game's DirectX 12 rendering: {e:?}"));
    }
}

fn module_directory(module: HINSTANCE) -> PathBuf {
    let mut buffer = vec![0u16; 32768];
    let length = unsafe { GetModuleFileNameW(Some(HMODULE(module.0)), &mut buffer) } as usize;
    let path = PathBuf::from(String::from_utf16_lossy(&buffer[..length]));
    path.parent().map(PathBuf::from).unwrap_or_default()
}

fn wait_for_game_window(limit: Duration) -> bool {
    unsafe extern "system" fn visit(window: HWND, found: LPARAM) -> BOOL {
        let mut process = 0;
        unsafe {
            GetWindowThreadProcessId(window, Some(&mut process));
            if process == GetCurrentProcessId() && IsWindowVisible(window).as_bool() {
                *(found.0 as *mut bool) = true;
                return BOOL(0);
            }
        }
        BOOL(1)
    }

    let started = Instant::now();
    while started.elapsed() < limit {
        let mut found = false;
        unsafe {
            let _ = EnumWindows(Some(visit), LPARAM(&mut found as *mut bool as isize));
        }
        if found {
            // Give the game a moment to create its swap chain after the window appears.
            std::thread::sleep(Duration::from_secs(2));
            return true;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
    false
}
