$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$Source = Join-Path $Root 'Taskbar-GroupMenu.cs'
$Output = Join-Path $Root 'Taskbar-GroupMenu.exe'

$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $csc) {
    throw 'Built-in .NET Framework C# compiler not found.'
}

& $csc /nologo /target:winexe /optimize+ `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /out:"$Output" "$Source"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Unblock-File -LiteralPath $Output -ErrorAction SilentlyContinue
Write-Host "Built $Output"
