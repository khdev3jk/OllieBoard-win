; Inno Setup script for OllieBoard. Built by build.ps1 (or the release workflow):
;   iscc /DAppVersion=1.2.0 /DSourceDir=..\build\publish installer\OllieBoard.iss
; Installs per user (no admin prompt) into %LOCALAPPDATA%\Programs\OllieBoard, so the in-app updater can
; replace it by running a newer installer silently.

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\build\publish"
#endif

[Setup]
AppId={{6F3B2C4E-8A1D-4F7B-9C2E-0B11E0A1B0A1}
AppName=OllieBoard
AppVersion={#AppVersion}
AppPublisher=OllieBoard
AppPublisherURL=https://github.com/khdev3jk/OllieBoard-win
DefaultDirName={localappdata}\Programs\OllieBoard
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\build
OutputBaseFilename=OllieBoard-{#AppVersion}-win-x64-setup
SetupIconFile=..\Assets\OllieBoard.ico
UninstallDisplayIcon={app}\OllieBoard.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "startup"; Description: "Start OllieBoard when Windows starts"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\OllieBoard"; Filename: "{app}\OllieBoard.exe"
Name: "{userstartup}\OllieBoard"; Filename: "{app}\OllieBoard.exe"; Tasks: startup

[Run]
; Normal install: offer to start it. Silent install (the in-app updater): start it again.
Filename: "{app}\OllieBoard.exe"; Description: "Launch OllieBoard"; Flags: nowait postinstall skipifsilent
Filename: "{app}\OllieBoard.exe"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im OllieBoard.exe /f"; Flags: runhidden; RunOnceId: "StopApp"
