; Kurobane installer (Inno Setup 6). Built by `scripts/package.ps1 -Version vX.Y.Z -Installer` from the same staged files
; as the update zip, so the installer and the zip always contain the same build.
; Per-user: no administrator rights. It installs under %LOCALAPPDATA%\Programs\Kurobane, a folder the in-app updater can
; write to (it cannot update a copy under Program Files). Settings and the DPAPI token in %APPDATA%\Kurobane are never
; touched, not even by the uninstaller.
#ifndef AppVersion
  #error Pass /DAppVersion=X.Y.Z /DStageDir=<publish folder> /DOutDir=<output folder> /DSourceRoot=<checkout>
#endif

[Setup]
AppId={{5F2C91D4-7B3E-4A18-9C62-2E8D0F1A6B44}
AppName=Kurobane
AppVersion={#AppVersion}
AppVerName=Kurobane {#AppVersion}
AppPublisher=Kurobane Contributors
AppPublisherURL=https://github.com/BookahCoding/Kurobane
AppSupportURL=https://github.com/BookahCoding/Kurobane/issues
AppUpdatesURL=https://github.com/BookahCoding/Kurobane/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Kurobane
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutDir}
OutputBaseFilename=Kurobane-Setup-{#AppVersion}
SetupIconFile={#SourceRoot}\src\Kurobane.App\Assets\Kurobane.ico
UninstallDisplayIcon={app}\Kurobane.exe
UninstallDisplayName=Kurobane
LicenseFile={#SourceRoot}\LICENSE
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Kurobane"; Filename: "{app}\Kurobane.exe"
Name: "{autodesktop}\Kurobane"; Filename: "{app}\Kurobane.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Kurobane.exe"; Description: "{cm:LaunchProgram,Kurobane}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; In-app updates can add files the installer never listed; remove the whole program folder and the updater's work folder.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Kurobane\updates"
