; BlueScreenHelper installer script
#define MyAppName "蓝屏诊断助手 BlueScreenHelper"
#define MyAppShortName "蓝屏诊断助手"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.7"
#endif
#ifndef MyAppNumericVersion
  #define MyAppNumericVersion "1.0.7.0"
#endif
#define MyAppExeName "BlueScreenHelper.exe"
#define MyAppPublisher "Memories-white"
#define MyAppURL "https://github.com/Memories-white/BlueScreenHelper"
#define SourceDir "..\publish\win-x64"
#define OutputDir "..\dist"

#if !FileExists(SourceDir + "\" + MyAppExeName)
  #error "Publish output is missing BlueScreenHelper.exe. Run scripts\Build-Installer.ps1 first."
#endif

[Setup]
AppId={{9E2D1C4A-5B7F-4A8E-9C3D-6F8B2A1E4D57}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppContact={#MyAppURL}/issues
AppComments=Windows 蓝屏转储分析与系统健康诊断工具
AppCopyright=Copyright (C) Memories-white
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
MinVersion=10.0.17763
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\BlueScreenHelper\Assets\app.ico
UninstallDisplayName={#MyAppName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppNumericVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoOriginalFileName=BlueScreenHelper_Setup.exe

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Dirs]
Name: "{app}\log"; Permissions: users-modify

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('BlueScreenHelper installed to ' + ExpandConstant('{app}'));
end;
