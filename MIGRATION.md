# Migration notes

The supplied Python/Tkinter GUI has been moved to a conventional .NET solution.

| Previous module | C# location |
|---|---|
| `zapret2_gui/core.py` | `src/Zapret2.Core/Core.cs` |
| `zapret2_gui/finder.py` | `WinwsFinder` and `ComponentDiscovery` in `Core.cs` |
| `zapret2_gui/strategies.py` | `StrategyCatalog` in `Core.cs` |
| `zapret2_gui/ui.py` | `src/Zapret2.App/MainWindow.xaml` and `MainWindow.xaml.cs` |
| `tests/test_core.py` | `tests/Zapret2.Core.Tests/CoreTests.cs` |
| `strategies/*.bat` | `assets/strategies/` |

The C# application reads and writes the previous JSON field names (`base_dir`, `active`, `seeded`, `profiles`) and the lowercase profile fields (`name`, `args`, `note`). It keeps the same `%APPDATA%\\Zapret2GUI\\config.json` location. Existing Python GUI profiles remain readable. Before each save, a `config.json.bak` backup is made; a damaged primary file is preserved as `config.broken` and restored from the backup when possible.

The repository's previous Rust source archive is retained under `legacy/rust/` for migration reference and rollback. The active application and build stack is C# / WPF / .NET 10. The portable build is self-contained and single-file; it intentionally does not embed `winws2.exe`, WinDivert, drivers, or the external zapret bundle.
