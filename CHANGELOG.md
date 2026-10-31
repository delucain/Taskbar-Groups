# Changelog

## 1.0.3 — 2026-10-31

### Added

- Persistent resident menu host with startup prewarming.
- Per-group resident `ToolStripItem` hierarchy cache.
- Persistent rendered menu-icon cache.
- Pipe-first resident-host discovery and same-user IPC diagnostics.
- Session + user-SID scoped IPC names and explicit current-user named-pipe ACL.
- Dedicated native Link Properties worker process.
- General resident-host session logging with optional `TASKBAR_GROUPS_PERF=1` performance records.
- Dedicated Taskbar Groups application icon.
- Explorer **Pin to Taskbar Group** context-menu integration for files and directories.
- Required Group selector in Add Link, including **<Create New Group...>**.
- Automatic native Windows taskbar-pin request for newly created groups.
- **Unpin Group from Taskbar** group command.
- Missing-group recovery dialog with **Recreate Group**, **Unpin from Taskbar**, and **Cancel**.

### Changed

- Secondary taskbar launchers forward requests before initializing WinForms.
- Menu positioning uses the original low-level taskbar click rather than the later cursor location.
- Previous menu items are detached from the live menu before deferred disposal.
- `.ps1` shortcut creation preserves existing user arguments while ensuring the script is supplied as the `-File` target.
- PowerShell-backed Open File Location and File Properties resolve the script itself when possible.
- Delete Group now attempts to unpin the taskbar shortcut before deleting group data.
- Native taskbar pinning uses Windows 10's taskband `IPinnedList3` interface first, with legacy shell verbs retained as fallbacks.
- A successful native pin request is treated as asynchronous so Windows confirmation UI can remain open without timing out Taskbar Groups.
- The Icon Library is treated as user-owned mutable starter content: upgrades install missing starter files only, never overwrite existing same-named icons, and uninstall preserves the library.
- Release builds compile with the application icon, write an Inno Setup install log, and restart the resident host after local installation without restarting Explorer.

### Fixed

- First-use and repeated menu latency caused by repeated WinForms/menu initialization.
- Menu placement following rapid cursor movement after a taskbar click.
- Link Properties failing because its native modeless property sheet outlived the launching command path.
- Named-pipe access failures between the resident host and normal taskbar launchers.
- Installer failure caused by an undefined `$ReleaseDir` diagnostic path.
- Installer upgrade failures caused by attempting to replace Explorer-held starter icon files.
- Automatic taskbar pin requests being silently ignored by the legacy `taskbarpin` shell verb.
- New-group creation reporting failure while Windows was still waiting for taskbar-pin confirmation.
- Stale pinned groups appearing to do nothing after their group directory was manually removed; the recovery dialog is now owned by the resident host and brought to the foreground.
- Obsolete lifecycle fields producing compiler warnings.
