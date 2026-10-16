; User-scope x64 installer for the local GlassDesk candidate.
#ifndef AppIdValue
  #error AppIdValue is required.
#endif
#if AppIdValue != "GlassDesk.Independent"
  #error This installer must use the standard GlassDesk registration.
#endif
#ifndef ProductVersion
  #error ProductVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PackageBaseName
  #error PackageBaseName is required.
#endif
#define StageDirectory GetEnv("GLASSDESK_PACKAGE_STAGE")
#if StageDirectory == ""
  #error GLASSDESK_PACKAGE_STAGE is required.
#endif

[Setup]
AppId={#AppIdValue}
AppName=GlassDesk Plus
AppVersion={#ProductVersion}
AppVerName=GlassDesk {#ProductVersion}
DefaultDirName={localappdata}\Programs\GlassDesk
DefaultGroupName=GlassDesk
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.22000
UninstallDisplayName=GlassDesk
UninstallDisplayIcon={app}\GlassDesk.exe
SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico
AppPublisher=GlassDesk
VersionInfoProductName=GlassDesk Plus
VersionInfoProductVersion={#FileVersion}
VersionInfoVersion={#FileVersion}
VersionInfoTextVersion={#FileVersion}
VersionInfoProductTextVersion={#ProductVersion}
OutputBaseFilename={#PackageBaseName}-Setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式:"; Flags: unchecked

[Files]
Source: "{#StageDirectory}\*"; DestDir: "{app}"; Excludes: "Data\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDirectory}\GlassDesk.ico"; DestDir: "{app}"; DestName: "GlassDesk-{#ProductVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\GlassDesk-{#ProductVersion}.ico"
Name: "{autodesktop}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"; IconFilename: "{app}\GlassDesk-{#ProductVersion}.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue
; 安装时开启系统透明效果（设置 → 个性化 → 颜色 → 透明效果）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"; ValueName: "EnableTransparency"; ValueType: dword; ValueData: "1"
; 安装时开启系统动画效果（设置 → 辅助功能 → 视觉效果 → 动画效果；窗口动画开关）
Root: HKCU; Subkey: "Control Panel\Desktop\WindowMetrics"; ValueName: "MinAnimate"; ValueType: dword; ValueData: "1"
; 开机自启：与设置页“登录时启动”共用同一 Run 值，安装后默认开启
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppIdValue}"; ValueType: string; ValueData: """{app}\GlassDesk.exe"" --autostart"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\GlassDesk.exe"; Parameters: "--autostart"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: IsGlassDeskUpdate
; 创建“登录时启动”计划任务：登录时优先拉起 GlassDesk，早于普通启动项
Filename: "schtasks.exe"; Parameters: "/Create /F /TN ""GlassDesk"" /TR """"{app}\GlassDesk.exe"" --autostart"" /SC ONLOGON /RL LIMITED /IT"; Flags: runhidden nowait

[UninstallRun]
; 卸载前强制结束正在运行的 GlassDesk（Dock 窗口会拦截优雅关闭请求，必须兜底强杀）
Filename: "taskkill.exe"; Parameters: "/IM GlassDesk.exe /F /T"; Flags: runhidden
; 卸载时删除登录计划任务
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN ""GlassDesk"""; Flags: runhidden nowait

[Code]
function IsGlassDeskUpdate: Boolean;
begin
  Result := ExpandConstant('{param:GLASSDESKUPDATE|0}') = '1';
end;
