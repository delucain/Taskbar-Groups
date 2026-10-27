param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

$Root        = $PSScriptRoot
$ReleaseRoot = Join-Path $Root '_release'
$Launcher    = Join-Path $Root 'Taskbar-GroupMenu.exe'
$BuildScript = Join-Path $Root 'Build-TaskbarGroupMenu.ps1'
$Iss         = Join-Path $Root 'installer\Taskbar-Groups.iss'

New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

Write-Host "Building Taskbar Groups $Version..."

& $BuildScript

if (-not (Test-Path -LiteralPath $Launcher -PathType Leaf)) {
    throw "Launcher build did not create: $Launcher"
}

# Prevent existing Mark-of-the-Web metadata from being carried into the
# installer payload. A downloaded installer may itself receive MOTW later;
# this step only cleans the files being packaged.
foreach ($path in @(
    $Launcher,
    (Join-Path $Root 'Icon Library'),
    (Join-Path $Root '_templates')
)) {
    if (-not (Test-Path -LiteralPath $path)) {
        continue
    }

    $item = Get-Item -LiteralPath $path

    if ($item -is [System.IO.DirectoryInfo]) {
        Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue |
            Unblock-File -ErrorAction SilentlyContinue
    }
    else {
        Unblock-File -LiteralPath $path -ErrorAction SilentlyContinue
    }
}

$possible = @()

if (${env:ProgramFiles(x86)}) {
    $possible += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
}

if ($env:ProgramFiles) {
    $possible += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
}

$iscc = $possible |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install Inno Setup 6, then rerun Build-Release.ps1.'
}

if (-not (Test-Path -LiteralPath $Iss -PathType Leaf)) {
    throw "Inno Setup script was not found: $Iss"
}

& $iscc "/DMyAppVersion=$Version" "/DOutputDir=$ReleaseRoot" $Iss

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$setup = Join-Path $ReleaseRoot "Taskbar-Groups-Setup-v$Version.exe"

if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Expected installer was not created: $setup"
}

Unblock-File -LiteralPath $setup -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Release built:"
Write-Host "  $setup"
