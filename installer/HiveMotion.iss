#define MyAppName "HiveMotion"
#define MyAppPublisher "woncomp"
#define MyAppExeName "HiveMotion.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{2FB5D162-82A1-48F3-BF11-6B78C09AEEC0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=HiveMotion-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  // Value names under this key are the installed runtime versions ("8.0.19", ...).
  DesktopRuntimeRegKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  // Stable permalink to the latest .NET 8.0.x Desktop Runtime installer; no patch version to maintain.
  DesktopRuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  DesktopRuntimeFileName = 'windowsdesktop-runtime-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;

function HasDesktopRuntimeRegistryEntry: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetValueNames(HKLM, DesktopRuntimeRegKey, Names) then
    exit;
  for I := 0 to GetArrayLength(Names) - 1 do
    // Any 8.x satisfies the app: roll-forward defaults to Minor.
    if Copy(Names[I], 1, 4) = '8.0.' then
    begin
      Result := True;
      exit;
    end;
end;

function HasDesktopRuntimeFolder: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  // Some machines have no sharedfx registry entries despite installed runtimes;
  // the runtime folders are present in every default install.
  if not FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*'), FindRec) then
    exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function IsDesktopRuntimeInstalled: Boolean;
begin
  Result := HasDesktopRuntimeRegistryEntry or HasDesktopRuntimeFolder;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading .NET 8 Desktop Runtime',
    'Setup needs to download and install the Microsoft .NET 8 Desktop Runtime. Please wait...',
    @OnDownloadProgress);
end;

function DownloadRuntimeInstaller: Boolean;
begin
  Result := False;
  if WizardSilent then
  begin
    try
      DownloadTemporaryFile(DesktopRuntimeUrl, DesktopRuntimeFileName, '', @OnDownloadProgress);
      Result := True;
    except
      Log(GetExceptionMessage);
    end;
  end
  else
  begin
    DownloadPage.Clear;
    DownloadPage.Add(DesktopRuntimeUrl, DesktopRuntimeFileName, '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Result := True;
      except
        if not DownloadPage.AbortedByUser then
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if IsDesktopRuntimeInstalled then
    exit;

  if not DownloadRuntimeInstaller then
  begin
    Result := 'Failed to download the .NET 8 Desktop Runtime. Check your internet connection and run setup again.';
    exit;
  end;

  if not Exec(ExpandConstant('{tmp}\' + DesktopRuntimeFileName), '/install /quiet /norestart', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := 'Failed to launch the .NET 8 Desktop Runtime installer.'
  else if ResultCode = 3010 then
    // Success, reboot required; the /norestart flag defers it to the end of setup.
    NeedsRestart := True
  else if ResultCode <> 0 then
    Result := FmtMessage('The .NET 8 Desktop Runtime installer failed with exit code %1.', [IntToStr(ResultCode)]);
end;
