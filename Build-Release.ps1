param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

$Root        = $PSScriptRoot
$ReleaseRoot = Join-Path $Root '_release'
$Source      = Join-Path $Root 'Taskbar-GroupMenu.cs'
$Launcher    = Join-Path $Root 'Taskbar-GroupMenu.exe'
$Iss         = Join-Path $Root 'installer\Taskbar-Groups.iss'

New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

Write-Host "Building Taskbar Groups $Version..."

if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "Launcher source was not found: $Source"
}

$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

if (-not $csc) {
    throw 'Built-in .NET Framework C# compiler not found.'
}

& $csc /nologo /target:winexe /optimize+ `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /out:"$Launcher" "$Source"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $Launcher -PathType Leaf)) {
    throw "Launcher build did not create: $Launcher"
}

Unblock-File -LiteralPath $Launcher -ErrorAction SilentlyContinue
Write-Host "Built $Launcher" 

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

Write-Host ''
Write-Host "Installing Taskbar Groups $Version..."

$installProcess = Start-Process `
    -FilePath $setup `
    -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' `
    -Verb RunAs `
    -Wait `
    -PassThru

if ($installProcess.ExitCode -ne 0) {
    throw "Installer failed with exit code $($installProcess.ExitCode)."
}

Write-Host ''
Write-Host "Release installed successfully."
