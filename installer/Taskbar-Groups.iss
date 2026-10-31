#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\_release"
#endif

#define MyAppName "Taskbar Groups"
#define MyAppPublisher "Taskbar Groups"
#define MyAppExeName "Taskbar-GroupMenu.exe"

[Setup]
AppId={{C8F7C927-5D30-48D6-A872-9A8D40D7E57B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Taskbar Groups
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Taskbar-Groups-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\assets\TaskbarGroups.ico
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Files]
Source: "..\Taskbar-GroupMenu.exe"; DestDir: "{app}"; Flags: ignoreversion
; The Icon Library is seeded by Setup but becomes user-owned mutable content.
; Never overwrite an existing icon during upgrades and never remove seeded or
; user-added icons during uninstall.
Source: "..\Icon Library\*"; DestDir: "{app}\Icon Library"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist uninsneveruninstall
Source: "..\_templates\*"; DestDir: "{app}\_templates"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\TaskbarGroups.ico"; DestDir: "{app}\assets"; Flags: ignoreversion


[Registry]
; Start one resident menu host per interactive session at logon. The host warms
; all group/menu/icon caches before the user clicks a taskbar group.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TaskbarGroupsMenuHost"; ValueData: """{app}\{#MyAppExeName}"" --resident-host"; Flags: uninsdeletevalue

; Explorer integration for selected files and directories.
; Use the installed executable rather than the resident host directly; this
; invocation opens a standalone Add Link dialog with no group preselected.
Root: HKCR; Subkey: "*\shell\TaskbarGroupsPin"; ValueType: string; ValueData: "Pin to Taskbar Group"; Flags: uninsdeletekey
Root: HKCR; Subkey: "*\shell\TaskbarGroupsPin"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\assets\TaskbarGroups.ico"
Root: HKCR; Subkey: "*\shell\TaskbarGroupsPin\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --pin-to-group ""%1"""

Root: HKCR; Subkey: "Directory\shell\TaskbarGroupsPin"; ValueType: string; ValueData: "Pin to Taskbar Group"; Flags: uninsdeletekey
Root: HKCR; Subkey: "Directory\shell\TaskbarGroupsPin"; ValueName: "Icon"; ValueType: string; ValueData: "{app}\assets\TaskbarGroups.ico"
Root: HKCR; Subkey: "Directory\shell\TaskbarGroupsPin\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --pin-to-group ""%1"""

[UninstallDelete]
; User data is intentionally not deleted. It lives under the user's
; %LOCALAPPDATA%\Taskbar-Groups directory and is managed by the application.
