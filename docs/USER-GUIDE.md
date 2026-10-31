# User Guide

## Groups

Each Taskbar Group is a directory under:

```text
%LOCALAPPDATA%\Taskbar-Groups\Groups\<Group Name>\
```

A matching source shortcut is stored under:

```text
%LOCALAPPDATA%\Taskbar-Groups\Pin to Taskbar\<Group Name>.lnk
```

Create a group from **Group Options > New Group**. You may select a custom icon during creation or change it later with **Change Group Icon**.

When a group is created, Taskbar Groups submits a native Windows request to pin its shortcut to the taskbar. Windows may display a confirmation prompt. The group is already created while that prompt is waiting, so the Taskbar Groups dialog does not time out if the prompt is not answered immediately.

## Adding links

Use **Group Options > Add Link** to append a link, or an existing item's **Add Link Below** command to insert at a specific location.

The Add Link dialog contains:

```text
Target
Arguments
Name
Icon
Group
```

The Group field is required. When Add Link is opened from a group menu, that group is selected automatically.

Supported launchable item types are:

```text
.lnk
.url
.exe
.bat
.cmd
.ps1
```

### Adding from Windows Explorer

Setup adds **Pin to Taskbar Group** to the Explorer context menu for files and directories. Selecting it opens Add Link with the selected path already populated. No group is preselected when the dialog originates from Explorer.

The Group list contains all existing groups plus **<Create New Group...>**. Selecting it opens New Group modally. Successful creation refreshes the list and selects the new group; cancelling restores the previous selection.

### PowerShell scripts

When a `.ps1` file is selected with a blank Arguments field, Taskbar Groups creates a shortcut through PowerShell and supplies:

```text
-NoProfile -ExecutionPolicy Bypass -File "<script>"
```

PowerShell 7 (`pwsh.exe`) is preferred when installed; otherwise Windows PowerShell 5.1 is used.

If Arguments already contains user input, Taskbar Groups preserves it and ensures the selected script remains the `-File` target.

For PowerShell-backed links, **Open File Location** and **File Properties** resolve the script itself rather than the PowerShell host when possible.

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

**Link Properties** opens the native Windows shortcut property sheet through a dedicated worker process so the modeless property window remains alive. **File Properties** opens the native property sheet for the underlying filesystem item where applicable.

## Group management

Group-level commands include:

```text
Add Link
Add Header
Add Separator
Rename Group
Change Group Icon
New Group
Unpin Group from Taskbar
Delete Group
```

**Unpin Group from Taskbar** removes the taskbar pin without deleting the group.

**Delete Group** attempts to unpin the group first, then removes its Taskbar Groups directory and source shortcut. It does not delete applications or files targeted by shortcuts inside the group.

Renaming a group updates its group directory and source shortcut. If the old group is already pinned, Windows may require the old pin to be removed and the updated shortcut pinned again.

## Missing-group recovery

If a taskbar shortcut is still pinned after its group folder has been manually removed, clicking the stale pin opens a foreground recovery dialog with:

- **Recreate Group** — recreates the missing empty group directory in place. It does not create another shortcut or submit another pin request.
- **Unpin from Taskbar** — removes the stale taskbar pin.
- **Cancel** — leaves the current state unchanged.

## Icons

The installer includes a starter `Icon Library`. Custom icons may also be selected from `.ico`, `.exe`, and `.dll` files.

For `.lnk` items, Taskbar Groups resolves icons in this order:

1. Explicit shortcut `IconLocation`, including resource index.
2. Shortcut target executable.
3. Windows shell-associated fallback.

### Icon Library ownership

The bundled Icon Library is starter content, not immutable installer-owned application data. Users may add, replace, or modify icons in the installed library.

During upgrades, Setup installs a bundled starter icon only when that filename is missing. An existing same-named icon is never overwritten, even if its contents differ from the bundled copy.

Uninstall preserves the Icon Library, including user-added and user-customized icons. If bundled artwork needs to change in a future release, it should be introduced under a new human-readable filename rather than replacing an existing starter icon.

## User data and uninstall

User group configuration is separate from installed application binaries. Upgrading or uninstalling Taskbar Groups preserves `%LOCALAPPDATA%\Taskbar-Groups`.
