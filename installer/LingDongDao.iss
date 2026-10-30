#define AppVersion "0.4.0"
#define AppName "灵动岛"

[Setup]
AppId={{CB677B45-75CB-43D6-AABE-56629C823A56}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Korloa
AppPublisherURL=https://github.com/Korloa/Lingdongdao-For-Windows
AppSupportURL=https://github.com/Korloa/Lingdongdao-For-Windows/issues
DefaultDirName={localappdata}\Programs\LingDongDao
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=LingDongDao-Setup-v{#AppVersion}-win-x64
SetupIconFile=..\assets\LingDongDao.ico
UninstallDisplayIcon={app}\LingDongDao.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "..\dist\LingDongDao.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: isreadme

[Icons]
Name: "{group}\灵动岛"; Filename: "{app}\LingDongDao.exe"
Name: "{group}\卸载灵动岛"; Filename: "{uninstallexe}"
Name: "{autodesktop}\灵动岛"; Filename: "{app}\LingDongDao.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LingDongDao.exe"; Description: "启动灵动岛"; Flags: nowait postinstall skipifsilent
