# Taskview++

**Taskview on steroids.**

Taskview++ takes the idea of Windows Task View and turns it into a persistent workspace you can keep open while you work. It uses live DWM thumbnails, lets you arrange and dock windows, works across virtual desktops, supports live desktop pop-outs, and restores real window placement when the overview is dismissed or after an unexpected UI-process exit.

> **Beta test release:** Taskview++ is currently public beta software. It is being shared for testing and feedback, and behavior, compatibility, settings, and features may still change before a stable release.

## Quick start

Download the latest **Taskview++ Beta** Windows package from the project's GitHub Releases page, extract it, and run `StayView.exe`. The executable keeps its original internal name for compatibility.

The default overview shortcut is **Ctrl + Win + Space**. **Esc** dismisses the overview. The app also remains available from its tray icon.

## Main interactions

- Click a window tile to bring the real window in front while leaving the overview available behind it. A focus click does not relocate the real window; it returns to the desktop position already associated with that window.
- Double-click empty background or an empty title bar in a focused window to return that window to the overview. Text, files, tabs, editors and controls retain their normal double-clicks. Empty background is identified through Windows accessibility; unknown areas stay with the app. Click a tile to focus it again, or use Ctrl + Win + Space to return to the grid. Win+drag moves a window from its content without taking ordinary content drags away from the app.
- Drag a non-docked window tile to change where that real window will live on the desktop. The real HWND does not follow the pointer behind the overview; Taskview++ records the drop and applies the translated desktop position under the opaque overview before the next focus handoff. Auto Arrange moves only the window you explicitly dragged, not neighbouring tiles that were rearranged automatically.
- Middle-click a window tile to send that application a normal close request.
- Dock small tiles beside the virtual-desktop cards by dropping them on the desktop bar. The dock is global for the active Taskview++ session, so docked windows remain accessible while you switch between Windows virtual desktops.
- Click a docked miniature to return it to the current canvas. If it belongs to another virtual desktop, Taskview++ moves that real window to the desktop you are currently using before undocking it. The existing two-step interaction is preserved: dock -> canvas -> focus.
- Right-click a docked miniature for a compact grey **Close** menu. This sends the application's normal close request without first focusing or undocking it, so native save/discard prompts still work.
- Windows that were already minimized when the overview opened can be placed in the dock automatically with **Options > Dock minimized windows**. A window you explicitly minimize while working in Taskview++ returns to the normal grid instead of being auto-docked.
- When a focused window is minimized, Taskview++ covers the shell's taskbar-bound minimize animation with its own shrink-to-grid handoff and clears stale dock/adornment borders so an empty frame is not left behind.
- Click a virtual-desktop card to switch desktops.
- Double-click another desktop's card to open it as a live always-on-top pop-out. The current desktop cannot be popped out.
- Close a pop-out with its `X` button or by double-clicking empty wallpaper in the pop-out. The matching desktop card briefly brightens to acknowledge the close.
- Use the `NEW DESKTOP` card at the end of the strip to create and switch to a new Windows virtual desktop.
- Right-click a desktop card for Close, Change background, Move left/right, and Pop out where applicable.
- Drag a Taskview++ tile onto an open desktop pop-out to move that real window to the represented desktop.
- Use Options to change appearance, desktop-strip position, thumbnail size, minimized-window docking, and desktop transition behavior.

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
