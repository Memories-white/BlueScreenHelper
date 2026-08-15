; BlueScreenHelper installer script
#define MyAppName "蓝屏诊断助手 BlueScreenHelper"
#define MyAppVersion "1.0.2"
#define MyAppExeName "BlueScreenHelper.exe"
#define MyAppPublisher "Memories-white"
#define MyAppURL "https://github.com/Memories-white/BlueScreenHelper"
#define MyAppAssocName MyAppName + ".dmp"
#define SourceDir "..\publish\win-x64"
#define OutputDir "..\dist"

[Setup]
AppId={{9E2D1C4A-5B7F-4A8E-9C3D-6F8B2A1E4D57}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\BlueScreenHelper
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=BlueScreenHelper_Setup_{#MyAppVersion}_win-x64
OutputDir={#OutputDir}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin
SetupIconFile=..\BlueScreenHelper\Assets\app.ico
UninstallDisplayName={#MyAppName}

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('BlueScreenHelper installed to ' + ExpandConstant('{app}'));
end;