# Changelog

## 0.3.0 — 2026-11-19

- Hotkey is now configurable from Settings → Plugins → 屏幕问答 (stored in `hotkey.txt`, applied within a second).
- Supports modifiers and function keys: `x`, `f9`, `ctrl+shift+x`, `side`, `mouse4`, `mouse5`, …
- The floating window no longer appears in the taskbar.
## 0.2.1 — 2026-11-19

- The floating UI is now a real desktop window (`Panel.exe`) with a round orb: double-click to expand, drag anywhere (including outside DSH), resize from the corner, collapse with `—`.
- Orb shows the DSH whale glyph; window position is remembered.
- Screenshots no longer auto-open the in-page overlay; the desktop panel handles them.
- Helper runs as a child of DSH, hides its console, exits via heartbeat.

## 0.2.0 — 2026-11-19

- Panel got white/black styling, resize handle, minimize and double-click collapse.
- Panel no longer constrains dragging to the viewport.

## 0.1.0 — 2026-11-19

- First working version: hold `X` + drag, capture region, ask a vision model in a floating panel, write the Q&A back into the current session.
- Mouse side button (`holdKey: side`) supported.
