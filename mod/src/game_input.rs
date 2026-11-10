//! What the overlay needs from the game's own input while its panel is open:
//! - the game's "show the mouse cursor" flag, so Windows hands the mouse back as it does in the game's menus;
//! - the controller, read through a hook on XInputGetState, and hidden from the game while the panel is open;
//! - the keyboard and mouse the game reads through DirectInput, also hidden while the panel is open.
//!
//! All of it is written for this mod; the cursor flag's location (CSMenuManImp + 0xAC, bit 0) and the
//! pattern that finds CSMenuManImp are the ones the Elden Ring modding community has published.

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicU16, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};

use hudhook::mh::{MH_ApplyQueued, MH_Initialize, MhHook, MH_STATUS};
use windows::core::{s, GUID, HSTRING};
use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress, LoadLibraryW};
use windows::Win32::System::SystemInformation::GetSystemDirectoryW;
use windows::Win32::UI::Input::XboxController::{XINPUT_GAMEPAD, XINPUT_STATE};

use crate::{log, memory};

/// While set, the game sees an idle controller, keyboard and mouse.
static BLOCK: AtomicBool = AtomicBool::new(false);
/// Controller buttons hidden from the game even while not blocking: the toggle combo while it is held.
static MASK: AtomicU16 = AtomicU16::new(0);
/// The last real state of each controller slot, before anything is hidden from the game.
static PADS: Mutex<[Option<XINPUT_GAMEPAD>; 4]> = Mutex::new([None; 4]);
/// Address of the game's global pointer to CSMenuManImp, or 0 if it was not found.
static MENU_MAN: AtomicUsize = AtomicUsize::new(0);
/// Counts reads in which the game's keyboard or mouse showed real use: a key or button down, or the
/// mouse moving. DirectInput reports the hardware, so the game re-centring its cursor does not count.
static KEYBOARD_MOUSE_ACTIVITY: AtomicU64 = AtomicU64::new(0);

/// Goes up whenever the keyboard or mouse is used.
pub fn keyboard_mouse_activity() -> u64 {
    KEYBOARD_MOUSE_ACTIVITY.load(Ordering::Relaxed)
}

/// A DirectInput state in use: a 256-byte keyboard with a key down, or a mouse that moved,
/// scrolled or has a button down.
unsafe fn state_is_active(data: *const u8, size: usize) -> bool {
    match size {
        256 => std::slice::from_raw_parts(data, 256).iter().any(|&key| key & 0x80 != 0),
        // DIMOUSESTATE (16 bytes) or DIMOUSESTATE2 (20): x, y and wheel movement, then the buttons.
        16 | 20 => {
            let axes = std::slice::from_raw_parts(data as *const i32, 3);
            let buttons = std::slice::from_raw_parts(data.add(12), size - 12);
            axes.iter().any(|&a| a != 0) || buttons.iter().any(|&b| b & 0x80 != 0)
        }
        _ => false,
    }
}

pub fn set_blocking(block: bool) {
    BLOCK.store(block, Ordering::SeqCst);
}

pub fn set_mask(buttons: u16) {
    MASK.store(buttons, Ordering::SeqCst);
}

/// The first connected controller, as it really is.
pub fn pad() -> Option<XINPUT_GAMEPAD> {
    let pads = PADS.lock().unwrap_or_else(|p| p.into_inner());
    pads.iter().flatten().next().copied()
}

/// Installs everything. Each part that fails is logged and skipped; the overlay still works without it.
pub fn install() {
    if let Err(e) = unsafe { hook_xinput() } {
        log::write(&format!("Controller support is off: {e}"));
    }
    if let Err(e) = unsafe { hook_direct_input() } {
        log::write(&format!("The game may still react to keys and the mouse while the panel is open: {e}"));
    }
    match find_menu_man() {
        Some(address) => MENU_MAN.store(address, Ordering::SeqCst),
        None => log::write("Could not find the game's cursor setting, so the overlay draws its own cursor."),
    }
}

