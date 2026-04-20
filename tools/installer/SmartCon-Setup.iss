#define AppName "SmartCon"
#define AppPublisher "AGK Engineering"
#define AppURL "https://github.com/Alexandrisius/AGK-SmartCon-Pro"

#ifndef AppVersion
  #define AppVersion Trim(FileRead(FileOpen("..\..\Version.txt"), 0));
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
russian.NoRevitFound=На этой системе не найдены поддерживаемые версии Revit (2019-2026).%n%nУстановка будет продолжена, но плагин не будет зарегистрирован в Revit автоматически.
english.NoRevitFound=No supported Revit versions (2019-2026) were found on this system.%n%nInstallation will proceed, but the plugin will not be automatically registered in Revit.

[Files]
; --- DLL set 0: Revit 2019-2020 (net48, RevitAPI 2020) ---
Source: "..\..\artifacts\publish\SmartCon-R19\*"; DestDir: "{app}\2019-2020"; Check: NeedR19; Flags: ignoreversion recursesubdirs; Excludes: "RevitAPI*.dll,AdWindows*.dll,UIAutomation*.dll"

; --- DLL set 1: Revit 2021-2023 (net48, RevitAPI 2021) ---
Source: "..\..\artifacts\publish\SmartCon-R21\*"; DestDir: "{app}\2021-2023"; Check: NeedR21; Flags: ignoreversion recursesubdirs; Excludes: "RevitAPI*.dll,AdWindows*.dll,UIAutomation*.dll"

; --- DLL set 2: Revit 2024 (net48, RevitAPI 2024) ---
Source: "..\..\artifacts\publish\SmartCon-R24\*"; DestDir: "{app}\2024"; Check: NeedR24; Flags: ignoreversion recursesubdirs; Excludes: "RevitAPI*.dll,AdWindows*.dll,UIAutomation*.dll"

; --- DLL set 3: Revit 2025 (net8.0-windows, RevitAPI 2025) ---
Source: "..\..\artifacts\publish\SmartCon-R25\*"; DestDir: "{app}\2025"; Check: NeedR25; Flags: ignoreversion recursesubdirs; Excludes: "RevitAPI*.dll,AdWindows*.dll,UIAutomation*.dll"

; --- DLL set 4: Revit 2026 (net8.0-windows, RevitAPI 2026) ---
Source: "..\..\artifacts\publish\SmartCon-R26\*"; DestDir: "{app}\2026"; Check: NeedR26; Flags: ignoreversion recursesubdirs; Excludes: "RevitAPI*.dll,AdWindows*.dll,UIAutomation*.dll"

; --- Updater (shared, net8.0) — always installed ---
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.deps.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\artifacts\publish\SmartCon-R25\SmartCon.Updater.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Registry]
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2019-2020"; ValueData: "{app}\2019-2020"; Check: NeedR19; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2021-2023"; ValueData: "{app}\2021-2023"; Check: NeedR21; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2024"; ValueData: "{app}\2024"; Check: NeedR24; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2025"; ValueData: "{app}\2025"; Check: NeedR25; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon\Installations"; ValueType: string; ValueName: "2026"; ValueData: "{app}\2026"; Check: NeedR26; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\SmartCon"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletevalue

[Code]
var
  Revit2019Installed: Boolean;
  Revit2020Installed: Boolean;
  Revit2021Installed: Boolean;
  Revit2022Installed: Boolean;
  Revit2023Installed: Boolean;
  Revit2024Installed: Boolean;
  Revit2025Installed: Boolean;
  Revit2026Installed: Boolean;

function IsRevitInstalled(Version: Integer): Boolean;
var
  Key, InstallPath: String;
