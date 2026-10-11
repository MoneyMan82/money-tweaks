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
PrivilegesRequiredOverridesAllowed=dialog
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
Name: "{group}\Uninstall Money Tweaks"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Money Tweaks"; Filename: "{app}\MoneyTweaks.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MoneyTweaks.exe"; Description: "{cm:LaunchProgram,Money Tweaks}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Tweaks stay applied after uninstall; backups in %LOCALAPPDATA%\MoneyTweaks are kept on purpose so you can
; reinstall and undo them later. Delete that folder manually if you no longer need it.
Type: dirifempty; Name: "{app}"
