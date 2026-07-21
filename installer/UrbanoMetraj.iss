; ============================================================================
;  UrbanoMetraj  -  Inno Setup 6 script  (BricsCAD edition, v1.1.0.0)
;
;  Installs the obfuscated .bundle (assembled by build-release.ps1 into
;  dist\UrbanoMetraj.bundle) to the PER-USER BricsCAD plugin folder:
;     %APPDATA%\Bricsys\BricsCAD\V23x64\en_US\Support\ApplicationPlugins\UrbanoMetraj.bundle
;
;  BricsCAD V23 auto-loads ApplicationPlugins bundles on startup using the SAME
;  AutoCAD-style PackageContents.xml (Platform="AutoCAD*", LoadOnAutoCADStartup).
;  Verified against the working MyInfrastructureTools.bundle on this machine,
;  which loads in BricsCAD with NO TRUSTEDPATHS entry — so, unlike the AutoCAD
;  installer, this one does not need to whitelist a trusted path.
;
;  This is the BricsCAD-ONLY build (DLL compiled against BrxMgd/TD_Mgd).  The
;  AutoCAD / Civil 3D product stays on the separate v1.0.0.0 installer.
;  Per-user install, no admin.
; ============================================================================

#define MyAppName    "UrbanoMetraj for BricsCAD"
#define MyAppVersion "1.1.0"
#define MyPublisher  "UrbanoMetraj"
#define MyBundleName "UrbanoMetraj.bundle"
#define MyBundleSrc  "..\dist\UrbanoMetraj.bundle"

[Setup]
; Distinct AppId from the AutoCAD build so both editions can coexist in
; Add/Remove Programs and have independent uninstallers.
AppId={{9A7C2E10-7B44-4C1E-9E2A-URBANOMETRJ11}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyPublisher}

; {app} == the bundle folder itself (per-user, under BricsCAD's SRCHPATH, writable).
DefaultDirName={userappdata}\Bricsys\BricsCAD\V23x64\en_US\Support\ApplicationPlugins\{#MyBundleName}
DisableDirPage=yes
DisableProgramGroupPage=yes

PrivilegesRequired=lowest

OutputDir=Output
OutputBaseFilename=UrbanoMetraj-BricsCAD-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

VersionInfoVersion=1.1.0.0
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyPublisher}

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Files]
; Copy the whole assembled bundle (PackageContents.xml + Contents\*) into {app}.
Source: "{#MyBundleSrc}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Code]

// ───────────────────────────────────────────────────────────────────────────
// BricsCAD V23 does NOT auto-load ApplicationPlugins .bundle/PackageContents.xml
// the way AutoCAD does (verified at runtime: the bundle deploys but is never
// loaded at startup).  Auto-load is wired instead through BricsCAD's Startup
// Suite — a plain UTF-8 file listing one DLL path per line:
//   %APPDATA%\Bricsys\BricsCAD\V23x64\en_US\appload.dfs
// Append our DLL path to it (de-duplicated).  BricsCAD loads every entry on
// startup.  (Our path is pure ASCII, so an ANSI append never corrupts the
// existing UTF-8 lines.)
// ───────────────────────────────────────────────────────────────────────────
procedure AddToStartupSuite;
var
  DfsPath, Entry: String;
  Lines: TArrayOfString;
  i: Integer;
  Found: Boolean;
begin
  DfsPath := ExpandConstant('{userappdata}\Bricsys\BricsCAD\V23x64\en_US\appload.dfs');
  Entry   := ExpandConstant('{app}\Contents\Windows\UrbanoMetraj.dll');

  Found := False;
  if FileExists(DfsPath) and LoadStringsFromFile(DfsPath, Lines) then
  begin
    for i := 0 to GetArrayLength(Lines) - 1 do
      if CompareText(Trim(Lines[i]), Entry) = 0 then
        Found := True;
  end;

  if not Found then
    SaveStringToFile(DfsPath, Entry + #13#10, True);   // append; creates if missing
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    AddToStartupSuite;
end;

// On uninstall, strip our line back out of appload.dfs.  Operate on RAW BYTES
// (AnsiString) so other entries' UTF-8 paths (e.g. Turkish) are preserved
// untouched — our path is pure ASCII, so a byte-level Pos/Delete is safe.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DfsPath: String;
  Data, Target, EntryA: AnsiString;
  P: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  DfsPath := ExpandConstant('{userappdata}\Bricsys\BricsCAD\V23x64\en_US\appload.dfs');
  if not FileExists(DfsPath) then Exit;
  if not LoadStringFromFile(DfsPath, Data) then Exit;
  EntryA := ExpandConstant('{app}\Contents\Windows\UrbanoMetraj.dll');
  Target := EntryA + #13#10;
  P := Pos(Target, Data);
  if P > 0 then
    Delete(Data, P, Length(Target))
  else
  begin
    P := Pos(EntryA, Data);          // last line, no trailing CRLF
    if P > 0 then Delete(Data, P, Length(EntryA));
  end;
  SaveStringToFile(DfsPath, Data, False);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result :=
    'Target CAD:' + NewLine +
    Space + 'BricsCAD V23 (x64, en_US)' + NewLine +
    NewLine +
    'Install location:' + NewLine +
    Space + ExpandConstant('{app}') + NewLine +
    NewLine +
    'UrbanoMetraj will be added to BricsCAD''s Startup Suite (appload.dfs) so it ' +
    'loads automatically on startup.  Please CLOSE BricsCAD before installing so ' +
    'the change is not overwritten.  After install, start BricsCAD and type ' +
    'UT_COMMANDS.';
end;
