$ErrorActionPreference = 'Stop'

$Root = 'C:\code\_Scripts\Taskbar-Groups'
$Source = Join-Path $PSScriptRoot 'Taskbar-GroupMenu.cs'
$Destination = Join-Path $Root 'Taskbar-GroupMenu.cs'
$BackupRoot = Join-Path $Root '_backups'
$BuildScript = Join-Path $Root 'Build-TaskbarGroupMenu.ps1'
$GroupsRoot = Join-Path $Root 'Groups'
$PinRoot = Join-Path $Root 'Pin to Taskbar'

if (-not (Test-Path -LiteralPath $Source)) {
    throw "Updated source not found: $Source"
}

if (-not (Test-Path -LiteralPath $Root)) {
    throw "Taskbar Groups project not found: $Root"
}

New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null

if (Test-Path -LiteralPath $Destination) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $BackupRoot "Taskbar-GroupMenu-$stamp.cs"
    Copy-Item -LiteralPath $Destination -Destination $backup -Force
    Write-Host "Backed up source to: $backup"
}

$sourceFull = [System.IO.Path]::GetFullPath($Source)
$destinationFull = [System.IO.Path]::GetFullPath($Destination)

if (-not $sourceFull.Equals($destinationFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}
else {
    Write-Host "Updated source is already the canonical source; skipping self-copy."
}

# Remove Mark of the Web from existing user-specific launcher data.
# This is intentionally scoped to Groups and Pin to Taskbar.
foreach ($path in @($GroupsRoot, $PinRoot)) {
    if (Test-Path -LiteralPath $path) {
        Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
            Unblock-File -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $BuildScript)) {
    throw "Build script not found: $BuildScript"
}

& $BuildScript
if ($LASTEXITCODE -ne 0) {
    throw "Taskbar-GroupMenu build failed with exit code $LASTEXITCODE."
}

Write-Host ''
Write-Host 'Taskbar Groups updated successfully.'
Write-Host 'Existing group and pin shortcuts were unblocked.'
Write-Host 'New shortcuts created by the launcher will have Zone.Identifier removed automatically.'
