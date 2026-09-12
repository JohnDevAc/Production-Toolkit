#ifndef AppVersion
  #error AppVersion must be supplied by the build script.
#endif
#ifndef AppIdentity
  #define AppIdentity "JohnLightfoot.ProductionToolkit"
#endif
#ifndef AppTitle
  #define AppTitle "Production Toolkit"
#endif
#ifndef PublishDirectory
  #define PublishDirectory "..\artifacts\publish\win-x64"
#endif
#ifndef ReleaseDirectory
  #define ReleaseDirectory "..\artifacts\release"
#endif
#define AppExe "Production Toolkit.exe"
#ifndef LaunchParameters
  #define LaunchParameters ""
#endif

[Setup]
AppId={#AppIdentity}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppPublisher=John Lightfoot
AppPublisherURL=https://github.com/JohnDevAc/Production-Toolkit
AppSupportURL=https://github.com/JohnDevAc/Production-Toolkit/issues
AppUpdatesURL=https://github.com/JohnDevAc/Production-Toolkit/releases
AppCopyright=Copyright (C) 2026 John Lightfoot. Proprietary. Free for non-commercial use.
DefaultDirName={localappdata}\Programs\{#AppTitle}
DefaultGroupName={#AppTitle}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ReleaseDirectory}
OutputBaseFilename=Production-Toolkit-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\ToolkitLauncher\Assets\toolkit.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppTitle}
Uninstallable=yes
CreateUninstallRegKey=yes
LicenseFile=..\LICENSE.md
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter={#AppExe}
RestartApplications=no
SetupMutex=Local\{#AppIdentity}.Setup
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=John Lightfoot
VersionInfoDescription={#AppTitle} Setup
VersionInfoCopyright=Copyright (C) 2026 John Lightfoot. Proprietary. Free for non-commercial use.

[Files]
Source: "{#PublishDirectory}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\INTEROPERABILITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "maintenance.version"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; Parameters: "{#LaunchParameters}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppTitle}"; Filename: "{app}\{#AppExe}"; Parameters: "{#LaunchParameters}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "{#LaunchParameters}"; Flags: nowait runasoriginaluser; Check: ShouldRestart
Filename: "{app}\{#AppExe}"; Parameters: "{#LaunchParameters}"; Description: "Launch {#AppTitle}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: NotRestarting

[Code]
var
  WasRunning: Boolean;
  Completed: Boolean;

function UpdateMode: Boolean;
begin
  Result := ExpandConstant('{param:TOOLKITUPDATE|0}') = '1';
end;

function ShouldRestart: Boolean;
begin
  Result := UpdateMode or WasRunning;
end;

function NotRestarting: Boolean;
begin
  Result := not ShouldRestart;
end;

function CloseToolkit: Boolean;
var
  ExitCode: Integer;
  Executable: String;
begin
  Result := True;
  Executable := ExpandConstant('{app}\{#AppExe}');
  Log('Toolkit maintenance target: ' + Executable);
  if not FileExists(Executable) then exit;
  // Earlier portable copies do not understand the maintenance command. Let the
  // standard Windows files-in-use handling cover a copy placed here manually.
  if not FileExists(ExpandConstant('{app}\maintenance.version')) then exit;
  if not Exec(Executable, '--shutdown-for-maintenance', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := False;
    exit;
  end;
  Log('Toolkit maintenance exit code: ' + IntToStr(ExitCode));
  // 0: a running instance closed. 2: no instance was running.
  if ExitCode = 0 then WasRunning := True;
  Result := (ExitCode = 0) or (ExitCode = 2);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion: String;
  ExistingPacked, NewPacked: Int64;
begin
  Result := '';
  // Block manual downgrades as well as automatic ones.
  if GetVersionNumbersString(ExpandConstant('{app}\{#AppExe}'), ExistingVersion) then
    if StrToVersion(ExistingVersion, ExistingPacked) and StrToVersion('{#AppVersion}.0', NewPacked) and
      (ComparePackedVersion(ExistingPacked, NewPacked) > 0) then
    begin
      Result := 'A newer version of Production Toolkit is already installed.';
      exit;
    end;
  if not CloseToolkit then
    Result := 'Production Toolkit is busy. Finish or cancel its current download or installation, close it, then retry Setup.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then Completed := True;
end;

procedure DeinitializeSetup;
var
  ExitCode: Integer;
begin
  // If Setup is cancelled or rolls back after closing the app, reopen the surviving version.
  if WasRunning and not Completed and FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exec(ExpandConstant('{app}\{#AppExe}'), '{#LaunchParameters}', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ExitCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if not CloseToolkit then
      RaiseException('Production Toolkit is busy. Close it before uninstalling.');
end;
