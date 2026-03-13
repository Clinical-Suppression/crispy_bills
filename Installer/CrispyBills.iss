; Crispy Bills - Inno Setup Installer Script
; Requires Inno Setup 6+ from https://jrsoftware.org/isinfo.php
; Build the app first by running build-installer.ps1 (or run step manually below).

#define MyAppName "Crispy Bills"
#define MyAppVersion "1.0"
#define MyAppPublisher "Chris"
#define MyAppExeName "CrispyBills.exe"
#define MyAppSourceDir "..\publish\win-x64"

[Setup]
AppId={{A3F1C2D4-9B7E-4E2A-BD18-0F6C3A2D5E71}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.\Output
OutputBaseFilename=CrispyBills_Setup
SetupIconFile=..\Crispy_Pig.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; The self-contained single-file exe includes the .NET 8 runtime - no separate install needed.
Source: "{#MyAppSourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";    DestName: "{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
