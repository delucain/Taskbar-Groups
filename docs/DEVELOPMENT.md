# Development

## 1.0.3 release baseline

Core behaviors expected in the 1.0.3 release:

- Resident host starts and incrementally prewarms group/menu/icon caches.
- Taskbar clients communicate with the resident host through a same-user named pipe.
- Menu open/close behavior works with the persistent host.
- Moving the mouse quickly after a taskbar click does not reposition the popup.
- Native Link Properties remains functional through its dedicated worker process.
- Explorer **Pin to Taskbar Group** opens Add Link with the selected file/directory.
- New groups submit a native Windows pin request without blocking on Windows confirmation UI.
- Delete Group unpins before deleting group data.
- Missing group directories produce a foreground recovery dialog with recreate/unpin/cancel choices.
- The Icon Library survives upgrades and uninstall without overwriting user changes.

## Build

```powershell
.\Build-Release.ps1 -Version 1.0.3
```

`-Version` is mandatory and must be `major.minor.patch`.

The build uses the standard .NET Framework `csc.exe`, embeds `assets\TaskbarGroups.ico`, and references:

```text
System.dll
System.Drawing.dll
System.Windows.Forms.dll
```

Inno Setup 6 produces:

```text
_release\Taskbar-Groups-Setup-v1.0.3.exe
```

The installer requires administrative privileges because application files are installed under Program Files and the resident-host Run entry is registered under HKLM. Per-user group data remains under `%LOCALAPPDATA%`.

`Build-Release.ps1` installs the release locally for testing, writes an Inno Setup log beside the release installer, then starts the freshly built executable in `--resident-host` mode. It must not restart Explorer.

## IPC invariants

1. A normal group invocation must try the pipe before initializing WinForms.
2. The pipe is the liveness check; the mutex is only host-election state.
3. Pipe/mutex names remain scoped to session and user SID.
4. The server pipe grants the current user access explicitly.
5. A real menu request can interrupt background prewarming.
6. A missing group is checked before cached menu content is shown.

## Menu-cache invariants

- Cached hierarchies belong to the resident process only.
- A filesystem signature change invalidates the affected group.
- Management operations invalidate the affected group.
- Detaching cached items must not dispose them.
- Items being permanently replaced may be disposed after the replacement menu paints.

## Link Properties

Do not fold Link Properties back into the main transient command path. The native property sheet is modeless and requires the worker process/message loop to remain alive until the property window closes.

## Taskbar pinning

Windows 10 does not reliably expose the legacy automatable `taskbarpin` shell verb. New-group pinning therefore uses the native taskband `IPinnedList3` path first and retains legacy shell verbs only as fallbacks.

A successful native pin request may cause Windows to display confirmation UI. Treat successful submission as asynchronous; do not wait for the pinned shortcut to appear before returning from group creation.

The source shortcut under `%LOCALAPPDATA%\Taskbar-Groups\Pin to Taskbar` remains the canonical group shortcut used for pin/unpin management.

## Icon Library invariant

Treat `{app}\Icon Library` as user-owned mutable data after initial seeding:

- install bundled icons only when the destination filename is absent;
- never overwrite an existing library icon during upgrade;
- never delete the Icon Library during uninstall;
- changed starter artwork must use a new human-readable filename rather than replacing an existing starter icon.

## Regression checklist

Before tagging 1.0.3:

1. Clean build with no unexpected compiler warnings.
2. Install over an existing configuration; groups remain intact and Explorer is not restarted.
3. Confirm resident host is running after install/logon.
4. First click after prewarm opens without the old cold-start delay.
5. Open several groups repeatedly; cache-hit behavior remains fast.
6. Click a taskbar group and immediately move the mouse; menu remains anchored to the original taskbar click.
7. Click elsewhere; menu closes normally.
8. Switch directly between several taskbar groups.
9. Add, rename, reorder, and remove a link.
10. Add/reorder a heading and separator.
11. Add a `.ps1` link with blank arguments.
12. Add a `.ps1` link with existing arguments and confirm they are preserved.
13. Open Link Properties and File Properties.
14. Use Explorer **Pin to Taskbar Group** for both a file and a directory.
15. Confirm Add Link requires a group and **<Create New Group...>** returns the created group to the dialog.
16. Create a group and confirm Windows receives the taskbar-pin request; leave the Windows confirmation prompt open long enough to verify Taskbar Groups does not time out.
17. Use **Unpin Group from Taskbar** without deleting the group.
18. Delete a pinned group and confirm it is unpinned.
19. Manually delete a group directory while leaving its taskbar pin; confirm the recovery dialog appears in front.
20. Test **Recreate Group**, **Unpin from Taskbar**, and **Cancel** in the missing-group dialog.
21. Add a custom icon to the installed Icon Library, modify a starter icon, then upgrade; confirm both survive unchanged while missing starter icons are seeded.
22. Change group and item icons.
23. Reboot/sign out and confirm the resident host starts automatically.
24. Uninstall and confirm `%LOCALAPPDATA%\Taskbar-Groups` and the user-owned Icon Library are preserved.

## Diagnostics

Every resident host writes `%LOCALAPPDATA%\Taskbar-Groups\TaskbarGroups_yyyyMMdd-HHmmss.log`. Set `TASKBAR_GROUPS_PERF=1` at user scope and restart the resident host to add detailed performance records. See [Performance and Caching](PERFORMANCE.md).
