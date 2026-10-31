# Taskbar Groups 1.0.3 Release Notes

> **Draft:** 1.0.3 is not final yet. This document records the stable baseline before the final planned feature is added.

## Major performance architecture change

Taskbar Groups now uses a persistent resident menu host instead of constructing a new WinForms host for every taskbar click. Normal taskbar invocations forward the requested group to the resident process over local IPC and exit quickly.

The resident host prewarms configured groups at startup and caches complete menu hierarchies. Rendered menu icons are also cached both in memory and on disk. Together these changes remove most repeated startup, menu-construction, and icon-resolution work from the interactive menu-open path.

## Reliable taskbar positioning

The resident host captures the original low-level taskbar mouse-down location and validates it against the Windows taskbar rectangle. Menu positioning no longer depends on where the cursor happens to be after the launcher process starts, fixing menus that could appear under a rapidly moved cursor instead of beside the clicked taskbar group.

## Resident-host IPC hardening

Resident-host discovery is pipe-first: the IPC endpoint is the liveness test, while the mutex is used only to elect a host when no responsive server exists.

IPC object names are scoped by Windows session ID and current-user SID. The named pipe is created with an explicit same-user access-control rule so ordinary taskbar launchers can communicate with the resident host reliably.

## Menu and icon caching

- Per-group `ToolStripItem` hierarchies are cached in the resident process.
- Lightweight filesystem signatures invalidate stale group caches.
- Previous menu hierarchies are detached before disposal work is performed.
- Icon cache validity is based on the local group item, avoiding unnecessary target-storage probes.
- Persistent menu icons are stored under `%LOCALAPPDATA%\Taskbar-Groups\Cache\MenuIcons`.

## Shortcut and management improvements included in this baseline

- Native Link Properties now runs through a dedicated worker process so the modeless Windows property sheet remains alive and functional.
- `.ps1` links prefer PowerShell 7 when available and fall back to Windows PowerShell 5.1.
- Existing user-entered arguments are preserved when creating PowerShell links while ensuring the script remains the `-File` target.
- Release builds stop stale resident hosts before replacing/testing the executable.
- Performance diagnostics use timestamped/PID-specific log filenames.
- Obsolete lifecycle fields that generated compiler warnings were removed.

## Upgrade behavior

Application binaries remain under Program Files. User groups, pin-ready shortcuts, and caches remain under `%LOCALAPPDATA%\Taskbar-Groups`. User group data is preserved across upgrades and uninstall.

## Before final 1.0.3

One additional feature is planned. After that feature is merged, update this file with its user-visible behavior and run the regression checklist in [DEVELOPMENT.md](DEVELOPMENT.md) before tagging the release.