begin
  { Primary: REVIT-05:0419\InstallationLocation (real path on this system) }
  Key := 'SOFTWARE\Autodesk\Revit\' + IntToStr(Version) + '\REVIT-05:0419';
  if RegQueryStringValue(HKLM64, Key, 'InstallationLocation', InstallPath) then
  begin
    if (InstallPath <> '') and DirExists(InstallPath) then
    begin
      Result := True;
      Exit;
    end;
  end;
  if RegQueryStringValue(HKLM32, Key, 'InstallationLocation', InstallPath) then
  begin
    if (InstallPath <> '') and DirExists(InstallPath) then
    begin
      Result := True;
      Exit;
    end;
  end;
  { Fallback: standard installation path }
  InstallPath := 'C:\Program Files\Autodesk\Revit ' + IntToStr(Version) + '\';
  if DirExists(InstallPath) then
  begin
    Result := True;
    Exit;
  end;
  Result := False;
end;

procedure DetectRevitVersions;
begin
  Revit2019Installed := IsRevitInstalled(2019);
  Revit2020Installed := IsRevitInstalled(2020);
  Revit2021Installed := IsRevitInstalled(2021);
  Revit2022Installed := IsRevitInstalled(2022);
  Revit2023Installed := IsRevitInstalled(2023);
  Revit2024Installed := IsRevitInstalled(2024);
  Revit2025Installed := IsRevitInstalled(2025);
  Revit2026Installed := IsRevitInstalled(2026);
end;

function NeedR19: Boolean;
begin
  Result := Revit2019Installed or Revit2020Installed;
end;

function NeedR21: Boolean;
begin
  Result := Revit2021Installed or Revit2022Installed or Revit2023Installed;
end;

function NeedR24: Boolean;
begin
  Result := Revit2024Installed;
end;

function NeedR25: Boolean;
begin
  Result := Revit2025Installed;
end;

function NeedR26: Boolean;
begin
  Result := Revit2026Installed;
end;

function InitializeSetup: Boolean;
begin
  DetectRevitVersions;
  if not NeedR19 and not NeedR21 and not NeedR24 and not NeedR25 and not NeedR26 then
    MsgBox(CustomMessage('NoRevitFound'), mbInformation, MB_OK);
  Result := True;
end;

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

procedure RemoveAddinAndDlls(const RevitVersion, DllSubDir: String);
var
  AppDataDir: String;
begin
  AppDataDir := ExpandConstant('{userappdata}');
  DeleteFile(AppDataDir + '\Autodesk\Revit\Addins\' + RevitVersion + '\SmartCon.addin');
  DelTree(AppDataDir + '\SmartCon\' + DllSubDir, True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if NeedR19 then
    begin
      if Revit2019Installed then WriteAddinFile('2019', '2019-2020');
      if Revit2020Installed then WriteAddinFile('2020', '2019-2020');
    end;
    if NeedR21 then
    begin
      if Revit2021Installed then WriteAddinFile('2021', '2021-2023');
      if Revit2022Installed then WriteAddinFile('2022', '2021-2023');
      if Revit2023Installed then WriteAddinFile('2023', '2021-2023');
    end;
    if NeedR24 then
    begin
      if Revit2024Installed then WriteAddinFile('2024', '2024');
    end;
    if NeedR25 then
    begin
      if Revit2025Installed then WriteAddinFile('2025', '2025');
    end;
    if NeedR26 then
    begin
      if Revit2026Installed then WriteAddinFile('2026', '2026');
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveAddinAndDlls('2019', '2019-2020');
    RemoveAddinAndDlls('2020', '2019-2020');
    RemoveAddinAndDlls('2021', '2021-2023');
    RemoveAddinAndDlls('2022', '2021-2023');
    RemoveAddinAndDlls('2023', '2021-2023');
    RemoveAddinAndDlls('2024', '2024');
    RemoveAddinAndDlls('2025', '2025');
    RemoveAddinAndDlls('2026', '2026');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.exe');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.dll');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.deps.json');
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.Updater.runtimeconfig.json');
    DelTree(ExpandConstant('{app}'), True, True, True);
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}\2019-2020"
Type: filesandordirs; Name: "{app}\2021-2023"
Type: filesandordirs; Name: "{app}\2024"
Type: filesandordirs; Name: "{app}\2025"
Type: filesandordirs; Name: "{app}\2026"
Type: files; Name: "{app}\SmartCon.Updater.exe"
Type: files; Name: "{app}\SmartCon.Updater.dll"
Type: filesandordirs; Name: "{app}"
