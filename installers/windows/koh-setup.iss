; Inno Setup script for the Koh toolchain, per-user install.
; CI compiles this with /DAppVersion=<x.y.z> to override the default.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Fixed AppId — all Koh toolchain versions share it so Add/Remove Programs
; shows only one "Koh Toolchain" entry and upgrades in place.
AppId={{A5E2C1D3-7B9E-4F1A-8C3D-B2D9E4F5A6C7}
AppName=Koh Toolchain
AppVersion={#AppVersion}
AppVerName=Koh Toolchain {#AppVersion}
AppPublisher=retro-dev-tools
AppPublisherURL=https://github.com/retro-dev-tools/koh
DefaultDirName={localappdata}\Koh\toolchain\{#AppVersion}
DefaultGroupName=Koh Toolchain
; lowest → no UAC prompt; installs under the user's profile.
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=..\..
OutputBaseFilename=koh-setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\bin\koh-lsp.exe
WizardStyle=modern

[Files]
Source: "payload\*"; DestDir: "{app}\bin"; Flags: recursesubdirs ignoreversion

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  CurrentPath: string;
  Meta: string;
begin
  if CurStep = ssPostInstall then begin
    // Point the canonical "current" pointer at whatever we just installed.
    // Extension reads this to pick which version to use when multiple
    // versions coexist under the per-user toolchain root.
    CurrentPath := ExpandConstant('{localappdata}\Koh\toolchain\current');
    SaveStringToFile(CurrentPath, ExpandConstant('{#AppVersion}'), False);

    // version.json alongside bin/ lets the resolver report the version
    // even if `current` gets out of sync (e.g. user edits by hand).
    Meta := '{"version":"' + ExpandConstant('{#AppVersion}') + '","rid":"win-x64","installedAt":""}';
    SaveStringToFile(ExpandConstant('{app}\version.json'), Meta, False);
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}\bin"
Type: files; Name: "{app}\version.json"
