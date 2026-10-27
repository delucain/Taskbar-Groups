# Taskbar Groups

Taskbar Groups is a lightweight Windows 10 taskbar launcher that
provides user-defined popup groups from pinned taskbar icons.

Each group is represented by a normal Windows shortcut pinned to the
taskbar. Clicking that shortcut launches `Taskbar-GroupMenu.exe` with
the group name. The launcher reads the corresponding directory under
`Groups\` and displays its contents as a native WinForms popup menu at
the mouse cursor.

The project is intentionally file-system driven. Group contents,
ordering, headings, separators, icons, and shortcuts are represented by
ordinary files rather than a separate configuration database.

## Features

-   Multiple independent taskbar groups.
-   Native Windows taskbar shortcuts for launching groups.
-   Supports `.lnk`, `.url`, `.exe`, `.bat`, `.cmd`, and `.ps1` group
    items.
-   Resolves Windows shortcut icons, including explicit icon resources
    and executable targets.
-   Supports packaged Windows application shortcuts when the `.lnk` was
    created through the Windows shell.
-   Numeric filename prefixes control display order.
-   Drag-and-drop reordering automatically normalizes prefixes to `010`,
    `020`, `030`, and so on.
-   Section headings and separators can be inserted between launcher
    items and reordered with the rest of the group.
-   Headers and separators use distinct styling so sections are visually
    apparent.
-   Per-item management menu for moving, inserting, renaming, changing
    icons, removing items, and opening shortcut/file properties.
-   Group-level management menu for adding links, headings, separators,
    renaming groups, changing group icons, creating groups, and deleting
    groups.
-   Dark/light mode follows the Windows app theme.
-   Group menus reopen at the same screen position after management
    operations.
-   Reusable icon library for group shortcuts.
-   No third-party runtime is required. The launcher is built with the
    .NET Framework C# compiler included with Windows.

## Installed layout

Taskbar Groups separates read-only application files from per-user data.

```text
C:\Program Files\Taskbar Groups\
├── Taskbar-GroupMenu.exe
├── Icon Library\
│   └── *.ico
└── _templates\
    ├── Section Heading.heading
    └── Spacer.separator
```

```text
%LOCALAPPDATA%\Taskbar-Groups\
├── Groups\
│   └── <Group Name>\
└── Pin to Taskbar\
    └── <Group Name>.lnk
