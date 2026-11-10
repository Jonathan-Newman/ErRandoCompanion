//! Loads ErRandoCompanion.Native.dll (the tracker, shared with the desktop app) and talks to its C API.
//! Every call sends JSON and gets {"ok":true,"result":...} or {"ok":false,"error":"..."} back.

use std::ffi::{c_char, CStr, CString};
use std::path::Path;

use serde::de::DeserializeOwned;
use serde_json::Value;
use windows::core::{HSTRING, PCSTR};
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

type TextFn = unsafe extern "C" fn(*const c_char) -> *mut c_char;
type FreeFn = unsafe extern "C" fn(*mut c_char);
type AnyFn = unsafe extern "system" fn() -> isize;

pub struct Native {
    init: TextFn,
    call: TextFn,
    free: FreeFn,
}

impl Native {
    pub fn load(path: &Path) -> Result<Self, String> {
        unsafe {
            let module = LoadLibraryW(&HSTRING::from(path.as_os_str()))
                .map_err(|e| format!("Could not load {}: {e}", path.display()))?;
            let export = |name: &'static [u8]| -> Result<AnyFn, String> {
                GetProcAddress(module, PCSTR(name.as_ptr())).ok_or_else(|| {
                    format!("{} has no {} export.", path.display(), String::from_utf8_lossy(&name[..name.len() - 1]))
                })
            };
            Ok(Self {
                init: std::mem::transmute::<AnyFn, TextFn>(export(b"er_init\0")?),
                call: std::mem::transmute::<AnyFn, TextFn>(export(b"er_call\0")?),
                free: std::mem::transmute::<AnyFn, FreeFn>(export(b"er_free\0")?),
            })
        }
    }

    pub fn init<T: DeserializeOwned>(&self, options: &Value) -> Result<T, String> {
        self.invoke(self.init, &options.to_string())
    }

    pub fn call<T: DeserializeOwned>(&self, request: Value) -> Result<T, String> {
        self.invoke(self.call, &request.to_string())
    }

    fn invoke<T: DeserializeOwned>(&self, function: TextFn, json: &str) -> Result<T, String> {
        let input = CString::new(json).map_err(|_| "The request contains a NUL character.".to_string())?;
        let text = unsafe {
            let reply = function(input.as_ptr());
            if reply.is_null() {
                return Err("The tracker gave no reply.".into());
            }
            let text = CStr::from_ptr(reply).to_string_lossy().into_owned();
            (self.free)(reply);
            text
        };
        let mut reply: Value = serde_json::from_str(&text).map_err(|e| format!("The tracker's reply is not JSON: {e}"))?;
        if reply["ok"] == Value::Bool(true) {
            serde_json::from_value(reply["result"].take()).map_err(|e| format!("Unexpected reply from the tracker: {e}"))
        } else {
            Err(reply["error"].as_str().unwrap_or("The tracker reported an unknown error.").to_string())
        }
    }
}