/// Shows or hides the game's own mouse cursor. False if that is not possible, so the caller can draw one.
pub fn show_game_cursor(show: bool) -> bool {
    let global = MENU_MAN.load(Ordering::SeqCst);
    if global == 0 {
        return false;
    }
    let Some(menu) = memory::read::<usize>(global).filter(|&m| m != 0) else {
        return false;
    };
    unsafe {
        let flag = (menu + 0xAC) as *mut u8;
        let value = flag.read_volatile();
        let wanted = if show { value | 1 } else { value & !1 };
        if wanted != value {
            flag.write_volatile(wanted);
        }
    }
    true
}

unsafe fn system_library(name: &str) -> Result<windows::Win32::Foundation::HMODULE, String> {
    let mut buffer = [0u16; 260];
    let length = GetSystemDirectoryW(Some(&mut buffer)) as usize;
    if length == 0 || length >= buffer.len() {
        return Err("could not find the Windows system folder".into());
    }
    let path = format!("{}\\{name}", String::from_utf16_lossy(&buffer[..length]));
    LoadLibraryW(&HSTRING::from(path.as_str())).map_err(|e| format!("could not load {path}: {e}"))
}

unsafe fn start_minhook() -> Result<(), String> {
    match MH_Initialize() {
        MH_STATUS::MH_OK | MH_STATUS::MH_ERROR_ALREADY_INITIALIZED => Ok(()),
        status => Err(format!("MinHook: {status:?}")),
    }
}

/// Hooks `target` with `detour`, storing the original in `original` before the hook goes live.
unsafe fn hook<T: Copy>(target: *mut c_void, detour: *mut c_void, original: &OnceLock<T>) -> Result<(), String> {
    let hook = MhHook::new(target, detour).map_err(|s| format!("{s:?}"))?;
    let trampoline = hook.trampoline();
    let _ = original.set(std::mem::transmute_copy::<*mut c_void, T>(&trampoline));
    hook.queue_enable().map_err(|s| format!("{s:?}"))?;
    MH_ApplyQueued().ok().map_err(|s| format!("{s:?}"))
}

// ---- Controller ----

type XInputGetState = unsafe extern "system" fn(u32, *mut XINPUT_STATE) -> u32;
static XINPUT_ORIGINAL: OnceLock<XInputGetState> = OnceLock::new();

unsafe fn hook_xinput() -> Result<(), String> {
    let library = system_library("xinput1_4.dll")?;
    let target = GetProcAddress(library, s!("XInputGetState")).ok_or("XInputGetState is missing")?;
    start_minhook()?;
    hook(target as *mut c_void, xinput_get_state as *mut c_void, &XINPUT_ORIGINAL)
}

unsafe extern "system" fn xinput_get_state(index: u32, state: *mut XINPUT_STATE) -> u32 {
    let original = XINPUT_ORIGINAL.get().expect("XInput hook without its original");
    let result = original(index, state);
    if index < 4 {
        let mut pads = PADS.lock().unwrap_or_else(|p| p.into_inner());
        pads[index as usize] = (result == 0 && !state.is_null()).then(|| (*state).Gamepad);
    }
    if result == 0 && !state.is_null() {
        if BLOCK.load(Ordering::SeqCst) {
            // A new packet number with an idle pad, so the game sees every button let go.
            (*state).Gamepad = XINPUT_GAMEPAD::default();
        } else {
            let mask = MASK.load(Ordering::SeqCst);
            (*state).Gamepad.wButtons.0 &= !mask;
        }
    }
    result
}

// ---- Keyboard and mouse through DirectInput ----

type DirectInput8Create = unsafe extern "system" fn(*mut c_void, u32, *const GUID, *mut *mut c_void, *mut c_void) -> i32;
type CreateDevice = unsafe extern "system" fn(*mut c_void, *const GUID, *mut *mut c_void, *mut c_void) -> i32;
type Release = unsafe extern "system" fn(*mut c_void) -> u32;
type GetDeviceState = unsafe extern "system" fn(*mut c_void, u32, *mut c_void) -> i32;
type GetDeviceData = unsafe extern "system" fn(*mut c_void, u32, *mut c_void, *mut u32, u32) -> i32;

static STATE_ORIGINAL: OnceLock<GetDeviceState> = OnceLock::new();
static DATA_ORIGINAL: OnceLock<GetDeviceData> = OnceLock::new();