```

The source repository may live anywhere. Build scripts resolve paths from
their own location rather than requiring a fixed development directory.

## Requirements

- Windows 10 or later.
- Windows PowerShell 5.1 or later for development/maintenance scripts.
- .NET Framework 4.x with the standard Windows C# compiler (`csc.exe`) to build from source.
- Inno Setup 6 to create the distributable installer.

## Installation

For normal users, run:

```text
Taskbar-Groups-Setup-v1.0.0.exe
```

The installer places application files in `C:\Program Files\Taskbar Groups`
and user data in `%LOCALAPPDATA%\Taskbar-Groups`.

When upgrading an older development-layout installation, the installer looks
for legacy groups under `C:\code\_Scripts\Taskbar-Groups\Groups`, copies any
groups that do not already exist in the new user-data directory, and rebuilds
pin-ready shortcuts so they target the installed executable. Existing user
data is not overwritten.

Uninstalling the application intentionally leaves `%LOCALAPPDATA%\Taskbar-Groups`
in place so uninstall/reinstall does not delete a user's groups.

## Building from source

From the repository root:

```powershell
.\Build-TaskbarGroupMenu.ps1
```

To build a release installer:

```powershell
.\Build-Release.ps1 -Version 1.0.0
```

The finished installer is written to `_release\Taskbar-Groups-Setup-v1.0.0.exe`.
`Build-Release.ps1` also removes Mark of the Web from release payloads before
packaging them.

`Install-TaskbarGroups.ps1` remains available as a manual/source-tree install
path. It requires elevation because it writes application files to Program
Files. `Setup-TaskbarGroup.ps1` creates or refreshes an individual group's
pin-ready shortcut in `%LOCALAPPDATA%\Taskbar-Groups\Pin to Taskbar`.

## Creating a group

Groups can be created from the launcher itself:

``` text
Group Options
└── New Group
```

The New Group dialog accepts a group name and an optional icon. Creating
a group:

1.  Creates `Groups\<Group Name>\`.
2.  Creates a shortcut under `Pin to Taskbar\<Group Name>.lnk`.
3.  Configures the shortcut to launch
    `Taskbar-GroupMenu.exe "<Group Name>"`.
4.  Opens/selects the new shortcut so it can be pinned normally.

Groups can also be prepared with `Setup-TaskbarGroup.ps1`.

## Using a group

Click a pinned group icon on the taskbar. The launcher opens the
corresponding popup menu at the current cursor position.

Selecting a normal item launches it and closes the popup.

Supported launchable item types are:

``` text
.lnk
.url
.exe
.bat
.cmd
.ps1
```

The group directory can also contain:

``` text
.heading
.separator
```

These are layout elements and are not launched.

## Ordering

Items are ordered by the numeric prefix in their filenames.

The normal numbering convention is:

``` text
010 - First Item.lnk
020 - Second Item.lnk
030 - Third Item.lnk
040 - Fourth Item.lnk
```

The gaps make manual insertion possible without immediately renumbering
the entire group.

### Drag-and-drop ordering

Items, headings, and separators can be reordered directly in the popup
menu.

Drag an item to the desired position and release it. After the drop, the
group is renumbered in canonical increments of ten:

``` text
010
020
030
040
...
```

This makes the filesystem ordering match the visual ordering.

### Renumbering utility

`Renumber-TaskbarGroups.ps1` is intended for bulk normalization/setup.

It alphabetically renumbers group contents in increments of ten.

Do **not** run it casually after manually arranging a group with
headings and separators. Its purpose is bulk alphabetical renumbering,
so it can intentionally destroy a hand-arranged section layout.

## Headers and separators

Headers and separators are ordinary files using custom extensions.

Templates are stored in:

``` text
_templates\Section Heading.heading
_templates\Spacer.separator
```

Use the launcher commands rather than manually copying the templates
when convenient:

``` text
Group Options
├── Add Header
└── Add Separator
```

Individual items also provide:

``` text
Insert Header Above
Insert Separator Below
```

Headers and separators participate in normal ordering and drag-and-drop
reordering.

## Adding links

At the group level:

``` text
Group Options
└── Add Link
```

For a specific insertion point, right-click an existing item and choose:

``` text
Add Link Below
```

The Add Link dialog allows selection of:

-   Target
-   Display name
-   Optional icon source

The resulting shortcut is created in the group's directory with an
appropriate numeric prefix.

## Item management

Right-click a launcher item to open its management submenu.

Available commands include:

``` text
Move Up
Move Down
────────────────────────
Insert Header Above
Insert Separator Below
────────────────────────
Add Link Below
Rename
Change Icon
Remove
────────────────────────
Open File Location
Link Properties
File Properties
```

Commands that do not apply to a particular item type are disabled or
omitted.

## Group management

The bottom of every group contains:

``` text
Open <Group> folder
Group Options
```

`Group Options` provides:

``` text
Add Link
Add Header
Add Separator
────────────────────────
Rename Group
Change Group Icon
New Group
Delete Group
```

Deleting a group removes the group's launcher data and pin-ready
shortcut. It does not delete the applications or files targeted by
shortcuts inside the group.

## Icon library

`Icon Library\` contains reusable `.ico` files intended primarily for
the taskbar group shortcuts.

Group icons use a common visual language so they can be distinguished
from ordinary application icons on the taskbar.

The library is source material and should be kept under version control.

## Shortcut icon resolution

For `.lnk` files, the launcher attempts to resolve icons in this order:

1.  Explicit `IconLocation` stored in the shortcut, including resource
    index.
2.  The shortcut's target executable.
3.  Windows shell-associated shortcut icon fallback.

Executable and DLL icon resources are extracted through the Windows
shell/native icon APIs.

This logic is important for shortcuts whose icons are stored in
executable resources rather than standalone `.ico` files.

## Dark and light themes

The launcher reads:

``` text
HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme
```

each time it is invoked.

Dark mode uses a custom WinForms renderer. Light mode uses the standard
Windows appearance. Headers and separators receive additional styling to
distinguish structural rows from normal launcher items.

## Building after source changes

`Taskbar-GroupMenu.cs` is the **canonical source file**.

After editing it, rebuild with:

``` powershell
.\Build-TaskbarGroupMenu.ps1
```

Do not create alternate source filenames for individual fixes.

## Updating the project

The canonical updater is:

``` text
Update-TaskbarGroupMenu.ps1
```

Future source updates should replace this same updater rather than
creating fix-specific installers.

The update workflow is:

1.  Back up the existing `Taskbar-GroupMenu.cs` into `_backups\`.
2.  Replace the canonical `Taskbar-GroupMenu.cs`.
3.  Invoke `Build-TaskbarGroupMenu.ps1`.
4.  Continue using the same canonical filenames.

This avoids accumulating scripts such as:

``` text
Install-SomeFix.ps1
Install-AnotherFix.ps1
Taskbar-GroupMenu-new.cs
Taskbar-GroupMenu-final.cs
Taskbar-GroupMenu-final2.cs
```

The project root should always make it obvious which source and build
scripts are current.

## Git/version-control policy

Source, scripts, templates, and reusable icon-library assets are intended
to remain visible to Git.

The contents of `Groups\` and `Pin to Taskbar\` are **user-specific
runtime/configuration data** and are not committed. The directories
themselves are retained in the repository with `.gitkeep` placeholders so
a fresh checkout has the expected structure.

Generated, transient, and user-specific files are ignored:

-   `Taskbar-GroupMenu.exe`
-   everything under `Groups\` except `Groups\.gitkeep`
-   everything under `Pin to Taskbar\` except `Pin to Taskbar\.gitkeep`
-   `_backups\`
-   compiler/debug artifacts such as `.pdb`
-   temporary files
-   editor/IDE metadata
-   downloaded update ZIPs
-   accidental fix-specific installer/update packages
-   timestamped source backups

There is no blanket `*.lnk` ignore rule. The user-specific directories are
ignored regardless of the file types stored inside them.

## Canonical files

For future maintenance:

  Purpose                   Canonical file
  ------------------------- -------------------------------
  Launcher source           `Taskbar-GroupMenu.cs`
  Launcher executable       `Taskbar-GroupMenu.exe`
  Build                     `Build-TaskbarGroupMenu.ps1`
  Updates                   `Update-TaskbarGroupMenu.ps1`
  Full installation/setup   `Install-TaskbarGroups.ps1`
  Individual group setup    `Setup-TaskbarGroup.ps1`
  Bulk renumbering          `Renumber-TaskbarGroups.ps1`

If a future change requires modifying the launcher, modify
`Taskbar-GroupMenu.cs` and rebuild it. Do not introduce another
competing "current" source or build script.
