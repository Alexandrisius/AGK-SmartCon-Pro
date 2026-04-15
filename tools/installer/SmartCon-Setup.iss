#define AppName "SmartCon"
#define AppPublisher "AGK Engineering"
#define AppURL "https://github.com/AGK-Engineering/AGK-SmartCon-Pro"

#ifndef AppVersion
  #define AppVersion = Trim(FileRead(FileOpen("..\..\Version.txt"), 0)));
#endif

[Setup]
AppId={{B8E2F1A3-4D5C-6E7F-8A9B-0C1D2E3F4A5B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
DefaultDirName={userappdata}\SmartCon
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\SmartCon.Updater.exe
OutputDir=..\..\artifacts
OutputBaseFilename=SmartCon-{#AppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.RevitVersionsTitle=Select Revit Versions
russian.RevitVersionsSubtitle=Select which Revit versions to install SmartCon for
russian.NoRevitFound=No supported Revit versions found.%n%nThe installer can still proceed, but you will need to manually configure the plugin later.
russian.InstallFor=Install for Revit %1
russian.NotInstalled=(not installed)
english.RevitVersionsTitle=Select Revit Versions
english.RevitVersionsSubtitle=Select which Revit versions to install SmartCon for
english.NoRevitFound=No supported Revit versions were found on your system.%n%nThe installer can still proceed, but you will need to manually configure the plugin later.
english.InstallFor=Install for Revit %1
english.NotInstalled=(not installed)

[Types]
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "core"; Description: "SmartCon Plugin Files"; Types: custom; Flags: fixed
Name: "revit2021"; Description: "Revit 2021-2023"; Types: custom; Flags: checkablealone
Name: "revit2024"; Description: "Revit 2024"; Types: custom; Flags: checkablealone
Name: "revit2025"; Description: "Revit 2025"; Types: custom; Flags: checkablealone

[Files]
; --- DLL set 1: Revit 2021-2023 (net48, RevitAPI 2021) ---
Source: "..\..\artifacts\publish\SmartCon-R21\SmartCon.App.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R21\SmartCon.Core.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R21\SmartCon.Revit.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R21\SmartCon.UI.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R21\SmartCon.PipeConnect.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R21\CommunityToolkit.*.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R21\Microsoft.Extensions.*.dll"; DestDir: "{app}\2021-2023"; Components: revit2021; Flags: ignoreversion

; --- DLL set 2: Revit 2024 (net48, RevitAPI 2024) ---
Source: "..\..\artifacts\publish\SmartCon-R24\SmartCon.App.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R24\SmartCon.Core.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R24\SmartCon.Revit.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R24\SmartCon.UI.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R24\SmartCon.PipeConnect.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R24\CommunityToolkit.*.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R24\Microsoft.Extensions.*.dll"; DestDir: "{app}\2024"; Components: revit2024; Flags: ignoreversion

; --- DLL set 3: Revit 2025 (net8.0-windows, RevitAPI 2025) ---
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.App.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Core.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Revit.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.UI.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.PipeConnect.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\CommunityToolkit.*.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\Microsoft.Extensions.*.dll"; DestDir: "{app}\2025"; Components: revit2025; Flags: ignoreversion

; --- Updater (shared, net8.0) — always installed ---
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.exe"; DestDir: "{app}"; Components: core; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.dll"; DestDir: "{app}"; Components: core; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.deps.json"; DestDir: "{app}"; Components: core; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.runtimeconfig.json"; DestDir: "{app}"; Components: core; Flags: ignoreversion skipifsourcedoesntexist

[Registry]
; Revit 2021-2023 share one DLL set, create .addin for each
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2021-2023"; ValueData: "{app}\2021-2023"; Components: revit2021; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2024"; ValueData: "{app}\2024"; Components: revit2024; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2025"; ValueData: "{app}\2025"; Components: revit2025; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletevalue

[Code]
var
  Revit2021Installed: Boolean;
  Revit2022Installed: Boolean;
  Revit2023Installed: Boolean;
  Revit2024Installed: Boolean;
  Revit2025Installed: Boolean;

function IsRevitInstalled(Version: Integer): Boolean;
var
  Key, InstallPath: String;
begin
  Key := 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + IntToStr(Version);
  Result := RegQueryStringValue(HKLM, Key, 'InstallLocation', InstallPath) and (InstallPath <> '') and DirExists(InstallPath);
end;

procedure DetectRevitVersions;
begin
  Revit2021Installed := IsRevitInstalled(2021);
  Revit2022Installed := IsRevitInstalled(2022);
  Revit2023Installed := IsRevitInstalled(2023);
  Revit2024Installed := IsRevitInstalled(2024);
  Revit2025Installed := IsRevitInstalled(2025);
end;

function ShouldInstallRevit2021: Boolean;
begin
  Result := Revit2021Installed or Revit2022Installed or Revit2023Installed;
end;

function InitializeSetup: Boolean;
begin
  DetectRevitVersions;
  Result := True;
end;

procedure InitializeWizard;
begin
  if not ShouldInstallRevit2021 and not Revit2024Installed and not Revit2025Installed then
    MsgBox(CustomMessage('NoRevitFound'), mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
  procedure WriteAddinFile(const RevitVersion, DllSubDir: String);
  var
    AddinContent, AddinPath, AppDataDir: String;
  begin
    AppDataDir := ExpandConstant('{userappdata}');
    AddinPath := AppDataDir + '\Autodesk\Revit\Addins\' + RevitVersion + '\SmartCon.addin';
    ForceDirectories(ExtractFilePath(AddinPath));
    AddinContent :=
      '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
      '<RevitAddIns>' + #13#10 +
      '  <AddIn Type="Application">' + #13#10 +
      '    <Name>SmartCon</Name>' + #13#10 +
      '    <Assembly>' + AppDataDir + '\SmartCon\' + DllSubDir + '\SmartCon.App.dll</Assembly>' + #13#10 +
      '    <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>' + #13#10 +
      '    <FullClassName>SmartCon.App.App</FullClassName>' + #13#10 +
      '    <VendorId>AGK</VendorId>' + #13#10 +
      '    <VendorDescription>AGK Engineering</VendorDescription>' + #13#10 +
      '  </AddIn>' + #13#10 +
      '</RevitAddIns>';
    SaveStringToFile(AddinPath, AddinContent, False);
  end;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsComponentSelected('revit2021') then
    begin
      if Revit2021Installed then WriteAddinFile('2021', '2021-2023');
      if Revit2022Installed then WriteAddinFile('2022', '2021-2023');
      if Revit2023Installed then WriteAddinFile('2023', '2021-2023');
    end;
    if WizardIsComponentSelected('revit2024') then
    begin
      if Revit2024Installed then WriteAddinFile('2024', '2024');
    end;
    if WizardIsComponentSelected('revit2025') then
    begin
      if Revit2025Installed then WriteAddinFile('2025', '2025');
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
  procedure RemoveAddinAndDlls(const RevitVersion, DllSubDir: String);
  var
    AppDataDir: String;
  begin
    AppDataDir := ExpandConstant('{userappdata}');
    DeleteFile(AppDataDir + '\Autodesk\Revit\Addins\' + RevitVersion + '\SmartCon.addin');
    DelTree(AppDataDir + '\SmartCon\' + DllSubDir, True, True, True);
  end;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveAddinAndDlls('2021', '2021-2023');
    RemoveAddinAndDlls('2022', '2021-2023');
    RemoveAddinAndDlls('2023', '2021-2023');
    RemoveAddinAndDlls('2024', '2024');
    RemoveAddinAndDlls('2025', '2025');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.exe');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.dll');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.deps.json');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.runtimeconfig.json');
    DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}\2021-2023"
Type: filesandordirs; Name: "{app}\2024"
Type: filesandordirs; Name: "{app}\2025"
Type: files; Name: "{app}\SmartCon.Updater.exe"
Type: files; Name: "{app}\SmartCon.Updater.dll"
Type: filesandordirs; Name: "{app}"
