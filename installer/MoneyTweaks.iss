; Money Tweaks installer (Inno Setup 6). Built by scripts\build-release.ps1.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6E1E5B7A-3F4B-4C1E-9D7A-4D6F0B2A9C11}
AppName=Money Tweaks
AppVersion={#AppVersion}
AppVerName=Money Tweaks {#AppVersion}
AppPublisher=Money Tweaks contributors
AppPublisherURL=https://github.com/MoneyMan82/money-tweaks
AppSupportURL=https://github.com/MoneyMan82/money-tweaks/issues
AppUpdatesURL=https://github.com/MoneyMan82/money-tweaks/releases
DefaultDirName={autopf}\Money Tweaks
DefaultGroupName=Money Tweaks
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=MoneyTweaks-Setup
SetupIconFile=..\src\MoneyTweaks\Assets\MoneyTweaks.ico
UninstallDisplayIcon={app}\MoneyTweaks.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Money Tweaks
VersionInfoDescription=Money Tweaks installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Money Tweaks"; Filename: "{app}\MoneyTweaks.exe"
; Works when the main app can't start: undoes Money Tweaks changes from their backups.
Name: "{group}\Money Tweaks Emergency Rescue"; Filename: "{app}\MoneyTweaks-Rescue.exe"
Name: "{group}\Uninstall Money Tweaks"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Money Tweaks"; Filename: "{app}\MoneyTweaks.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MoneyTweaks.exe"; Parameters: "--restarted"; Description: "{cm:LaunchProgram,Money Tweaks}"; Flags: nowait postinstall skipifsilent
; In-app updates run the installer silently; start the new version afterwards.
Filename: "{app}\MoneyTweaks.exe"; Parameters: "--restarted"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; %LOCALAPPDATA%\MoneyTweaks (logs, and backups of anything that couldn't be undone, e.g. when administrator permission
; was declined) is kept so a reinstall can still undo those. Delete it manually if you no longer need it.
Type: dirifempty; Name: "{app}"

[Code]
// Before any file is removed (usUninstall): undo everything Money Tweaks applied - tweaks from their backups, an active
// gaming profile, CPU/GPU tuning, NVIDIA profile changes, game config files, display modes and its startup entry. The
// app closes a running copy first and asks for administrator rights only if system-wide changes are still applied.
// A silent uninstall runs it without a window. In-app updates don't run the uninstaller, so updating never undoes anything.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Params := '--uninstall-restore';
    if UninstallSilent then
      Params := Params + ' --quiet';
    if not Exec(ExpandConstant('{app}\MoneyTweaks.exe'), Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      Log('Money Tweaks restore could not be started: ' + SysErrorMessage(ResultCode));
  end;
end;
