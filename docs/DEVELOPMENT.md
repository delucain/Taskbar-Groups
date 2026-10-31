# Development

## Known-good baseline

This document describes the stable pre-final-1.0.3 baseline established after the persistent-host and IPC work. Preserve this state in version control before adding the final planned 1.0.3 feature.

Core behaviors confirmed at this baseline:

- Project builds with the Windows .NET Framework compiler used by `Build-Release.ps1`.
- Resident host starts and prewarms group/menu/icon caches.
- Taskbar clients can communicate with the resident host through the named pipe.
- Same-user pipe security works with normal taskbar-launched clients.
- Menu open/close behavior works with the persistent host.
- Moving the mouse quickly after a taskbar click does not reposition the popup to the later cursor location.
- Native Link Properties opens and remains functional through its dedicated worker process.
- The previous dead-field compiler warnings have been removed.

## Build

```powershell
.\Build-Release.ps1 -Version 1.0.3
```

`-Version` is mandatory and must be `major.minor.patch`.

The build uses the standard .NET Framework `csc.exe` and references:

```text
System.dll
System.Drawing.dll
System.Windows.Forms.dll
```

The source also uses pipe access-control and Windows principal types available through the framework assemblies used by the Windows compiler/runtime.

## Installer

Inno Setup 6 builds:

```text
_release\Taskbar-Groups-Setup-v1.0.3.exe
```

The installer requires administrative privileges because application files are installed under Program Files and the resident-host Run entry is registered under HKLM.

User group data remains under `%LOCALAPPDATA%` and is not removed during uninstall.

## Resident-host startup

The installed Run entry executes:

```text
"C:\Program Files\Taskbar Groups\Taskbar-GroupMenu.exe" --resident-host
```

During local release builds, `Build-Release.ps1` also starts the freshly built host after installation so the new binary can be tested without signing out or rebooting.

## IPC invariants

Do not regress these properties when changing startup code:

1. A normal group invocation must try the pipe before initializing WinForms.
2. The pipe is the liveness check; the mutex is only host-election state.
3. Pipe/mutex names must remain scoped to session and user SID.
4. The server pipe must grant the current user access explicitly.
5. A real menu request must be able to interrupt background prewarming.

## Menu-cache invariants

- Cached hierarchies belong to the resident process only.
- A filesystem signature change invalidates the affected group.
- Management operations invalidate the affected group.
- Detaching cached items must not dispose them.
- Items being permanently replaced may be disposed after the replacement menu paints.

## Link Properties

Do not fold Link Properties back into the main transient command path. The native property sheet is modeless and requires the worker process/message loop to remain alive until the property window closes.

## Regression checklist

Before tagging 1.0.3:

1. Clean build with no unexpected compiler warnings.
2. Install over an existing configuration; groups remain intact.
3. Confirm resident host is running after install/logon.
4. First click after host prewarm opens without the old cold-start delay.
5. Open several groups repeatedly; cache-hit behavior remains fast.
6. Click a taskbar group and immediately move the mouse; menu remains anchored to the original taskbar click.
7. Click elsewhere; menu closes normally.
8. Switch directly between several taskbar groups.
9. Add, rename, reorder, and remove a link.
10. Add/reorder a heading and separator.
11. Add a `.ps1` link with blank arguments.
12. Add a `.ps1` link with existing user arguments and confirm they are preserved.
13. Open Link Properties and File Properties.
14. Rename a group and confirm its pin-ready shortcut is updated.
15. Change group and item icons.
16. Reboot/sign out and confirm the resident host starts automatically.
17. Uninstall and confirm `%LOCALAPPDATA%\Taskbar-Groups` is preserved.

## Diagnostics

Set `TASKBAR_GROUPS_PERF=1` at user scope and restart the resident host. See [Performance and Caching](PERFORMANCE.md).
