//! Finding the game's globals by byte pattern, and reading through pointers without risking a crash.

use std::ffi::c_void;

use windows::Win32::System::Diagnostics::Debug::ReadProcessMemory;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Threading::GetCurrentProcess;

/// Reads a value at `address`, or None if that memory is not readable (a stale or null pointer).
pub fn read<T: Copy + Default>(address: usize) -> Option<T> {
    if address < 0x10000 {
        return None;
    }
    let mut value = T::default();
    let mut done = 0usize;
    let ok = unsafe {
        ReadProcessMemory(
            GetCurrentProcess(),
            address as *const c_void,
            &mut value as *mut T as *mut c_void,
            std::mem::size_of::<T>(),
            Some(&mut done),
        )
    };
    (ok.is_ok() && done == std::mem::size_of::<T>()).then_some(value)
}

/// Reads the pointer at `base`, then the pointer at each offset from the last one: `[[[base] + a] + b]`.
pub fn follow(base: usize, offsets: &[usize]) -> Option<usize> {
    let mut address = read::<usize>(base)?;
    for offset in offsets {
        address = read::<usize>(address.checked_add(*offset)?)?;
    }
    (address != 0).then_some(address)
}

/// Parses "48 8B 3D ?? ?? ?? ??" into bytes and wildcards.
pub fn pattern(text: &str) -> Vec<Option<u8>> {
    text.split_whitespace().map(|b| u8::from_str_radix(b, 16).ok()).collect()
}

/// Finds a RIP-relative instruction in the game's code and returns the address of the global it names.
/// `operand` is where the 32-bit offset sits in the match and `end` where the instruction ends.
pub fn find_global(pattern: &[Option<u8>], operand: usize, end: usize) -> Option<usize> {
    let at = find_in_code(pattern)?;
    let offset = unsafe { ((at + operand) as *const i32).read_unaligned() };
    Some((at + end).wrapping_add_signed(offset as isize))
}

/// The address of the first match of `pattern` in the game's code sections.
pub fn find_in_code(pattern: &[Option<u8>]) -> Option<usize> {
    unsafe {
        let base = GetModuleHandleW(None).ok()?.0 as usize;
        let read_u16 = |at: usize| (at as *const u16).read_unaligned() as usize;
        let read_u32 = |at: usize| (at as *const u32).read_unaligned() as usize;
        let nt = base + read_u32(base + 0x3C);
        let section_count = read_u16(nt + 6);
        let optional_size = read_u16(nt + 20);
        let first_section = nt + 24 + optional_size;
        for i in 0..section_count {
            let section = first_section + i * 40;
            let size = read_u32(section + 8);
            let start = base + read_u32(section + 12);
            let executable = read_u32(section + 36) & 0x2000_0000 != 0;
            if !executable || size < pattern.len() {
                continue;
            }
            let code = std::slice::from_raw_parts(start as *const u8, size);
            if let Some(at) = find(code, pattern) {
                return Some(start + at);
            }
        }
    }
    None
}

pub fn find(haystack: &[u8], pattern: &[Option<u8>]) -> Option<usize> {
    let first = pattern.first().copied().flatten()?;
    let last = haystack.len().checked_sub(pattern.len())?;
    let mut at = 0;
    while at <= last {
        let next = haystack[at..=last].iter().position(|&b| b == first)?;
        at += next;
        if pattern.iter().zip(&haystack[at..]).all(|(p, &b)| p.map_or(true, |p| p == b)) {
            return Some(at);
        }
        at += 1;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_a_pattern_with_wildcards() {
        let wanted = pattern("48 8B 0D ?? ?? ?? ?? 48 8B 49 08");
        let mut code = vec![0x90u8; 64];
        code[20..31].copy_from_slice(&[0x48, 0x8B, 0x0D, 1, 2, 3, 4, 0x48, 0x8B, 0x49, 0x08]);
        code[5] = 0x48; // a false start
        assert_eq!(find(&code, &wanted), Some(20));
        assert_eq!(find(&code[..25], &wanted), None);
    }

    #[test]
    fn reading_a_bad_address_fails_instead_of_crashing() {
        assert_eq!(read::<u64>(0), None);
        assert_eq!(read::<u64>(0x10), None);
        let value = 42u64;
        assert_eq!(read::<u64>(&value as *const u64 as usize), Some(42));
    }
}
