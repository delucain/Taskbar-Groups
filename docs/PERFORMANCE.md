# Performance and Caching

## Design goal

Opening a taskbar group is an interactive UI operation, so the important latency is the path between the taskbar click and the first painted menu frame. The 1.0.3 architecture moves initialization, hierarchy construction, and most icon work out of that path.

## Persistent host

Earlier versions started WinForms and constructed a new menu host on every taskbar click. The resident-host architecture pays that cost once per Windows session.

Normal taskbar invocations are short-lived IPC clients and do not initialize WinForms when a responsive resident host is available.

## Startup prewarm

`--resident-host` queues configured groups for prewarming. Prewarm builds and caches menu hierarchies without displaying them.

Only one group is warmed per message-pump turn so a real menu request takes priority over background warmup.

## Menu hierarchy cache

The resident process caches fully constructed `ToolStripItem` arrays per group. Warm opens validate the group's filesystem signature and reattach the existing hierarchy.

This avoids repeatedly paying for menu-item allocation, layout setup, submenu construction, icon lookup/image construction, and static management-item construction.

Management operations invalidate the affected group. A missing group directory is detected before cached content is shown.

## Icon cache

Resolved menu icons are cached in memory and persisted as PNGs under:

```text
%LOCALAPPDATA%\Taskbar-Groups\Cache\MenuIcons
```

Cache identity is based on the local group item rather than repeatedly probing the shortcut target. This avoids waking slow, sleeping, removable, or network storage merely to render a menu icon.

## Deferred disposal

Replacing a live `ContextMenuStrip` hierarchy can trigger synchronous WinForms layout/disposal work. Taskbar Groups detaches old items first and defers disposal until after the replacement menu paints.

## Resident-host logging

Each resident host always creates one session log:

```text
%LOCALAPPDATA%\Taskbar-Groups\TaskbarGroups_yyyyMMdd-HHmmss.log
```

The same file is used until that resident process exits. A replacement host creates a new timestamped file.

The log always contains startup/lifecycle messages and errors reported through the application error handler. Unhandled WinForms UI-thread and AppDomain exceptions are also captured when possible.

Short-lived launcher processes do not create their own session log when they successfully forward to an existing host. If a launcher becomes the new resident host, its buffered startup diagnostics are flushed into that host's session log.

## Detailed performance records

Enable `PERF` records with:

```powershell
[Environment]::SetEnvironmentVariable('TASKBAR_GROUPS_PERF', '1', 'User')
```

Restart the resident host after changing the variable. Detailed records are written into the same resident-host session log rather than a separate performance file.

Useful events include:

- data-directory initialization;
- IPC identity and failures;
- resident-host construction;
- pipe-server startup;
- prewarm queue/start/completion;
- menu cache hit/miss;
- icon cache hit/miss;
- menu show/open/first-paint timing.

Disable detailed performance records with:

```powershell
[Environment]::SetEnvironmentVariable('TASKBAR_GROUPS_PERF', $null, 'User')
```
