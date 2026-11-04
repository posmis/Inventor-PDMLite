; Compiled by build-release.ps1. No personal paths or library data are committed.
#ifndef PayloadDir
  #error PayloadDir must be specified
#endif
#ifndef OutputDir
  #error OutputDir must be specified
#endif
#ifndef Variant
  #define Variant "Online"
#endif
#define AppVersion "0.1"

[Setup]
AppId={{A81E7CC7-814F-45B0-A481-3DB0321DDC03}
AppName=PdmLite
AppVersion={#AppVersion}
VersionInfoVersion=0.1.0.0
DefaultDirName={localappdata}\PdmLite
DisableDirPage=yes
DefaultGroupName=PdmLite
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=PdmLite-Setup-0.1-{#Variant}
SetupIconFile=..\..\src\PdmLite.Explorer\Assets\PdmLite.ico
UninstallDisplayIcon={app}\Explorer\PdmLite.Explorer.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
DiskSpanning=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PayloadDir}\Explorer\*"; DestDir: "{app}\Explorer"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\AddIn\*"; DestDir: "{app}\AddIn"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\Setup\*"; DestDir: "{app}\Setup"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "postgresql.exe"
#if Variant == "Offline"
Source: "{#PayloadDir}\Setup\postgresql.exe"; DestDir: "{app}\Setup"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\PdmLite Explorer"; Filename: "{app}\Explorer\PdmLite.Explorer.exe"
Name: "{autodesktop}\PdmLite Explorer"; Filename: "{app}\Explorer\PdmLite.Explorer.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked

[Run]
Filename: "{app}\Explorer\PdmLite.Explorer.exe"; Description: "Запустить PdmLite Explorer"; Flags: nowait postinstall skipifsilent; Check: SetupSucceeded

[UninstallDelete]
Type: files; Name: "{userappdata}\Autodesk\Inventor 2025\Addins\PdmLite.addin"
; Library files, backups, settings and the PostgreSQL service deliberately remain.

[Code]
var
  LibraryPage: TInputDirWizardPage;
  Configured: Boolean;
  Completed: Boolean;

function SetupSucceeded: Boolean;
begin
  Result := Completed;
end;

procedure InitializeWizard;
begin
  Configured := FileExists(ExpandConstant('{userappdata}\PdmLite\database.json'));
  LibraryPage := CreateInputDirPage(wpSelectDir, 'Библиотека компонентов',
    'Где хранить файлы библиотеки?',
    'При первой установке сюда будет восстановлена библиотека из резервной копии. Выберите пустую папку.',
    False, '');
  LibraryPage.Add('Папка библиотеки:');
  LibraryPage.Values[0] := ExpandConstant('{userdocs}\PdmLite\Library');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := Configured and (PageID = LibraryPage.ID);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  if (CurPageID = LibraryPage.ID) and not Configured and
     not FileExists(ExpandConstant('{userappdata}\PdmLite\setup-pending.json')) then begin
    if DirExists(LibraryPage.Values[0]) and FindFirst(AddBackslash(LibraryPage.Values[0]) + '*', FindRec) then begin
      try
        repeat
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
            MsgBox('Папка не пустая. Выберите пустую папку, чтобы сохранить существующие файлы.', mbError, MB_OK);
            Result := False;
            Break;
          end;
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Ps, Cmd: String;
begin
  Result := '';
  Ps := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Cmd := '-NoProfile -Command "if (Get-Process -Name Inventor,PdmLite.Explorer -ErrorAction SilentlyContinue) { exit 1 }"';
  if not Exec(Ps, Cmd, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Result := 'Закройте Inventor и PdmLite Explorer, затем повторите установку.';
  if not FileExists(ExpandConstant('{pf64}\Autodesk\Inventor 2025\Bin\Inventor.exe')) and
     not RegKeyExists(HKLM64, 'SOFTWARE\Autodesk\Inventor\RegistryVersion29.0') then
    Result := 'Для PdmLite требуется установленный Autodesk Inventor 2025.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then begin
    WizardForm.StatusLabel.Caption := 'Настройка PostgreSQL и библиотеки...';
    if not Exec(ExpandConstant('{app}\Setup\PdmLite.Setup.exe'),
      AddQuotes(ExpandConstant('{app}')) + ' ' + AddQuotes(LibraryPage.Values[0]),
      '', SW_SHOW, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('Настройка PdmLite не завершена. Данные сохранены. Запустите установщик повторно после устранения причины.');
    Completed := True;
  end;
end;
