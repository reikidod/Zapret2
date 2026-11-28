//! Small, versioned native ABI used by the C# control center.
//!
//! The native layer deliberately does not launch or configure Zapret. C# remains
//! the owner of process lifecycle, profiles, persistence and user interaction.
//! Rust provides a compact filesystem probe that can be called without shelling
//! out to PowerShell or another interpreter.

use std::{
    ffi::CStr,
    os::raw::c_char,
    path::Path,
};

/// ABI contract version. Increment only when an incompatible ABI change is made.
pub const ABI_VERSION: u32 = 1;

/// Seven paths are passed as newline-separated UTF-8 absolute paths.
///
/// Bit 0: winws.exe
/// Bit 1: winws2.exe
/// Bit 2: blockcheck.cmd
/// Bit 3: blockcheck2.cmd
/// Bit 4: zapret-lib.lua
/// Bit 5: WinDivert.dll
/// Bit 6: WinDivert64.sys
pub const COMPONENT_COUNT: usize = 7;

#[no_mangle]
pub extern "C" fn z2_abi_version() -> u32 {
    ABI_VERSION
}

/// Checks the seven component paths supplied by the managed application.
///
/// # Safety
/// `paths` must point to a valid NUL-terminated UTF-8 string and `out_mask`
/// must point to writable memory for one u64. Both pointers are provided by C#.
#[no_mangle]
pub unsafe extern "C" fn z2_probe_paths_utf8(
    paths: *const c_char,
    out_mask: *mut u64,
) -> i32 {
    if paths.is_null() || out_mask.is_null() {
        return -1;
    }

    // SAFETY: pointer validity is part of this function's C ABI contract.
    let raw = unsafe { CStr::from_ptr(paths) };
    let text = match raw.to_str() {
        Ok(value) => value,
        Err(_) => return -2,
    };

    let mut mask = 0_u64;
    for (index, line) in text.lines().take(COMPONENT_COUNT).enumerate() {
        let path = line.trim();
        if !path.is_empty() && Path::new(path).is_file() {
            mask |= 1_u64 << index;
        }
    }

    // SAFETY: pointer validity is part of this function's C ABI contract.
    unsafe { *out_mask = mask };
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, time::{SystemTime, UNIX_EPOCH}};

    fn temp_dir() -> std::path::PathBuf {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let path = std::env::temp_dir().join(format!("zapret2-native-{nonce}"));
        fs::create_dir_all(&path).expect("create temp directory");
        path
    }

    #[test]
    fn abi_version_is_stable() {
        assert_eq!(z2_abi_version(), 1);
    }

    #[test]
    fn probe_sets_only_bits_for_existing_files() {
        let root = temp_dir();
        let first = root.join("winws.exe");
        let third = root.join("blockcheck.cmd");
        fs::write(&first, b"test").expect("write first");
        fs::write(&third, b"test").expect("write third");

        let input = format!("{}\n{}\n{}\n", first.display(), root.join("missing.exe").display(), third.display());
        let mask = probe_text(&input).expect("probe");
        assert_eq!(mask, 0b000_0101);

        fs::remove_dir_all(root).expect("cleanup");
    }

    #[test]
    fn probe_ignores_empty_lines_and_stops_after_seven_paths() {
        let root = temp_dir();
        let file = root.join("component.bin");
        fs::write(&file, b"test").expect("write component");
        let input = format!("\n{}\n{}\n", file.display(), file.display());
        assert_eq!(probe_text(&input).expect("probe"), 0b000_0010);
        fs::remove_dir_all(root).expect("cleanup");
    }

    fn probe_text(text: &str) -> Result<u64, i32> {
        let c_text = std::ffi::CString::new(text).expect("no NUL bytes");
        let mut mask = 0_u64;
        // SAFETY: CString and local output live through the call.
        let status = unsafe { z2_probe_paths_utf8(c_text.as_ptr(), &mut mask) };
        if status == 0 { Ok(mask) } else { Err(status) }
    }
}
