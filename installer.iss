#define AppName "Apollo Mic Bridge"
#define AppVersion "0.1.2"

[Setup]
AppId={{77DCFA70-A21A-43F9-AB44-C6A8D3481997}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Sky-Systems
DefaultDirName={localappdata}\Programs\SkySystems\ApolloMicBridge
DefaultGroupName=SkySystems\Apollo Mic Bridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
OutputDir=dist
OutputBaseFilename=ApolloMicBridge-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\ApolloMicBridge.exe
AppMutex=Local\SkySystems.ApolloMicBridge
CloseApplications=no
SetupLogging=yes
UninstallLogging=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.Autostart=Bei der Windows-Anmeldung im Hintergrund starten
german.DesktopIcon=Desktop-Verknüpfung erstellen
german.OpenApp=Apollo Mic Bridge zur Einrichtung öffnen
german.CloseError=Apollo Mic Bridge konnte nicht beendet werden. Bitte über das Tray-Menü beenden und erneut versuchen.
english.Autostart=Start in the background when signing in to Windows
english.DesktopIcon=Create a desktop shortcut
english.OpenApp=Open Apollo Mic Bridge to configure the microphone
english.CloseError=Apollo Mic Bridge could not be closed. Quit it from the tray menu and try again.

[Tasks]
Name: "autostart"; Description: "{cm:Autostart}"
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "dist\win-x64\ApolloMicBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "dist\win-x64\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Apollo Mic Bridge"; Filename: "{app}\ApolloMicBridge.exe"; WorkingDir: "{app}"
Name: "{group}\Deinstallieren - Uninstall"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Apollo Mic Bridge"; Filename: "{app}\ApolloMicBridge.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\SkySystems\ApolloMicBridge"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SkySystemsApolloMicBridge"; ValueData: """{app}\ApolloMicBridge.exe"" --autostart --background"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "SkySystemsApolloMicBridge"; Tasks: not autostart; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "SkySystemsApolloMicBridge"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\ApolloMicBridge.exe"; Description: "{cm:OpenApp}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{localappdata}\SkySystems\ApolloMicBridge\settings.json"
Type: files; Name: "{localappdata}\SkySystems\ApolloMicBridge\settings.json.tmp"
Type: files; Name: "{localappdata}\SkySystems\ApolloMicBridge\app.log"
Type: files; Name: "{localappdata}\SkySystems\ApolloMicBridge\app.log.previous"
Type: dirifempty; Name: "{localappdata}\SkySystems\ApolloMicBridge"

[Code]
function StopOwnedApp(const Directory: String): Boolean;
var
  AppFile: String;
  ResultCode: Integer;
begin
  AppFile := AddBackslash(Directory) + 'ApolloMicBridge.exe';
  Result := True;
  if FileExists(AppFile) then
    Result := Exec(AppFile, '--quit', Directory, SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('CloseError'), mbError, MB_OK, IDOK);
end;

function InitializeSetup(): Boolean;
var
  PreviousDirectory: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\SkySystems\ApolloMicBridge', 'InstallPath', PreviousDirectory) then
    Result := StopOwnedApp(PreviousDirectory);
end;

procedure InitializeWizard();
var
  PreviousDirectory, Command: String;
begin
  if RegQueryStringValue(HKCU, 'Software\SkySystems\ApolloMicBridge', 'InstallPath', PreviousDirectory) then
  begin
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SkySystemsApolloMicBridge', Command) then
      WizardSelectTasks('autostart')
    else
      WizardSelectTasks('!autostart');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopOwnedApp(ExpandConstant('{app}'));
end;
