param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$Root        = $PSScriptRoot
$ReleaseRoot = Join-Path $Root '_release'
$Source      = Join-Path $Root 'Taskbar-GroupMenu.cs'
$Launcher    = Join-Path $Root 'Taskbar-GroupMenu.exe'
$Iss         = Join-Path $Root 'installer\Taskbar-Groups.iss'

New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null

Write-Host "Building Taskbar Groups $Version..."

# A released build may already have the new persistent menu host running.
# Stop it before replacing Taskbar-GroupMenu.exe.
Get-Process -Name 'Taskbar-GroupMenu' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 150

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

$AppIcon = Join-Path $Root 'assets\TaskbarGroups.ico'
if (-not (Test-Path -LiteralPath $AppIcon -PathType Leaf)) {
    throw "Application icon was not found: $AppIcon"
}

& $csc /nologo /target:winexe /optimize+ `
    /win32icon:"$AppIcon" `
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

$installLog = Join-Path $ReleaseRoot ("Taskbar-Groups-Setup-v{0}-install.log" -f $Version)
Remove-Item -LiteralPath $installLog -Force -ErrorAction SilentlyContinue

# Inno Setup exit code 5 means the user cancelled Setup. Do not suppress setup
# message boxes while diagnosing/installing: doing so can turn an actionable
# file/registry error into a bare cancellation exit code with no visible cause.
$installArguments = '/VERYSILENT /NORESTART /LOG="' + $installLog + '"'

$installProcess = Start-Process `
    -FilePath $setup `
    -ArgumentList $installArguments `
    -Verb RunAs `
    -Wait `
    -PassThru

if ($installProcess.ExitCode -ne 0) {
    Write-Host ''
    Write-Host "Installer log:"
    Write-Host "  $installLog"

    if (-not [String]::IsNullOrWhiteSpace($installLog) -and
        (Test-Path -LiteralPath $installLog -PathType Leaf)) {
        Write-Host ''
        Write-Host 'Last 40 installer log lines:'
        Get-Content -LiteralPath $installLog -Tail 40
    }

    if ($installProcess.ExitCode -eq 5) {
        throw "Installer was cancelled (Inno Setup exit code 5). Review the installer log above for the operation that caused Setup to abort."
    }

    throw "Installer failed with exit code $($installProcess.ExitCode). Review: $installLog"
}

Write-Host ''
Write-Host "Release installed successfully."


# Start the exact executable produced by this build in resident-host mode.
# Do not probe the install location: Inno Setup may install under an elevated
# account/context, while this build output path is deterministic and is the
# binary whose behavior we are testing.
$builtMenuHost = $Launcher

if (-not (Test-Path -LiteralPath $builtMenuHost -PathType Leaf)) {
    throw "Built menu host was not found: $builtMenuHost"
}

# The installer or a previous test run may have left an older resident host
# alive after the pre-build stop. Remove it immediately before starting the
# freshly built host so the new binary owns both the mutex and pipe.
Get-Process -Name 'Taskbar-GroupMenu' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 250

Write-Host ''
Write-Host "Starting resident menu host with --resident-host in the interactive user context..."

# Do not pass an arbitrary executable command line to explorer.exe. Explorer treats
# that syntax as shell navigation, which can open Documents instead of launching the
# program. Use the shell's ShellExecute verb explicitly.
$shell = New-Object -ComObject Shell.Application
$shell.ShellExecute($builtMenuHost, '--resident-host', (Split-Path -Parent $builtMenuHost), 'open', 0)
[void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)

Start-Sleep -Milliseconds 2000

$residentProcess = Get-Process -Name 'Taskbar-GroupMenu' -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending |
    Select-Object -First 1

if (-not $residentProcess) {
    throw "Resident menu host was not found after shell startup."
}

Write-Host "Resident menu host running (PID $($residentProcess.Id)); startup prewarm has been given 2 seconds to complete."
