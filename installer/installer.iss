; Inno Setup script for CC Pad (built by .github/workflows/release.yml).
; Version is passed in by CI:  ISCC.exe /DMyAppVersion=1.0.4 installer\installer.iss
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "CC Pad"
#define MyAppPublisher "vv-l"
#define MyAppURL "https://github.com/vv-l/CCPad"
#define MyAppExeName "CCPad.exe"
; Anchor paths to this script's own directory (installer/) so they resolve
; regardless of the working directory ISCC is invoked from.
#define MyPublishDir SourcePath + "..\publish"

[Setup]
AppId={{8F2A1C7E-3B6D-4E9A-9C21-5D7F0A1B2C3D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\CC Pad
DefaultGroupName=CC Pad
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#SourcePath}dist
OutputBaseFilename=CCPad-Setup-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user install: no UAC prompt, friendliest for end users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Lets the auto-updater's `/SILENT /CLOSEAPPLICATIONS` close a running CC Pad.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
