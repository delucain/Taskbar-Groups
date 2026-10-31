$VariableName = 'TASKBAR_GROUPS_PERF'
$CurrentValue = [Environment]::GetEnvironmentVariable($VariableName, 'User')

if ($CurrentValue -eq '1') {
    [Environment]::SetEnvironmentVariable($VariableName, $null, 'User')
    $NewState = 'DISABLED'
}
else {
    [Environment]::SetEnvironmentVariable($VariableName, '1', 'User')
    $NewState = 'ENABLED'
}

# Notify Windows that the environment has changed.
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class EnvironmentBroadcast
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}
'@

$HWND_BROADCAST   = [IntPtr]0xffff
$WM_SETTINGCHANGE = 0x001A
$SMTO_ABORTIFHUNG = 0x0002
$result = [UIntPtr]::Zero

[void][EnvironmentBroadcast]::SendMessageTimeout(
    $HWND_BROADCAST,
    $WM_SETTINGCHANGE,
    [UIntPtr]::Zero,
    'Environment',
    $SMTO_ABORTIFHUNG,
    5000,
    [ref]$result
)

# The resident host inherited the old environment when it started.
# Restart it so the new logging setting takes effect immediately.
Get-Process -Name 'Taskbar-GroupMenu' -ErrorAction SilentlyContinue |
    Stop-Process -Force

Write-Host "Taskbar Groups performance logging: $NewState"