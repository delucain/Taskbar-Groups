# Changelog

## 1.0.3 — In development

Current stable baseline before the final planned 1.0.3 feature.

### Added

- Persistent resident menu host.
- Startup group/menu prewarming.
- Per-group resident `ToolStripItem` hierarchy cache.
- Persistent rendered menu-icon cache.
- Pipe-first resident-host discovery and diagnostics.
- Session + user-SID scoped IPC names.
- Explicit current-user named-pipe ACL.
- Dedicated native Link Properties worker process.
- Timestamp/PID-specific performance logs controlled by `TASKBAR_GROUPS_PERF=1`.

### Changed

- Secondary taskbar launchers forward requests before initializing WinForms.
- Menu positioning uses the original low-level taskbar click rather than the later cursor location.
- Previous menu items are detached from the live menu before deferred disposal.
- `.ps1` shortcut creation preserves existing user arguments while ensuring the script is supplied as the file target.
- Release builds stop stale resident hosts before build/testing and start a fresh host after installation.

### Fixed

- First-use and repeated menu latency caused by repeated WinForms/menu initialization.
- Menu placement following rapid cursor movement after a taskbar click.
- Link Properties failing because its native modeless property sheet outlived the launching command path.
- Named-pipe access failures between the resident host and normal taskbar launchers.
- Obsolete lifecycle fields producing compiler warnings.

### Pending

- One final feature before the 1.0.3 release is tagged.
