# Taskbar Groups 1.0.3 Release Notes

Taskbar Groups 1.0.3 substantially changes the runtime architecture and adds direct Explorer/taskbar management while preserving the filesystem-driven group model.

## Faster menu opening

Taskbar Groups now uses a persistent resident menu host instead of rebuilding WinForms state for every taskbar click. Normal taskbar invocations are short-lived IPC clients that forward the requested group and exit.

The resident host prewarms groups at startup, caches complete menu hierarchies, and keeps rendered icons in both memory and a persistent disk cache. Warm menu opens avoid repeated application startup, hierarchy construction, and icon resolution.

## Reliable taskbar positioning

The resident host captures the original low-level taskbar mouse-down location and validates it against the Windows taskbar rectangle. The popup is anchored to that click rather than the cursor's later position, so rapid mouse movement after clicking no longer moves the menu.

## Explorer integration

The installer adds **Pin to Taskbar Group** to the Explorer context menu for files and directories. It opens the normal Add Link dialog with the selected path populated.

Add Link now includes a required **Group** selector. Existing groups are listed along with **<Create New Group...>**, which opens New Group modally and returns the newly created group to Add Link.

## Taskbar pin lifecycle

Creating a group now submits a native Windows taskbar-pin request. On Windows builds that require confirmation, Windows displays its own prompt and Taskbar Groups returns immediately rather than timing out while waiting for the user.

Group Options now includes **Unpin Group from Taskbar**. Deleting a group attempts to unpin it before deleting its group data and source shortcut.

If a pinned shortcut survives after its group folder is manually removed, clicking it opens a foreground recovery dialog with **Recreate Group**, **Unpin from Taskbar**, and **Cancel**. Recreate restores the missing empty group without creating a duplicate shortcut or issuing another pin request.

## Shortcut management improvements

- Native Link Properties runs through a dedicated worker process so the modeless Windows property sheet remains alive and functional.
- `.ps1` links prefer PowerShell 7 when available and fall back to Windows PowerShell 5.1.
- Existing user-entered arguments are preserved while ensuring the selected script remains the `-File` target.
- PowerShell-backed **Open File Location** and **File Properties** resolve the script itself when possible.

## Resident-host and IPC hardening

- Pipe-first resident-host discovery: the IPC endpoint is the liveness test; the mutex is used for host election.
- IPC object names are scoped by Windows session ID and current-user SID.
- The named pipe uses an explicit same-user ACL.
- Real menu requests take priority over incremental startup prewarming.
- Per-group filesystem signatures invalidate stale menu caches.
- Detached old menu items are disposed outside the menu-open critical path.

## Logging and diagnostics

Each resident host always writes one session log under `%LOCALAPPDATA%\Taskbar-Groups` using the form:

```text
TaskbarGroups_yyyyMMdd-HHmmss.log
```

Lifecycle messages and errors are always recorded. Setting `TASKBAR_GROUPS_PERF=1` adds detailed performance records to the same session log. Short-lived launcher processes do not create separate logs when they successfully forward to the resident host.

The release build also writes an Inno Setup installation log and surfaces installer failures instead of reducing them to an unexplained exit code.

## Icon and installer behavior

1.0.3 adds a dedicated Taskbar Groups application icon and uses it for the Explorer context-menu integration.

The bundled Icon Library is now treated as starter/user-owned mutable content. Setup installs missing starter icons but never overwrites an existing same-named icon. The Icon Library is preserved during uninstall, so user-added or customized icons are not destroyed.

This policy also avoids the previous upgrade problem where Explorer could hold an installed icon open and block Setup from replacing it. Explorer no longer needs to be restarted during installation.

## Upgrade behavior

Application binaries remain under Program Files. Groups, source shortcuts, caches, and logs remain under `%LOCALAPPDATA%\Taskbar-Groups`. Existing group data is preserved across upgrades and uninstall.
