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
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Files]
Source: "..\Taskbar-GroupMenu.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\Icon Library\*"; DestDir: "{app}\Icon Library"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\_templates\*"; DestDir: "{app}\_templates"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
; User data is intentionally not deleted. It lives under the user's
; %LOCALAPPDATA%\Taskbar-Groups directory and is managed by the application.
