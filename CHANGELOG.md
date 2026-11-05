# Changelog

## v1.0.0 - 2026-11-05

First public release.

- Always-on-top, click-through overlay docked to the bottom-right corner of the primary screen.
- Metrics: FPS (ETW present events of the foreground process), CPU, RAM (% or GB), GPU load, VRAM, GPU temperature (NVIDIA NVML).
- `MiniStats.ini` configuration with live reload: items, order, font, colours, opacity, corner radius, position.
- Tray menu: move/lock, reset position, edit config, start with Windows (elevated logon task), exit.
- Per-monitor DPI awareness.
- Developer switches: `--probe` (present-event diagnostics) and `--render` (offscreen PNG).
