# Editor Window Screenshot

`screenshot editor_window` captures Unity Editor UI in Edit Mode or Play Mode. It is separate from `scene_view`, which renders a Scene view camera, and it never falls back to an OS desktop screenshot.

```bash
$CLI screenshot editor_window --target editor
$CLI screenshot editor_window --target active
$CLI screenshot editor_window --windowType MyNamespace.MyCustomEditorWindow
$CLI screenshot editor_window --title "My Tool"
$CLI screenshot editor_window --instanceId 12345
$CLI screenshot editor_window --entityId 12345
```

- `--target editor` captures the Unity main window, including the menu bar and docked panels. Floating windows and transient popups are not composed into this image.
- `--target active` uses the last focused Unity `EditorWindow`, not the OS foreground window.
- `--windowType`, `--title`, and `--entityId`/`--instanceId` are AND filters. On Unity 6000.4+ prefer `--entityId` (`--instanceId` remains a compatible alias). Multiple matches fail with `target_ambiguous`.
- Selecting an inactive docked tab temporarily focuses it for two Editor repaint cycles, captures its host area including the tab strip, and restores the previous focus.
- PNG files are written to `.aibridge/screenshots/`. Success data contains only `path`, `width`, and `height`.

Windows 10 1803+ uses Windows Graphics Capture. Unity floating containers rejected by WGC use a window-only `PrintWindow(PW_RENDERFULLCONTENT)` compatibility path with nonblank pixel validation; desktop BitBlt and screen-region capture remain prohibited. macOS 12.3+ uses ScreenCaptureKit and requires Screen Recording permission. Linux is not supported. Hidden, minimized, zero-sized, batch-mode, permission-denied, and unverified targets fail instead of returning a black image or unrelated desktop pixels.
