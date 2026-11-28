# Native component

This workspace contains the Rust native layer for Zapret2 Control Center.

## Responsibility boundary

- **C# / WPF** owns UI, profiles, configuration, updates, process lifetime, logs,
  blockcheck orchestration and all user-facing errors.
- **Rust** is a small native library for bounded, deterministic system checks.
  It does not launch processes, modify network settings, or interpret profile
  commands.
- The boundary is a versioned C ABI. No shared mutable state is used.

## ABI v1

- `z2_abi_version() -> u32`
- `z2_probe_paths_utf8(paths, out_mask) -> i32`

The probe receives seven newline-separated UTF-8 absolute paths and returns a
bit mask indicating which runtime files exist. Return codes: `0` success,
`-1` invalid pointers, `-2` invalid UTF-8. C# must verify ABI version before
calling other exports and must retain a managed fallback if the DLL is missing
or incompatible.

Bit assignments are documented beside the Rust exports in
`zapret2-native/src/lib.rs`.

## Build and test

On Windows x64 with the stable Rust toolchain:

```powershell
cargo test --manifest-path native/Cargo.toml
cargo build --manifest-path native/Cargo.toml --release --target x86_64-pc-windows-msvc
```

The native DLL is not yet a required runtime dependency; integration and
single-file embedding are handled by the managed application build.
