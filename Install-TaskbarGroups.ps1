$ErrorActionPreference = 'Stop'

$Root = 'C:\code\_Scripts\Taskbar-Groups'
$Package = Split-Path -Parent $MyInvocation.MyCommand.Path

$PinFolder = Join-Path $Root 'Pin to Taskbar'
$IconLibrary = Join-Path $Root 'Icon Library'

New-Item -ItemType Directory -Force -Path `
    $Root, `
    $PinFolder, `
    $IconLibrary | Out-Null

# Only copy the package files when the installer is being run
# from somewhere other than the final installation directory.
$packagePath = [System.IO.Path]::GetFullPath($Package).TrimEnd('\')
$rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')

if (-not $packagePath.Equals(
    $rootPath,
    [System.StringComparison]::OrdinalIgnoreCase
)) {
    Copy-Item `
        (Join-Path $Package 'Taskbar-GroupMenu.cs') `
        $Root `
        -Force

    Copy-Item `
        (Join-Path $Package 'Build-TaskbarGroupMenu.ps1') `
        $Root `
        -Force

    Copy-Item `
        (Join-Path $Package 'Setup-TaskbarGroup.ps1') `
        $Root `
        -Force

    $packageIcons = Join-Path $Package 'Icon Library\*.ico'

    Copy-Item `
        $packageIcons `
        $IconLibrary `
        -Force
}

# Compile the fast launcher.
& (Join-Path $Root 'Build-TaskbarGroupMenu.ps1')

# Recreate the six current group shortcuts using the new icon library.
$groups = @(
    '3D Printing',
    'Communications',
    'Development',
    'Games',
    'Media Tools',
    'Utilities'
)

foreach ($group in $groups) {
    & (Join-Path $Root 'Setup-TaskbarGroup.ps1') `
        -Group $group
}

Write-Host ''
Write-Host 'Installation complete.'
Write-Host ''
Write-Host "Pin-ready shortcuts:"
Write-Host "  $PinFolder"
Write-Host ''
Write-Host 'Your existing Taskbar Groups content folders were not modified.'
Write-Host ''
Write-Host 'Next:'
Write-Host '  1. Unpin the old six group shortcuts.'
Write-Host '  2. Open the Pin to Taskbar folder.'
Write-Host '  3. Pin the six replacement shortcuts.'
Write-Host ''

Start-Process explorer.exe -ArgumentList "`"$PinFolder`""
