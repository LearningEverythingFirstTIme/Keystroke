; Keystroke Installer Script for Inno Setup
; Builds a per-user installer (no admin required)

#define MyAppName "Keystroke"
#define MyAppVersion "0.1.2"
#define MyAppPublisher "Nick Kessler"
#define MyAppExeName "KeystrokeApp.exe"
#define MyAppURL "https://keystroke-app.com"

; Path to the single-file publish output (relative to this .iss file)
#define PublishDir "..\src\KeystrokeApp\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{B8F3A2E1-7C4D-4E5F-9A1B-2D3C4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=KeystrokeSetup
Compression=lzma2/ultra64
SolidCompression=yes
; Per-user install, no UAC prompt
PrivilegesRequired=lowest
; 64-bit only
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Uninstall info
UninstallDisplayName={#MyAppName}
; Use the app exe as the installer icon (comment out and set SetupIconFile if you add a .ico later)
; SetupIconFile=..\assets\keystroke.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start Keystroke automatically when I log in"; GroupDescription: "Startup:"

[Files]
; Main executable (single-file publish)
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Native WPF dependencies (cannot be bundled into single-file)
Source: "{#PublishDir}\D3DCompiler_47_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\PenImc_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\PresentationNative_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\vcruntime140_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\wpfgfx_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion

; All remaining files and subdirectories (locale satellite assemblies, etc.)
; Excludes the main exe (already listed above), native DLLs, and debug symbols
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Excludes: "{#MyAppExeName},*.pdb,D3DCompiler_47_cor3.dll,PenImc_cor3.dll,PresentationNative_cor3.dll,vcruntime140_cor3.dll,wpfgfx_cor3.dll"
; NOTE: Don't include the .pdb in the installer (debug symbols not needed for end users)

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Auto-start on login (per-user, only if task selected)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Offer to launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up any config/data files the app creates in its install directory
Type: filesandordirs; Name: "{app}"
