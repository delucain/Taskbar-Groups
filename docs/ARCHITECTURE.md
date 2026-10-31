# Architecture

## Overview

Taskbar Groups consists of one executable, `Taskbar-GroupMenu.exe`, used in several modes:

| Mode | Invocation | Purpose |
|---|---|---|
| Resident host | `Taskbar-GroupMenu.exe --resident-host` | Owns the WinForms host/menu, IPC server, taskbar click tracker, menu hierarchy cache, and in-memory icon cache. |
| Taskbar launcher | `Taskbar-GroupMenu.exe "Group Name"` | Fast client. Forwards the requested group to the resident host and exits. |
| Link Properties worker | `Taskbar-GroupMenu.exe --link-properties-worker "path.lnk"` | Keeps a message loop alive while the native Windows shortcut property sheet is open. |

The same binary is used so installed group shortcuts remain simple and portable across upgrades.

## Resident-host lifecycle

At Windows logon the installer-created Run entry starts `Taskbar-GroupMenu.exe --resident-host`.

The host:

1. Creates the per-user data directories.
2. Attempts to contact an existing host.
3. Uses a named mutex to elect a host if none responds.
4. Initializes WinForms only in the process that becomes the host.
5. Creates the hidden owner form and reusable `ContextMenuStrip`.
6. Installs the low-level mouse click tracker.
7. Starts the named-pipe server.
8. Queues prewarming of all configured groups.
9. Enters the WinForms message loop.

Prewarming is intentionally incremental: one group is prepared per UI-message-pump turn. A real menu request therefore takes priority over background warmup.

## IPC

The host and launchers communicate through a local named pipe. IPC identity includes:

- Windows session ID.
- Current user's SID.

The same suffix is used for the host mutex and pipe names. This prevents unrelated users or sessions from colliding with one another.

The pipe server creates an explicit `PipeSecurity` descriptor granting the current user full control. This is required because normal taskbar launchers must be able to reach the already-running resident host regardless of how the host was started during installation/testing.

The pipe is the authoritative liveness check. The mutex is used for host election, not as proof that a functioning IPC server exists.

## Menu hierarchy cache

Each group has a cached `ToolStripItem[]` hierarchy in the resident process.

A lightweight signature derived from the group's top-level filesystem entries determines whether a cached hierarchy remains valid. On a cache hit, the hierarchy is reattached to the shared `ContextMenuStrip`; item construction and icon resolution are skipped.

Management operations invalidate the affected group cache.

When switching menus, old items are detached from the live `ContextMenuStrip` before disposal. Disposal is deferred until after the replacement menu paints so recursive WinForms cleanup does not sit on the menu-open critical path.

## Icon cache

Menu icons use two cache layers:

1. In-memory `Bitmap` cache in the resident process.
2. Persistent PNG cache under `%LOCALAPPDATA%\Taskbar-Groups\Cache\MenuIcons`.

Cache identity is based on the local group item rather than probing the target application's metadata. This is important for shortcuts whose targets live on slow, sleeping, removable, or network storage.

## Taskbar positioning

The resident host records low-level mouse-down coordinates before the short-lived taskbar launcher starts. When a group request arrives, the recorded click is accepted only if it is recent and lies inside the Windows taskbar rectangle.

The popup is then anchored to the desktop-facing edge of the taskbar using that original click coordinate. Later cursor movement is not sampled for positioning. This prevents the menu from following the mouse when the user moves quickly after clicking the taskbar icon.

## Link Properties worker

Windows shortcut property sheets are native/modeless. If the process that opens the sheet exits immediately, the property window disappears or fails to behave correctly.

`--link-properties-worker` isolates this operation in a dedicated process with a hidden WinForms owner and message loop. The worker monitors the native property window and exits after it closes.

## Filesystem data model

A group is a directory under:

```text
%LOCALAPPDATA%\Taskbar-Groups\Groups\<Group Name>\
```

Display order is encoded in numeric filename prefixes. For example:

```text
010 - Visual Studio.lnk
020 - GitHub.url
030 - Build Tools.heading
040 - PowerShell.lnk
050 - Spacer.separator
```

The filesystem is the source of truth; there is no separate group database.
