# User Guide

## Groups

Each Taskbar Group is a directory under `%LOCALAPPDATA%\Taskbar-Groups\Groups`. A matching shortcut is created under `%LOCALAPPDATA%\Taskbar-Groups\Pin to Taskbar` for normal Windows taskbar pinning.

Create a group from:

```text
Group Options
└── New Group
```

You can assign a custom group icon during creation or change it later with **Change Group Icon**.

## Adding links

Use **Group Options > Add Link** to append a link, or an existing item's **Add Link Below** command to insert at a specific location.

Supported launchable item types are:

```text
.lnk
.url
.exe
.bat
.cmd
.ps1
```

The Add Link dialog accepts a target, optional arguments, display name, and optional icon source.

### PowerShell scripts

When a `.ps1` file is selected with a blank Arguments field, Taskbar Groups creates a shortcut through PowerShell and supplies:

```text
-NoProfile -ExecutionPolicy Bypass -File "<script>"
```

PowerShell 7 (`pwsh.exe`) is preferred when installed; otherwise Windows PowerShell 5.1 is used.

If the Arguments field already contains user input, Taskbar Groups preserves it and ensures the selected script is still passed as the `-File` target rather than replacing the user's arguments.

## Headings and separators

Structural rows use ordinary files:

```text
.heading
.separator
```

Add them from **Group Options**, or insert them relative to an existing item.

## Ordering

Ordering is encoded in filename prefixes:

```text
010 - First Item.lnk
020 - Second Item.lnk
030 - Section.heading
040 - Third Item.lnk
050 - Spacer.separator
```

Items can be dragged directly in the popup menu. After reordering, Taskbar Groups normalizes prefixes in increments of ten.

## Item management

Depending on item type, the item submenu can provide:

```text
Move Up
Move Down
Insert Header Above
Insert Separator Below
Add Link Below
Rename
Change Icon
Remove
Open File Location
Link Properties
File Properties
```

**Link Properties** opens the native Windows shortcut property sheet for `.lnk` files. **File Properties** opens the native property sheet for the underlying filesystem item where applicable.

## Group management

The bottom of a group contains its group folder command and **Group Options**. Group-level commands include:

```text
Add Link
Add Header
Add Separator
Rename Group
Change Group Icon
New Group
Delete Group
```

Deleting a group removes its Taskbar Groups directory and its pin-ready shortcut. It does not delete applications or files targeted by shortcuts inside the group.

## Icons

The installer includes two icon sets:

- `Icon Library\` — normal unframed icons.
- `Icon Library\Group Icons\` — bordered variants intended to visually identify taskbar group launchers.

Custom icons may also be selected from `.ico`, `.exe`, and `.dll` files.

For `.lnk` files, Taskbar Groups resolves icons in this order:

1. Explicit shortcut `IconLocation`, including resource index.
2. Shortcut target executable.
3. Windows shell-associated fallback.

## User data and uninstall

User configuration is intentionally separate from installed application files. Uninstalling or upgrading Taskbar Groups does not delete `%LOCALAPPDATA%\Taskbar-Groups`.
