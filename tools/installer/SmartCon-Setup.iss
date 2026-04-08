#define AppName "SmartCon"
#define AppPublisher "AGK Engineering"
#define AppURL "https://github.com/AGK-Engineering/AGK-SmartCon-Pro"
#define RevitVersion "2025"

#ifndef AppVersion
  #define AppVersion = Trim(FileRead(FileOpen("..\..\Version.txt"), 0)));
#endif

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
DefaultDirName={userappdata}\Autodesk\Revit\Addins\{#RevitVersion}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName} {#AppVersion} for Revit {#RevitVersion}
UninstallDisplayIcon={app}\SmartCon\SmartCon.App.dll
OutputDir=..\..\artifacts
OutputBaseFilename=SmartCon-{#AppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\..\artifacts\publish\SmartCon\SmartCon.App.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.Core.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.Revit.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.UI.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.PipeConnect.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.Updater.exe"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.Updater.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\SmartCon.Updater.deps.json"; DestDir: "{app}\SmartCon"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\artifacts\publish\SmartCon\SmartCon.Updater.runtimeconfig.json"; DestDir: "{app}\SmartCon"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\artifacts\publish\SmartCon\CommunityToolkit.*.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion
Source: "..\..\artifacts\publish\SmartCon\Microsoft.Extensions.*.dll"; DestDir: "{app}\SmartCon"; Flags: ignoreversion

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  AddinContent: string;
  AddinPath: string;
begin
  if CurStep = ssPostInstall then
  begin
    AddinPath := ExpandConstant('{app}') + '\SmartCon.addin';
    AddinContent :=
      '<?xml version="1.0" encoding="utf-8" standalone="no"?>' + #13#10 +
      '<RevitAddIns>' + #13#10 +
      '  <AddIn Type="Application">' + #13#10 +
      '    <Name>SmartCon</Name>' + #13#10 +
      '    <Assembly>SmartCon\SmartCon.App.dll</Assembly>' + #13#10 +
      '    <FullClassName>SmartCon.App.App</FullClassName>' + #13#10 +
      '    <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>' + #13#10 +
      '    <VendorId>AGK</VendorId>' + #13#10 +
      '    <VendorDescription>AGK Engineering</VendorDescription>' + #13#10 +
      '  </AddIn>' + #13#10 +
      '</RevitAddIns>';
    SaveStringToFile(AddinPath, AddinContent, False);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DelTree(ExpandConstant('{app}') + '\SmartCon', True, True, True);
    DeleteFile(ExpandConstant('{app}') + '\SmartCon.addin');
  end;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}\SmartCon"
Type: files; Name: "{app}\SmartCon.addin"
