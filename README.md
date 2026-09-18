# Taskview++

**Taskview on steroids.**

Taskview++ takes the idea of Windows Task View and turns it into a persistent workspace you can keep open while you work. It uses live DWM thumbnails, lets you arrange and dock windows, works across virtual desktops, supports live desktop pop-outs, and restores real window placement when the overview is dismissed or after an unexpected UI-process exit.

> **Beta test release:** Taskview++ is currently public beta software. It is being shared for testing and feedback, and behavior, compatibility, settings, and features may still change before a stable release.

## Quick start

Download the latest **Taskview++ Beta** Windows package from the project's GitHub Releases page, extract it, and run `StayView.exe`. The executable keeps its original internal name for compatibility.

The default overview shortcut is **Ctrl + Win + Space**. **Esc** dismisses the overview. The app also remains available from its tray icon.

## Main interactions

- Click a window tile to bring the real window in front while leaving the overview available behind it.
- Drag window tiles around the overview. Dropped tile positions are remembered for the active session without moving the real window.
- Middle-click a window tile to send that application a normal close request.
- Dock small tiles beside the virtual-desktop cards by dropping them on the desktop bar.
- Click a virtual-desktop card to switch desktops.
- Double-click another desktop's card to open it as a live always-on-top pop-out. The current desktop cannot be popped out.
- Close a pop-out with its `X` button or by double-clicking empty wallpaper in the pop-out. The matching desktop card briefly brightens to acknowledge the close.
- Use the `NEW DESKTOP` card at the end of the strip to create and switch to a new Windows virtual desktop.
- Right-click a desktop card for Close, Change background, Move left/right, and Pop out where applicable.
- Drag a Taskview++ tile onto an open desktop pop-out to move that real window to the represented desktop.
- Use Options to change appearance, desktop-strip position, thumbnail size, minimized-window docking, and desktop transition behavior.

## Window safety

Before Taskview++ changes a managed window, it journals the original placement, monitor, DPI, virtual desktop, show state, process identity, and topmost state. Normal dismissal restores the saved placement. A separate guardian process can restore the same journal if the UI process exits unexpectedly.

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
dotnet publish src/StayView/StayView.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/Taskview++-win-x64
```

The source project keeps its original internal `StayView` project, process, settings, and executable names for compatibility; the user-facing product name is Taskview++.

## Notes for public distribution

Compiled beta packages belong in **GitHub Releases**, not in the source repository. Debug symbols, local build outputs, development screenshots, reference dumps, session notes, and private working artifacts are intentionally excluded from this public tree.

## License

Taskview++ is released under the [MIT License](LICENSE).

Copyright (c) 2026 **FranksApps**.

This beta status does not change the licence: the MIT terms apply to the source and distributed software in this public tree.
