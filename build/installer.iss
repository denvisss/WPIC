; Установщик «один файл»: приложение + офлайн-набор драйверов + офлайн-winget.
#define AppName "Windows Post-Install Client"
#define AppVersion "0.3.0"
#define AppPublisher "denvisss"
#define AppExeName "WindowsPostInstallClient.exe"
#define PublishDir SourcePath + "publish"
#define PayloadDir SourcePath + "payload"

[Setup]
AppId={{4F87FF46-EEDD-40FF-A484-A08B6AEE0C52}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=WPIC_{#AppVersion}-full
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64os
ArchitecturesAllowed=x64os
MinVersion=10.0.17763
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\src\WPIC\appicon.ico
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs
Source: "{#PayloadDir}\DriverPack\7za64.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\DriverPack\*"; DestDir: "{app}\DriverPack"; Flags: ignoreversion recursesubdirs
; winget-пак не кладём рядом с exe: он встроен в ресурсы exe и распаковывается
; в %LOCALAPPDATA%\WPIC\winget при первом обращении (как в portable) — без дубликата ~69 МиБ.

[InstallDelete]
; старое имя сборки до переименования приложения
Type: files; Name: "{app}\WindowsInstallClient.exe"
; loose-копия winget-пака из установщиков 0.2.0 и раньше
Type: filesandordirs; Name: "{app}\winget"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
