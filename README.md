# Taskview++

**Taskview on steroids.**

Taskview++ takes the idea of Windows Task View and turns it into a persistent workspace you can keep open while you work. It uses live DWM thumbnails, lets you arrange and dock windows, works across virtual desktops, supports live desktop pop-outs, and restores real window placement when the overview is dismissed or after an unexpected UI-process exit.

> **Beta test release:** Taskview++ is currently public beta software. It is being shared for testing and feedback, and behavior, compatibility, settings, and features may still change before a stable release.

## Quick start

Download the latest **Taskview++ Beta** Windows package from the project's GitHub Releases page, extract it, and run `StayView.exe`. The executable keeps its original internal name for compatibility.

The default overview shortcut is **Ctrl + Win + Space**. **Esc** dismisses the overview. The app also remains available from its tray icon.

## Main interactions

- Click a window tile to bring the real window in front while leaving the overview available behind it. A focus click does not relocate the real window; it returns to the desktop position already associated with that window. The actual foreground browsed window gets a thicker blue outline than any other browsed window so focus is visually obvious without changing the real HWND's size or position.
- Double-click empty background or an empty title bar in a focused window to return that window to the overview. Text, files, tabs, editors and controls retain their normal double-clicks. Empty background is identified through Windows accessibility; unknown areas stay with the app. Click a tile to focus it again, or use Ctrl + Win + Space to return to the grid. Win+drag moves a window from its content without taking ordinary content drags away from the app. You can also hold the right mouse button and drag anywhere inside the focused window to move its top-level host, including over embedded child-app HWNDs; if you do not drag, Taskview++ replays a normal right-click so context menus still work.
- Hold the right mouse button without moving for about one second on a focused window's title bar to pin it; hold the title bar again to unpin. Long right-button holds in application content stay native, while right-drag still moves the focused host from anywhere inside it. Minimized/docked thumbnails keep their existing hold-to-pin gesture. A pinned window is marked by a large blue dot at its top-left corner, keeps its saved position, and remains visible in the same place while switching Windows virtual desktops. Move, resize, maximize/snap, tile drag, dock, undock, and desktop-transfer gestures are blocked until it is unpinned. Native Minimize and X / Close remain available; minimizing keeps the pin and moves its live thumbnail into the locked dock.
- Drag a non-docked window tile to change where that real window will live on the desktop. The real HWND does not follow the pointer behind the overview; Taskview++ records the drop and applies the translated desktop position under the opaque overview before the next focus handoff. Auto Arrange moves only the window you explicitly dragged, not neighbouring tiles that were rearranged automatically. While a real window is focused, its blue outline follows live moves/resizes; native resizing cannot reduce either edge below 15% of the standard miniature size, with a 32 px safety floor.
- While Taskview++ is active, pressing **Minimize** on a focused window docks it. The real source is restored non-activating behind the overview after Windows finishes the native minimize transition so the dock preview remains live. The native **X / Close** button is not intercepted and closes the application window normally.
- Closing a focused Chromium-style window (including Opera) no longer causes Taskview++ to re-activate the closing HWND when focus falls through to the overview. Only a completed tile drag can authorize the special canvas re-front handoff.
- Focus expansion keeps the visible title bar within the current monitor work area. The DWM-visible frame is checked before handoff and after minimized restore; an off-top window is shifted down without changing its size, and the corrected placement is retained.
- Launching the executable now presents the overview immediately once startup initialization is complete. The tray icon and hotkey remain available for later toggles; no initial tray/taskbar click is required.
- The popped-out virtual-desktop preview is non-activating: mouse interaction still works, but clicking it no longer takes foreground focus or triggers an overview z-order round trip. Routine refreshes also update thumbnails in place without repeatedly re-fronting the popout, reducing preview flicker.
- The **+** (new desktop) card sits at the horizontal centre of the desktop strip, with existing desktops a small gap either side of it.
- Middle-click a window tile to send that application a normal close request.
- Dock small tiles beside the virtual-desktop cards by dropping them on the desktop bar. The dock is global for the active Taskview++ session, so docked windows remain accessible while you switch between Windows virtual desktops.
- Click a docked miniature to return it to the current canvas. If it belongs to another virtual desktop, Taskview++ moves that real window to the desktop you are currently using before undocking it. The existing two-step interaction is preserved: dock -> canvas -> focus.
- Right-click a docked miniature for a compact grey **Close** menu. This sends the application's normal close request without first focusing or undocking it, so native save/discard prompts still work.
- Windows that were already minimized when the overview opened can be placed in the dock automatically with **Options > Dock minimized windows**. A window you explicitly minimize while working in Taskview++ returns to the normal grid instead of being auto-docked.
- When a focused window is minimized, Taskview++ covers the shell's taskbar-bound minimize animation with its own shrink-to-grid handoff and clears stale dock/adornment borders so an empty frame is not left behind.
- Browsed-window thumbnail suppression is guarded by z-order: Taskview++ hides a miniature only after the real source is confirmed above the opaque overview. If Windows/WinUI is still settling activation or a move, the live DWM copy remains visible temporarily at the real-window rectangle rather than leaving a blank hole.
- Left-click a virtual-desktop card to switch desktops immediately.
- Right-click another desktop's card to open it directly as a live always-on-top pop-out. The current desktop cannot be popped out, so its right-click management menu remains available.
- Close a pop-out with its `X` button or by double-clicking empty wallpaper in the pop-out. The matching desktop card briefly brightens to acknowledge the close.
- Click the centred **+** card to create and switch to a new Windows virtual desktop.
- A popped-out desktop keeps the physical monitor it represents fixed even if you drag the floating panel onto another monitor, preventing its live window thumbnails from disappearing at a monitor boundary.
- Drag a Taskview++ tile onto an open desktop pop-out to move that real window to the represented desktop. You can also drag a live window out of the pop-out and onto the visible Taskview++ canvas to move it back to the current desktop at the drop location.
- Use Settings to change appearance, auto-arrange, how many windows can be focused, minimized-window docking, dock-on-contact, the plasma effect, desktop-bar position, the opening animation and the desktop transition.

