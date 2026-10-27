$ErrorActionPreference='Stop'
$Root='C:\code\_Scripts\Taskbar-Groups'
$Source=Join-Path $Root 'Taskbar-GroupMenu.cs'
$Output=Join-Path $Root 'Taskbar-GroupMenu.exe'
$csc=@("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe","$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe")|?{Test-Path $_}|select -First 1
if(-not $csc){throw 'Built-in .NET Framework C# compiler not found.'}
& $csc /nologo /target:winexe /optimize+ /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /out:"$Output" "$Source"
if($LASTEXITCODE -ne 0){throw 'Compilation failed.'}
Write-Host "Built $Output"
