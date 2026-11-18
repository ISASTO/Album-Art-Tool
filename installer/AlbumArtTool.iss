; Muzikette installer (Inno Setup 6). Before 4.0 the app was Music Library Manager; the AppId is unchanged, so
; this setup upgrades that install in place: it moves it to a Muzikette folder and removes the old shortcuts.
; Per-user install: no administrator prompt, Start menu entry, standard uninstaller.
; Build: ISCC.exe /DAppVersion=4.0.0 /DSourceDir=..\dist\app installer\AlbumArtTool.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
; Updaters before 4.0 only accept an installer whose product name is "Music Library Manager". 4.0 keeps that name in
; its version resource so every existing copy can update to it; later versions use Muzikette.
#ifndef VersionProduct
  #define VersionProduct "Music Library Manager"
#endif

[Setup]
AppId={{6C4C3B4E-0F2B-4B5E-9E61-5C2B8D7A3A11}
AppName=Muzikette
AppVersion={#AppVersion}
AppVerName=Muzikette {#AppVersion}
AppPublisher=ISASTO
AppPublisherURL=https://github.com/ISASTO/Muzikette
AppSupportURL=https://github.com/ISASTO/Muzikette/issues
AppUpdatesURL=https://github.com/ISASTO/Muzikette/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#VersionProduct}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=Muzikette setup
VersionInfoCompany=ISASTO
; The folder comes from InstallFolder below: an earlier install's folder, renamed to Muzikette.
DefaultDirName={code:InstallFolder}
UsePreviousAppDir=no
DefaultGroupName=Muzikette
UsePreviousGroup=no
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Muzikette-Setup
SetupIconFile=..\src\AlbumArtTool\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Muzikette.exe
UninstallDisplayName=Muzikette
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
Type: files; Name: "{app}\AlbumArtTool.exe"
; Shortcuts from before 4.0 (Music Library Manager).
Type: files; Name: "{autoprograms}\Music Library Manager\Music Library Manager.lnk"
Type: dirifempty; Name: "{autoprograms}\Music Library Manager"
Type: files; Name: "{autodesktop}\Music Library Manager.lnk"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Muzikette"; Filename: "{app}\Muzikette.exe"
Name: "{autodesktop}\Muzikette"; Filename: "{app}\Muzikette.exe"; Tasks: desktopicon

[Run]
; Interactive install: optional launch on the last page.
Filename: "{app}\Muzikette.exe"; Description: "{cm:LaunchProgram,Muzikette}"; Flags: nowait postinstall skipifsilent
; In-app update (silent): reopen in the same music folder.
Filename: "{app}\Muzikette.exe"; Parameters: "{code:MusicFolderArgument}"; Flags: nowait; Check: RelaunchAfterUpdate

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  // Muzikette, and Music Library Manager before 4.0 (whose updater starts this setup).
  RunningMutex = 'Muzikette.Running,AlbumArtTool.Running';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6C4C3B4E-0F2B-4B5E-9E61-5C2B8D7A3A11}_is1';

var
  PreviousFolder: String;

// The folder of an earlier install of this app (same AppId), or '' on a first install.
function FindPreviousFolder(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', Result) then
    if not RegQueryStringValue(HKLM, UninstallKey, 'Inno Setup: App Path', Result) then
      Result := '';
end;

// Updates stay where the app is installed. An install in a folder named Music Library Manager (the default before
// 4.0) moves next door to a folder named Muzikette; the old folder is removed after the new files are in place.
function InstallFolder(Param: String): String;
begin
  PreviousFolder := FindPreviousFolder();
  if PreviousFolder = '' then
    Result := ExpandConstant('{autopf}\Muzikette')
  else if CompareText(ExtractFileName(RemoveBackslashUnlessRoot(PreviousFolder)), 'Music Library Manager') = 0 then
    Result := AddBackslash(ExtractFileDir(RemoveBackslashUnlessRoot(PreviousFolder))) + 'Muzikette'
  else
    Result := PreviousFolder;
end;

// Removes the pre-4.0 install folder, but only when it is clearly that app's folder.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Old: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if PreviousFolder = '' then Exit;
  Old := RemoveBackslashUnlessRoot(PreviousFolder);
  if CompareText(Old, RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0 then Exit;
  if CompareText(ExtractFileName(Old), 'Music Library Manager') <> 0 then Exit;
  if not FileExists(AddBackslash(Old) + 'AlbumArtTool.exe') then Exit;
  DelTree(Old, True, True, True);
end;

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
    if MsgBox('Muzikette is still running. Close it, then click OK to continue.', mbConfirmation, MB_OKCANCEL) <> IDOK then
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
