# Performance and Caching

## Design goal

Opening a taskbar group is an interactive UI operation, so latency is dominated by the critical path between the taskbar click and the first painted menu frame. The 1.0.3 work moves expensive initialization and construction out of that path.

## Persistent host

Earlier versions started WinForms and constructed a new menu host on every taskbar click. The resident-host architecture pays that cost once per Windows session.

Normal taskbar invocations now act as short-lived IPC clients and do not initialize WinForms when a responsive resident host is available.

## Startup prewarm

`--resident-host` queues every configured group for prewarming. Prewarm builds and caches menu hierarchies without displaying them.

Only one group is warmed per message-pump turn. This prevents background warmup from blocking a real request that arrives before warmup has finished.

## Menu hierarchy cache

The resident process caches fully constructed `ToolStripItem` arrays per group. Warm opens validate the group's filesystem signature and reattach the existing hierarchy.

This avoids repeatedly paying for:

- menu-item allocation;
- text/layout setup;
- submenu construction;
- icon lookup and image construction;
- static management-item construction.

## Icon cache

Resolved menu icons are cached in memory and persisted as PNGs under:

```text
%LOCALAPPDATA%\Taskbar-Groups\Cache\MenuIcons
```

The persistent cache prevents a shortcut target on a sleeping HDD or other slow storage from being touched merely to render a menu icon.

## Deferred disposal

Replacing a live `ContextMenuStrip` hierarchy can trigger significant synchronous WinForms layout/disposal work. Taskbar Groups detaches old items first and defers disposal until after the replacement menu's first paint.

## Performance logging

Enable diagnostics with:

```powershell
[Environment]::SetEnvironmentVariable('TASKBAR_GROUPS_PERF', '1', 'User')
```

Restart the resident host after changing the environment variable. Logs are written to `%LOCALAPPDATA%\Taskbar-Groups` with timestamp, millisecond, and PID in the filename.

Useful events include:

- data-directory initialization;
- IPC identity and failures;
- resident-host construction;
- pipe-server startup;
- prewarm queue/start/completion;
- menu cache hit/miss;
- icon cache hit/miss;
- menu show/open/first-paint timing.

Disable logging with:

```powershell
[Environment]::SetEnvironmentVariable('TASKBAR_GROUPS_PERF', $null, 'User')
```


## Logging lifetime

When `TASKBAR_GROUPS_PERF=1`, Taskbar Groups writes one timestamped performance
log for the lifetime of the resident menu-host process. Opening additional groups
does not create additional files. If the resident process is stopped or restarted,
the replacement host creates a new timestamped log and uses it until that process
ends.

Short-lived launcher processes buffer their initial diagnostic messages only in
memory. If they successfully forward a group to the resident host, they exit
without creating a log file. If a launcher becomes the new resident host, those
startup messages are flushed into that host's session log.


## General resident-host log

Each resident-host process always creates one log when it starts:

`TaskbarGroups_yyyyMMdd-HHmmss.log`

The same file is used until that resident process exits. A replacement resident
host creates a new timestamped file.

The log always contains normal resident-host startup/lifecycle messages and errors
reported through the application's error handler. Unhandled WinForms UI-thread and
AppDomain exceptions are also captured when possible.

When `TASKBAR_GROUPS_PERF=1`, detailed `PERF` records are added to this same file.
When performance logging is disabled, the general startup/runtime/error log remains
active; only the detailed performance records are omitted.

Short-lived taskbar launcher processes do not create session logs.
