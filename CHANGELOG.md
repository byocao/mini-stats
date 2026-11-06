# Changelog

## v1.1.0 - 2026-11-06

### Added
- **Settings window** (WPF, dark Windows 11 style) with a live preview strip; every change is previewed on the real
  overlay and only saved on Save / Apply. Cancel or Esc restores the previous look.
  - Metrics: on/off switches and up/down ordering, refresh rate.
  - Appearance: theme presets (Midnight, Daylight, Nord, Mono), font, text size, opacity, corner radius,
    colour chips with a palette / hex input.
  - Position: display picker, clickable corner map, drag-to-place, edge distance.
  - General: FPS status with *Restart as administrator*, *Start with Windows*, settings file, about.
- Dock to any corner (`anchor=`) of any display (`monitor=`).
- Running `MiniStats.exe` again (or `--settings [page]`) opens the settings of the running instance, even when it
  runs elevated.
- `--selftest` and `--shot-settings` developer switches.

### Changed
- Tray menu reduced to *Settings...*, *Unlock to move* and *Exit*; double-clicking the tray icon opens Settings.
- Memory used by the settings window is handed back to Windows when it closes.
- Existing v1.0 configs keep their dragged position (`x`/`y` without `anchor` is read as `anchor=custom`).

## v1.0.0 - 2026-11-05

First public release.

- Always-on-top, click-through overlay docked to the bottom-right corner of the primary screen.
- Metrics: FPS (ETW present events of the foreground process), CPU, RAM (% or GB), GPU load, VRAM, GPU temperature (NVIDIA NVML).
- `MiniStats.ini` configuration with live reload: items, order, font, colours, opacity, corner radius, position.
- Tray menu: move/lock, reset position, edit config, start with Windows (elevated logon task), exit.
- Per-monitor DPI awareness.
- Developer switches: `--probe` (present-event diagnostics) and `--render` (offscreen PNG).
