# Taskbar Groups

Taskbar Groups is a lightweight Windows 10 taskbar launcher for user-defined popup groups. Each group is represented by a Windows shortcut pinned to the taskbar. Clicking it asks a small resident host to display the group's menu adjacent to the original taskbar click.

The project is deliberately filesystem-driven. Groups, shortcuts, headings, separators, ordering, and icons are ordinary files rather than records in a configuration database.

## Features

- Multiple independent taskbar groups.
- Persistent per-user menu host for low-latency menu opening.
- Startup prewarming of group menus and icon caches.
- In-memory caching of complete `ToolStripItem` menu hierarchies.
- Persistent rendered-icon cache under `%LOCALAPPDATA%\Taskbar-Groups\Cache\MenuIcons`.
- Taskbar-relative menu placement based on the original low-level mouse-down location.
- Automatic taskbar pin request when a group is created.
- Group commands to unpin or delete a group; deletion unpins first.
- Recovery dialog for stale taskbar pins whose group folder is missing, with **Recreate Group**, **Unpin from Taskbar**, and **Cancel**.
- Explorer **Pin to Taskbar Group** context-menu command for files and directories.
- Add Link dialog with explicit group selection and inline **<Create New Group...>** support.
- Supports `.lnk`, `.url`, `.exe`, `.bat`, `.cmd`, and `.ps1` launch items.
- PowerShell 7 is preferred for `.ps1` shortcuts when available; Windows PowerShell 5.1 is the fallback.
- Existing command-line arguments are preserved when creating PowerShell links while ensuring the selected script remains the `-File` target.
- Section headings and separators.
- Drag-and-drop ordering with normalized numeric prefixes (`010`, `020`, `030`, ...).
- Per-item commands for moving, inserting, renaming, changing icons, removing items, opening file locations, and opening native Windows property dialogs.
- Dark/light appearance follows the Windows app theme.
- Starter icon library that users may extend or customize without upgrades overwriting their files.
- Dedicated application/Explorer context-menu icon.
- No third-party runtime required by the launcher.

## How it works

Taskbar shortcuts invoke:

```text
Taskbar-GroupMenu.exe "<Group Name>"
```

At logon, the installer registers:

```text
Taskbar-GroupMenu.exe --resident-host
```

The resident host initializes WinForms once, starts the menu IPC endpoint, prewarms configured groups, and remains idle. A later taskbar click starts a short-lived launcher process which forwards the requested group over a same-user named pipe and exits. The resident process validates the group, attaches its cached menu hierarchy, and displays it.

The host is isolated by Windows session ID and user SID. The named pipe also has an explicit ACL granting the current user access.

See [Architecture](docs/ARCHITECTURE.md) for details.

## Installation

Download and run:

```text
Taskbar-Groups-Setup-v1.0.3.exe
```

Application files are installed under:

```text
C:\Program Files\Taskbar Groups\
├── Taskbar-GroupMenu.exe
├── assets\
├── Icon Library\
└── _templates\
```

Per-user state lives under:

```text
%LOCALAPPDATA%\Taskbar-Groups\
├── Groups\
│   └── <Group Name>\
├── Pin to Taskbar\
│   └── <Group Name>.lnk
└── Cache\
    └── MenuIcons\
```

User group data is preserved during upgrades and uninstall. The installed `Icon Library` is also preserved because it becomes user-owned mutable content after Setup seeds it.

## Creating a group

From an existing group choose:

```text
Group Options
└── New Group
```

Creation:

1. Creates `%LOCALAPPDATA%\Taskbar-Groups\Groups\<Group Name>\`.
2. Creates `%LOCALAPPDATA%\Taskbar-Groups\Pin to Taskbar\<Group Name>.lnk`.
3. Configures the shortcut to launch `Taskbar-GroupMenu.exe "<Group Name>"`.
4. Submits the native Windows taskbar-pin request. Windows may display its own confirmation prompt; Taskbar Groups does not block while that prompt is waiting for input.

If a pinned group shortcut survives after its group directory is manually removed, launching it opens a recovery dialog that can recreate the empty group or unpin the stale shortcut.

## Adding items from Explorer

Setup adds **Pin to Taskbar Group** to the Windows Explorer context menu for files and directories. It opens the normal Add Link dialog with the selected path populated.

The **Group** field is required. The group list contains existing groups plus **<Create New Group...>**. Creating a group from this dialog refreshes the list and selects the new group.

See the [User Guide](docs/USER-GUIDE.md) for complete item and group management behavior.

## Building from source

### Requirements

- Windows 10 or later.
- Windows PowerShell 5.1 or later.
- .NET Framework 4.x with the standard Windows C# compiler (`csc.exe`).
- Inno Setup 6.

Build from the repository root:

```powershell
.\Build-Release.ps1 -Version 1.0.3
```

The build script compiles the launcher with `assets\TaskbarGroups.ico`, builds the Inno Setup installer, installs it locally for testing, writes an installer log under `_release`, and restarts the resident host using the freshly built executable. It does not restart Explorer.

See [Development](docs/DEVELOPMENT.md) for the release checklist.

## Logging and performance diagnostics

Every resident host writes one session log under:

```text
%LOCALAPPDATA%\Taskbar-Groups\TaskbarGroups_yyyyMMdd-HHmmss.log
```

Normal lifecycle messages and errors are always logged. Enable detailed `PERF` records with:

```powershell
[Environment]::SetEnvironmentVariable('TASKBAR_GROUPS_PERF', '1', 'User')
```

Restart the resident host after changing the variable. Disable detailed performance records with:

```powershell
[Environment]::SetEnvironmentVariable('TASKBAR_GROUPS_PERF', $null, 'User')
```

See [Performance and Caching](docs/PERFORMANCE.md).

## Repository layout

```text
Taskbar-Groups\
├── Taskbar-GroupMenu.cs
├── Build-Release.ps1
├── README.md
├── CHANGELOG.md
├── LICENSE
├── assets\
│   └── TaskbarGroups.ico
├── Icon Library\
├── _templates\
├── installer\
│   ├── Taskbar-Groups.iss
│   └── Install-UserData.ps1
└── docs\
    ├── ARCHITECTURE.md
    ├── DEVELOPMENT.md
    ├── PERFORMANCE.md
    ├── RELEASE-NOTES-1.0.3.md
    └── USER-GUIDE.md
```

`installer\Install-UserData.ps1` remains for compatibility with older working trees. Current versions initialize their own `%LOCALAPPDATA%\Taskbar-Groups` state.

## License

Taskbar Groups is released under the MIT License. See [LICENSE](LICENSE).
