; =============================================================
; ACS Simulator - Inno Setup Script
; =============================================================
; Compile with: ISCC.exe ACS.Simulator.Setup.iss
; build-simulator.ps1 passes AppVersion from AppVersion.props.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PackagePath
  #define PackagePath "..\..\dist-simulator"
#endif

[Setup]
AppName=ACS Simulator
AppVersion={#AppVersion}
AppVerName=ACS Simulator {#AppVersion}
AppPublisher=ACS Team
AppPublisherURL=https://github.com/buiminhduc23011/ACS
DefaultDirName=C:\ACS.Simulator
DefaultGroupName=ACS Simulator
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=ACS-Simulator-{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
SetupIconFile=compiler:SetupClassicIcon.ico
UninstallDisplayIcon={app}\server\ACS.Simulator.API.exe
RestartIfNeededByRun=no
AllowNoIcons=yes
UninstallDisplayName=ACS Simulator {#AppVersion}
CreateUninstallRegKey=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Components]
Name: "main"; Description: "ACS Simulator (Backend + Web UI)"; Flags: fixed

[Files]
Source: "{#PackagePath}\server\*"; DestDir: "{app}\server"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackagePath}\setup-simulator.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackagePath}\install-simulator-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Dọn shortcut Configure cũ để desktop chỉ còn icon Web UI.
Type: files; Name: "{commondesktop}\ACS Simulator - Configure.lnk"

[Dirs]
Name: "{app}\logs"; Permissions: users-full
Name: "{app}\server\logs"; Permissions: users-full
Name: "{app}\server\data"; Permissions: users-full

[Icons]
; Start Menu
Name: "{group}\ACS Simulator"; Filename: "{code:GetAppUrl}"; IconFilename: "{app}\server\ACS.Simulator.API.exe"; Comment: "Mở giao diện ACS Simulator"
Name: "{group}\ACS Simulator - Configure"; Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\setup-simulator.ps1"" -InstallDir ""{app}"""; WorkingDir: "{app}"; IconFilename: "{app}\server\ACS.Simulator.API.exe"
Name: "{group}\Uninstall ACS Simulator"; Filename: "{uninstallexe}"

; Desktop: chỉ một icon mở Web UI.
Name: "{commondesktop}\ACS Simulator"; Filename: "{code:GetAppUrl}"; IconFilename: "{app}\server\ACS.Simulator.API.exe"; Comment: "Mở giao diện ACS Simulator"

[Run]
Filename: "{code:GetAppUrl}"; Description: "Mở giao diện ACS Simulator trên trình duyệt"; Flags: postinstall shellexec nowait

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop ACS.Simulator"; Flags: runhidden; RunOnceId: "StopSimService"
Filename: "cmd.exe"; Parameters: "/c timeout /t 3 /nobreak >nul"; Flags: runhidden; RunOnceId: "WaitSimStop"
Filename: "sc.exe"; Parameters: "delete ACS.Simulator"; Flags: runhidden; RunOnceId: "DeleteSimService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\server\logs"

[Code]
var
  ConfigPage: TInputQueryWizardPage;

procedure InitializeWizard();
begin
  ConfigPage := CreateInputQueryPage(
    wpSelectDir,
    'Cấu hình ACS Simulator',
    'Nhập cổng Web UI, MQTT broker và ACS API',
    'Các giá trị này được ghi vào appsettings.Production.json.');

  ConfigPage.Add('Simulator Port:', False);
  ConfigPage.Add('MQTT Broker Address:', False);
  ConfigPage.Add('MQTT Broker Port:', False);
  ConfigPage.Add('ACS API Base URL:', False);

  ConfigPage.Values[0] := '9060';
  ConfigPage.Values[1] := '127.0.0.1';
  ConfigPage.Values[2] := '1883';
  ConfigPage.Values[3] := 'http://localhost:9050';
end;

function GetAppUrl(Param: String): String;
var
  Port: String;
begin
  Port := '9060';
  if ConfigPage <> nil then
  begin
    Port := Trim(ConfigPage.Values[0]);
    if Port = '' then Port := '9060';
  end;
  Result := 'http://localhost:' + Port;
end;

function IsValidPort(const Value: String): Boolean;
var
  Port: Integer;
begin
  Port := StrToIntDef(Trim(Value), -1);
  Result := (Port >= 1) and (Port <= 65535);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  AcsUrl: String;
begin
  Result := True;

  if CurPageID = ConfigPage.ID then
  begin
    if not IsValidPort(ConfigPage.Values[0]) then
    begin
      MsgBox('Simulator Port không hợp lệ. Nhập số trong khoảng 1..65535.', mbError, MB_OK);
      Result := False;
      exit;
    end;

    if Trim(ConfigPage.Values[1]) = '' then
    begin
      MsgBox('MQTT Broker Address không được để trống.', mbError, MB_OK);
      Result := False;
      exit;
    end;

    if not IsValidPort(ConfigPage.Values[2]) then
    begin
      MsgBox('MQTT Broker Port không hợp lệ. Nhập số trong khoảng 1..65535.', mbError, MB_OK);
      Result := False;
      exit;
    end;

    AcsUrl := Lowercase(Trim(ConfigPage.Values[3]));
    if (Pos('http://', AcsUrl) <> 1) and (Pos('https://', AcsUrl) <> 1) then
    begin
      MsgBox('ACS API Base URL phải bắt đầu bằng http:// hoặc https://.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure StopSimulatorService();
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop ACS.Simulator', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(3000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopSimulatorService();
  Result := '';
end;

function GetPowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function GetInstallLogPath(): String;
begin
  Result := ExpandConstant('{app}\logs\install-service.log');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Args: String;
begin
  if CurStep = ssPostInstall then
  begin
    Args :=
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install-simulator-service.ps1') + '"' +
      ' -InstallDir "' + ExpandConstant('{app}') + '"' +
      ' -SimPort ' + Trim(ConfigPage.Values[0]) +
      ' -MqttHost "' + Trim(ConfigPage.Values[1]) + '"' +
      ' -MqttPort ' + Trim(ConfigPage.Values[2]) +
      ' -AcsApiBaseUrl "' + Trim(ConfigPage.Values[3]) + '"';

    if not Exec(GetPowerShellPath(), Args, ExpandConstant('{app}'), SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException(
        'ACS Simulator service configuration failed.'#13#10 +
        'Log: ' + GetInstallLogPath());
  end;
end;

// Giữ appsettings.Production.json khi gỡ cài đặt.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigFile: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    ConfigFile := ExpandConstant('{app}\server\appsettings.Production.json');
    if FileExists(ConfigFile) then
    begin
      if MsgBox('Bạn có muốn giữ lại file cấu hình (appsettings.Production.json)?'#13#10 +
                'Chọn Yes để lưu một bản sao ra Desktop.', mbConfirmation, MB_YESNO) = IDYES then
      begin
        CopyFile(ConfigFile, ExpandConstant('{userdesktop}\appsettings.Production.json.backup'), False);
      end;
    end;
  end;
end;
