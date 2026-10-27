# Taskbar Groups no longer initializes per-user data from the elevated
# installer. Taskbar-GroupMenu.exe creates the current user's data directories
# under %LOCALAPPDATA%\Taskbar-Groups when it runs.
#
# Legacy pre-v1 data should be migrated explicitly with:
#   Migrate-LegacyTaskbarGroups.ps1
#
# This file is retained only so older working trees do not accidentally keep
# using the previous elevated per-user installation behavior.
Write-Host "No per-user installation step is required."
