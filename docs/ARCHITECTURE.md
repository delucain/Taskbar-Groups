# Architecture

## Overview

Taskbar Groups uses one executable, `Taskbar-GroupMenu.exe`, in several modes:

| Mode | Invocation | Purpose |
|---|---|---|
| Resident host | `Taskbar-GroupMenu.exe --resident-host` | Owns WinForms state, IPC server, taskbar click tracker, menu hierarchy cache, and in-memory icon cache. |
| Taskbar launcher | `Taskbar-GroupMenu.exe "Group Name"` | Fast client that forwards a group request to the resident host and exits. |
| Explorer Add Link | `Taskbar-GroupMenu.exe --pin-to-group "path"` | Opens Add Link for a file/directory selected through Explorer. |
| Link Properties worker | `Taskbar-GroupMenu.exe --link-properties-worker "path.lnk"` | Keeps a message loop alive while the native shortcut property sheet is open. |

## Resident-host lifecycle

At Windows logon the installer-created Run entry starts `Taskbar-GroupMenu.exe --resident-host`.

The host:

1. Creates per-user data directories.
2. Attempts to contact an existing host.
3. Uses a named mutex to elect a host if none responds.
4. Initializes WinForms only in the elected host.
5. Creates the hidden owner form and reusable `ContextMenuStrip`.
6. Installs the low-level taskbar mouse tracker.
7. Starts the named-pipe server.
8. Queues incremental prewarming of configured groups.
9. Enters the WinForms message loop.

A real menu request takes priority over background prewarming.

## IPC

The host and launchers communicate through a local named pipe. IPC identity includes the Windows session ID and current-user SID. The same suffix scopes the host mutex and pipe names.

The server creates an explicit `PipeSecurity` descriptor granting the current user full control. The pipe is the authoritative liveness check; the mutex is host-election state, not proof of a functioning server.

## Menu request path

When the resident host receives a group request it first resolves `%LOCALAPPDATA%\Taskbar-Groups\Groups\<Group Name>` and verifies that the directory exists.

If it is missing, cached menu content is not used. The host displays an owned foreground recovery dialog that can recreate the empty group, unpin the stale taskbar shortcut, or cancel.

If the group exists, its lightweight filesystem signature determines whether the cached `ToolStripItem[]` hierarchy can be reused.

## Menu hierarchy cache

Each group has a cached `ToolStripItem[]` hierarchy in the resident process. A signature derived from top-level filesystem entries determines whether it remains valid. Management operations explicitly invalidate affected groups.

When switching menus, old items are detached from the live `ContextMenuStrip` before disposal. Permanent disposal is deferred until after replacement content paints.

## Icon cache

Menu icons use two cache layers:

1. In-memory `Bitmap` cache in the resident process.
2. Persistent PNG cache under `%LOCALAPPDATA%\Taskbar-Groups\Cache\MenuIcons`.

Cache identity is based on the local group item rather than target application metadata, avoiding unnecessary probes of slow or sleeping target storage.

The installed `Icon Library` is separate from the rendered menu-icon cache. It is starter/user-owned mutable content: Setup seeds missing files but does not overwrite or uninstall existing library files.

## Taskbar positioning

The resident host records low-level mouse-down coordinates before the short-lived launcher starts. The click is accepted only if it is recent and lies inside the Windows taskbar rectangle.

The popup is anchored to the desktop-facing edge of the taskbar using that original click coordinate. Later cursor movement is not sampled for positioning.

## Taskbar pin management

Each group has a source shortcut under `%LOCALAPPDATA%\Taskbar-Groups\Pin to Taskbar`.

For new groups, Taskbar Groups submits a native taskbar-pin request through Windows' taskband `IPinnedList3` interface. Legacy shell verbs remain fallbacks for older behavior. A successful native request is asynchronous because Windows may display confirmation UI.

Unpin and delete operations use the taskbar pin state plus the source shortcut. Delete Group attempts to unpin before removing the group directory and source shortcut.

## Explorer integration

Setup registers **Pin to Taskbar Group** for both files and directories. Explorer launches `--pin-to-group`, which initializes a standalone Add Link dialog rather than contacting or becoming the resident host.

The dialog requires a destination group and can create a new group inline.

## Link Properties worker

Windows shortcut property sheets are native/modeless. `--link-properties-worker` isolates this operation in a dedicated process with a hidden WinForms owner and message loop. The worker exits after the property sheet closes.

## Filesystem data model

A group is a directory under:

```text
%LOCALAPPDATA%\Taskbar-Groups\Groups\<Group Name>\
```

Display order is encoded in numeric filename prefixes:

```text
010 - Visual Studio.lnk
020 - GitHub.url
030 - Build Tools.heading
040 - PowerShell.lnk
050 - Spacer.separator
```

The filesystem is the source of truth; there is no separate group database.
