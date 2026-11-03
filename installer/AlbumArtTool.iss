; Music Library Manager installer (Inno Setup 6).
; Per-user install: no administrator prompt, Start menu entry, standard uninstaller.
; Build: ISCC.exe /DAppVersion=3.0.0 /DSourceDir=..\dist\app installer\AlbumArtTool.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6C4C3B4E-0F2B-4B5E-9E61-5C2B8D7A3A11}
AppName=Music Library Manager
AppVersion={#AppVersion}
AppVerName=Music Library Manager {#AppVersion}
AppPublisher=ISASTO
AppPublisherURL=https://github.com/ISASTO/Album-Art-Tool
AppSupportURL=https://github.com/ISASTO/Album-Art-Tool/issues
AppUpdatesURL=https://github.com/ISASTO/Album-Art-Tool/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Music Library Manager
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Music Library Manager setup
VersionInfoCompany=ISASTO
DefaultDirName={autopf}\Music Library Manager
DefaultGroupName=Music Library Manager
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=AlbumArtTool-Setup
SetupIconFile=..\src\AlbumArtTool\Assets\AppIcon.ico
UninstallDisplayIcon={app}\AlbumArtTool.exe
UninstallDisplayName=Music Library Manager
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The app is closed by its own updater before setup starts; setup waits for it below.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Remove files from earlier versions that this version no longer ships.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.pdb"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Music Library Manager"; Filename: "{app}\AlbumArtTool.exe"
Name: "{autodesktop}\Music Library Manager"; Filename: "{app}\AlbumArtTool.exe"; Tasks: desktopicon

[Run]
; Interactive install: optional launch on the last page.
Filename: "{app}\AlbumArtTool.exe"; Description: "{cm:LaunchProgram,Music Library Manager}"; Flags: nowait postinstall skipifsilent
; In-app update (silent): reopen in the same music folder.
Filename: "{app}\AlbumArtTool.exe"; Parameters: "{code:MusicFolderArgument}"; Flags: nowait; Check: RelaunchAfterUpdate

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  RunningMutex = 'AlbumArtTool.Running';

// Wait for a running copy (for example the app that started this update) to exit
// before any installed file is replaced.
function InitializeSetup(): Boolean;
var
  Waited: Integer;
begin
  Result := True;
  Waited := 0;
  while CheckForMutexes(RunningMutex) and (Waited < 120000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  while CheckForMutexes(RunningMutex) do
  begin
    if WizardSilent() then
    begin
      Result := False;
      Exit;
    end;
    if MsgBox('Music Library Manager is still running. Close it, then click OK to continue.', mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function RelaunchAfterUpdate(): Boolean;
begin
  Result := WizardSilent() and ((ExpandConstant('{param:MUSICFOLDER|}') <> '') or (ExpandConstant('{param:LAUNCH|}') = '1'));
end;

// Quote for the Windows command line. A trailing backslash (for example a drive root
// such as G:\) is doubled so it does not escape the closing quote.
function MusicFolderArgument(Param: String): String;
var
  Folder: String;
begin
  Folder := ExpandConstant('{param:MUSICFOLDER|}');
  if Folder = '' then
  begin
    Result := '';
    Exit;
  end;
  if Copy(Folder, Length(Folder), 1) = '\' then
    Folder := Folder + '\';
  Result := '"' + Folder + '"';
end;
