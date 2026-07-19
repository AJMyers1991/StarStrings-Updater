; ──────────────────────────────────────────────────────────
;  StarStrings Updater — Inno Setup Installer Script
;  Compile with: ISCC.exe StarStringsUpdater-Setup.iss
; ──────────────────────────────────────────────────────────

#define AppName       "StarStrings Updater"
#define AppVersion    "2.0.0"
#define AppPublisher  "StarStrings Updater Contributors"
#define AppURL        "https://github.com/MrKraken/StarStrings"
#define AppExeName    "StarStringsUpdater.exe"
#define OutputDir     "..\installer-output"

[Setup]
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
AppId={{B4F8A3D2-7C1E-4E92-A6D5-2F8B1C9E3D7A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=StarStringsUpdater-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
MinVersion=6.3.9600
LicenseFile=license.rtf

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\framework-dependent\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "license.rtf"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Updates StarStrings localization for Star Citizen"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Updates StarStrings localization for Star Citizen"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName} to configure"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetFallbackURL = 'https://dotnet.microsoft.com/en-us/download/dotnet/8.0';

var
  DotNetInstallPage: TOutputProgressWizardPage;

// ── Check for .NET 8 Desktop Runtime using dotnet CLI ───────
function IsDotNet8DesktopInstalled: Boolean;
var
  ResultCode: Integer;
begin
  // Use dotnet --list-runtimes (works regardless of install method:
  // SDK, standalone installer, winget, Visual Studio, script, etc.)
  Result := Exec('powershell.exe',
    '-NoProfile -Command ' +
    '"& { $out = & dotnet --list-runtimes 2>$null; ' +
    'if ($LASTEXITCODE -ne 0) { exit 1 }; ' +
    'if ($out -match ''Microsoft\.WindowsDesktop\.App 8\.0\.'') { exit 0 } else { exit 1 } }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('dotnet --list-runtimes check: code=%d found=%d', [ResultCode, Integer(Result and (ResultCode = 0))]));
  Result := Result and (ResultCode = 0);
end;

// ── Try installing via winget ───────────────────────────────
function TryInstallDotNet8ViaWinget: Boolean;
var
  ResultCode: Integer;
begin
  Log('Tier 1: Attempting .NET 8 install via winget...');
  Result := Exec('winget.exe',
    'install Microsoft.DotNet.DesktopRuntime.8 --silent --accept-package-agreements --accept-source-agreements',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('winget exited with code %d', [ResultCode]));
  Result := Result and (ResultCode = 0);
end;

// ── Write and run a PowerShell script to install .NET 8 ─────
function TryInstallDotNet8ViaInstaller: Boolean;
var
  ScriptPath: String;
  ScriptContent: String;
  ResultCode: Integer;
begin
  Result := False;
  ScriptPath := ExpandConstant('{tmp}\install-dotnet8.ps1');

  // Build the PowerShell script content (avoids all escaping issues)
  ScriptContent :=
    '$ErrorActionPreference = ''Stop''' + #13#10 +
    '# Step 1: find the download URL for the latest .NET 8 Desktop Runtime' + #13#10 +
    '$meta = Invoke-RestMethod -Uri ''https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json''' + #13#10 +
    '$latest = $meta.releases[0]' + #13#10 +
    '$files = $latest.windowsdesktop.files' + #13#10 +
    '$url = ($files | Where-Object { $_.name -like ''*win-x64.exe'' -and $_.rid -eq ''win-x64'' }).url' + #13#10 +
    'if (-not $url) { $url = ($files | Where-Object { $_.name -like ''*win-x64.exe'' }).url }' + #13#10 +
    'if (-not $url) { Write-Error ''Could not locate .NET 8 Desktop Runtime installer''; exit 1 }' + #13#10 +
    'Write-Host "Found installer: $url"' + #13#10 +
    '# Step 2: download the installer' + #13#10 +
    '$installer = Join-Path $env:TEMP ''dotnet-desktop-runtime-8.exe''' + #13#10 +
    'Write-Host ''Downloading .NET 8 Desktop Runtime...''' + #13#10 +
    'Invoke-WebRequest -Uri $url -OutFile $installer' + #13#10 +
    '# Step 3: run it silently' + #13#10 +
    'Write-Host ''Installing (this may take several minutes)...''' + #13#10 +
    '$proc = Start-Process -FilePath $installer -ArgumentList ''/install /quiet /norestart'' -Wait -PassThru' + #13#10 +
    'Remove-Item $installer -Force -ErrorAction SilentlyContinue' + #13#10 +
    'Write-Host "Installer exit code: $($proc.ExitCode)"' + #13#10 +
    'exit $proc.ExitCode';

  if not SaveStringToFile(ScriptPath, ScriptContent, False) then
  begin
    Log('ERROR: Could not write install script to temp directory.');
    Exit;
  end;

  Log('Tier 2: Running PowerShell installer script...');
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log(Format('PowerShell installer exited with code %d', [ResultCode]));
  Result := Result and (ResultCode = 0);
end;

// ── Open fallback download page in browser ──────────────────
procedure OpenDotNetDownloadPage;
var
  ErrorCode: Integer;
begin
  ShellExec('open', DotNetFallbackURL, '', '', SW_SHOW, ewNoWait, ErrorCode);
end;

// ── Auto-install .NET 8 ─────────────────────────────────────
function AutoInstallDotNet8: Boolean;
var
  MsgResult: Integer;
begin
  Result := False;

  // Tier 1: winget (fast, system-wide)
  DotNetInstallPage.SetText('Installing .NET 8 Desktop Runtime...',
    'Trying Windows Package Manager (winget).');
  if TryInstallDotNet8ViaWinget then
  begin
    if IsDotNet8DesktopInstalled then
    begin
      Result := True;
      Exit;
    end;
  end;

  // Tier 2: download the official Microsoft installer
  DotNetInstallPage.SetText('Installing .NET 8 Desktop Runtime...',
    'Downloading from Microsoft. This may take several minutes.');
  if TryInstallDotNet8ViaInstaller then
  begin
    if IsDotNet8DesktopInstalled then
    begin
      Result := True;
      Exit;
    end;
  end;

  // Tier 3: browser fallback (last resort)
  DotNetInstallPage.Hide;
  MsgResult := MsgBox(
    'Automatic installation was unsuccessful.' + #13#10#13#10 +
    'Your browser will open to the Microsoft .NET 8 download page.' + #13#10 +
    'Please download and install ".NET Desktop Runtime 8.0 (x64)",' + #13#10 +
    'then return here and click OK.',
    mbInformation, MB_OKCANCEL);

  if MsgResult = IDOK then
  begin
    OpenDotNetDownloadPage;
    while not IsDotNet8DesktopInstalled do
    begin
      MsgResult := MsgBox(
        '.NET 8 Desktop Runtime was not detected yet.' + #13#10#13#10 +
        'Click OK once installation is complete, or Cancel to exit.',
        mbConfirmation, MB_OKCANCEL);
      if MsgResult = IDCANCEL then
        Exit;
    end;
    Result := True;
  end;
end;

// ── Prepare-to-install: check .NET dependency ───────────────
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  if IsDotNet8DesktopInstalled then
  begin
    Log('.NET 8 Desktop Runtime detected — skipping install.');
    Exit;
  end;

  Log('.NET 8 Desktop Runtime NOT detected. Starting auto-install...');

  DotNetInstallPage := CreateOutputProgressPage(
    'Installing .NET 8 Desktop Runtime',
    'StarStrings Updater requires the .NET 8 Desktop Runtime. It is being installed automatically.');

  DotNetInstallPage.Show;
  try
    if not AutoInstallDotNet8 then
    begin
      Result := '.NET 8 Desktop Runtime is required but could not be installed. Setup will now exit.';
    end;
  finally
    DotNetInstallPage.Hide;
  end;
end;

// ── Verify on initialize ────────────────────────────────────
function InitializeSetup: Boolean;
begin
  Result := True;
end;

// ── Custom uninstall cleanup ────────────────────────────────
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigPath: String;
  MsgResult: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ConfigPath := GetEnv('LOCALAPPDATA') + '\StarStringsUpdater';
    if DirExists(ConfigPath) then
    begin
      MsgResult := MsgBox(
        'Do you want to remove the configuration and log files as well?' + #13#10#13#10 +
        ConfigPath + #13#10#13#10 +
        'This includes your saved installation paths and update history.',
        mbConfirmation, MB_YESNO);
      if MsgResult = IDYES then
        DelTree(ConfigPath, True, True, True);
    end;
  end;
end;