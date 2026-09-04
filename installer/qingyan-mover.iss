#define MyAppName "抖音监控工具"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "qingyan-mover"
#define MyAppExeName "QingyanMover.exe"

[Setup]
AppId={{B3EAF5B4-1B3B-4BAE-9AE2-8F43D3CF0E2C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\QingyanMover
DefaultGroupName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=QingyanMover-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
SetupIconFile=..\qingyan.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
Filename: "{app}\README.txt"; Description: "查看配置说明"; Flags: postinstall shellexec skipifsilent