const IID_IDIRECTINPUT8W: GUID = GUID::from_u128(0xBF798031_483A_4DA2_AA99_5D64ED369700);
const GUID_SYS_KEYBOARD: GUID = GUID::from_u128(0x6F1D2B61_D5A0_11CF_BFC7_444553540000);

unsafe fn vtable(object: *mut c_void) -> *const *mut c_void {
    *(object as *const *const *mut c_void)
}

/// Every DirectInput device shares one implementation, so hooking a throwaway keyboard's
/// GetDeviceState and GetDeviceData covers the game's keyboard and mouse too.
unsafe fn hook_direct_input() -> Result<(), String> {
    let library = system_library("dinput8.dll")?;
    let create: DirectInput8Create =
        std::mem::transmute(GetProcAddress(library, s!("DirectInput8Create")).ok_or("DirectInput8Create is missing")?);
    let instance = GetModuleHandleW(None).map_err(|e| e.to_string())?;
    let mut input: *mut c_void = std::ptr::null_mut();
    let hr = create(instance.0, 0x0800, &IID_IDIRECTINPUT8W, &mut input, std::ptr::null_mut());
    if hr < 0 || input.is_null() {
        return Err(format!("DirectInput8Create failed ({hr:#x})"));
    }
    let input_vtable = vtable(input);
    let create_device: CreateDevice = std::mem::transmute(*input_vtable.add(3));
    let release_input: Release = std::mem::transmute(*input_vtable.add(2));
    let mut device: *mut c_void = std::ptr::null_mut();
    let hr = create_device(input, &GUID_SYS_KEYBOARD, &mut device, std::ptr::null_mut());
    if hr < 0 || device.is_null() {
        release_input(input);
        return Err(format!("could not open a keyboard ({hr:#x})"));
    }
    let device_vtable = vtable(device);
    let get_state = *device_vtable.add(9);
    let get_data = *device_vtable.add(10);
    let release_device: Release = std::mem::transmute(*device_vtable.add(2));
    release_device(device);
    release_input(input);

    start_minhook()?;
    hook(get_state, get_device_state as *mut c_void, &STATE_ORIGINAL)?;
    hook(get_data, get_device_data as *mut c_void, &DATA_ORIGINAL)
}

unsafe extern "system" fn get_device_state(device: *mut c_void, size: u32, data: *mut c_void) -> i32 {
    let original = STATE_ORIGINAL.get().expect("DirectInput hook without its original");
    let result = original(device, size, data);
    if result >= 0 && !data.is_null() && state_is_active(data as *const u8, size as usize) {
        KEYBOARD_MOUSE_ACTIVITY.fetch_add(1, Ordering::Relaxed);
    }
    if result >= 0 && BLOCK.load(Ordering::SeqCst) && !data.is_null() {
        std::ptr::write_bytes(data as *mut u8, 0, size as usize);
    }
    result
}

unsafe extern "system" fn get_device_data(device: *mut c_void, size: u32, data: *mut c_void, count: *mut u32, flags: u32) -> i32 {
    let original = DATA_ORIGINAL.get().expect("DirectInput hook without its original");
    let result = original(device, size, data, count, flags);
    // Real events only: a call with no buffer just empties the queue.
    if result >= 0 && !data.is_null() && !count.is_null() && *count > 0 {
        KEYBOARD_MOUSE_ACTIVITY.fetch_add(1, Ordering::Relaxed);
    }
    // The events are still taken from the queue, so they do not all arrive when the panel closes.
    if result >= 0 && BLOCK.load(Ordering::SeqCst) && !count.is_null() {
        *count = 0;
    }
    result
}

// ---- The game's cursor flag ----

/// `mov rcx,[rip+x]; mov rcx,[rcx+8]; call; mov rdx,rax; mov rcx,rsi; call`, where x is CSMenuManImp's global.
const MENU_MAN_PATTERN: &str = "48 8B 0D ?? ?? ?? ?? 48 8B 49 08 E8 ?? ?? ?? ?? 48 8B D0 48 8B CE E8";

fn find_menu_man() -> Option<usize> {
    memory::find_global(&memory::pattern(MENU_MAN_PATTERN), 3, 7)
}
