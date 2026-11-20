; Inno Setup 6 script: per-user install, no admin rights needed.
; Build with:  ISCC.exe installer\AllianceStatus.iss   (after .\publish.ps1 -Mode standalone)

#define AppName "Alliance Status"
#define AppVersion "1.0.0"
#define AppExe "AllianceStatus.exe"

[Setup]
AppId={{B7E3C2A4-5D19-4F6B-9C8E-2A7F1D3E4B50}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Ferdinand Koenig and Claude (Anthropic)
AppComments=Written by Claude (Anthropic), initiated and co-developed by Ferdinand Koenig.
DefaultDirName={localappdata}\Programs\AllianceStatus
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=AllianceStatus-Setup
SetupIconFile=..\src\AllianceStatus\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start {#AppName} when I sign in to Windows"

[Files]
Source: "..\dist\win-x64-standalone\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "AllianceStatus"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName} now"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{app}\{#AppExe}"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "Cleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\AllianceStatus"