## Task View layout and motion

- Window miniatures use the same layout as the Windows 11 Task View: most-recently-used order, the same size caps, row choice and spacing (24 px between windows, 26 px between rows, 39 px title band at 100% scaling). The rules were measured from the real Task View; `tests/StayView.Checks/taskview-golden` holds 48 recorded layouts that the layout code is checked against (`taskview-layout-checks`).
- Dragging a tile pushes neighbours out of the way to keep Task View's spacing; neighbours slide back as you move away, a tile dropped squarely on another swaps places with it, and nothing is ever left overlapping another tile. Pinned tiles never move.
- Pushing a dragged window into the desktop bar shows an electric plasma effect across the bar; with **Dock on bar contact** on (default) the window docks after a quarter of a second of contact. Both the effect and the behaviour can be switched off in Settings.
- Hovering a docked window or a desktop card zooms it out into a larger live preview with a frame; moving away eases it back. Clicking while zoomed still undocks / switches desktop.
- Switching desktops slides the whole layout in from the side of the desktop you are moving to (default **Slide** transition). Animations run in step with the display refresh.
- A **Taskview++** banner shows while the app starts, when the overview is closed with Esc, and on exit.

## Window safety

Before Taskview++ changes a managed window, it journals placement, monitor, DPI, virtual desktop, show state, process identity, and topmost state. Automatic layout does not rewrite real window placement. Explicit user actions that are meant to change real state do update the journal: dragging a miniature translates that window's desktop position, dragging/resizing a focused real window recaptures its geometry, and moving a window to another virtual desktop records the new desktop.

The hidden real HWND is not moved continuously while a miniature is dragged. Placement changes are applied while the overview is still covering the source, which avoids background flashes and lets the DWM thumbnail animate directly into the final visible window frame. Normal dismissal restores the latest intentional user state. A separate guardian process can restore the same journal if the UI process exits unexpectedly.

Closed windows stay closed. Taskview++ does not fabricate replacement screenshots or icons when Windows or an application cannot provide a live DWM thumbnail.

## Requirements

- Windows 11 x64.
- The GitHub Releases Windows package is self-contained and does not require a separate .NET installation.
- Full virtual-desktop controls use a private Windows shell interface that is deliberately build-gated to Windows builds 26100 through 26200. On unsupported builds, the current-desktop overview can still run, but private virtual-desktop operations are unavailable.

## Build from source

Requires the .NET 8 SDK on Windows.

```powershell
dotnet build StayView.sln -c Release -p:Platform=x64
dotnet run --project tests/StayView.Checks/StayView.Checks.csproj -c Release
dotnet run --project tests/StayView.Checks/StayView.Checks.csproj -c Release -- empty-space-checks
dotnet publish src/StayView/StayView.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/Taskview++-win-x64
```

The source project keeps its original internal `StayView` project, process, settings, and executable names for compatibility; the user-facing product name is Taskview++.

## Notes for public distribution

Compiled beta packages belong in **GitHub Releases**, not in the source repository. Debug symbols, local build outputs, development screenshots, reference dumps, session notes, and private working artifacts are intentionally excluded from this public tree.

## License

Taskview++ is released under the [MIT License](LICENSE).

Copyright (c) 2026 **FranksApps**.

This beta status does not change the licence: the MIT terms apply to the source and distributed software in this public tree.
