; User-scope x64 installer for the local GlassDesk candidate.
#ifndef AppIdValue
  #error AppIdValue is required.
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
AppName=GlassDesk
AppVersion={#ProductVersion}
AppVerName=GlassDesk {#ProductVersion}
DefaultDirName={localappdata}\Programs\GlassDesk
DefaultGroupName=GlassDesk
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
UsePreviousAppDir=yes
UninstallDisplayName=GlassDesk
UninstallDisplayIcon={app}\GlassDesk.exe
SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico
AppPublisher=Hoxien
AppPublisherURL=https://glassdesk.cn
AppSupportURL=https://glassdesk.cn
AppUpdatesURL=https://glassdesk.cn
VersionInfoCompany=Hoxien
VersionInfoDescription=GlassDesk {#ProductVersion} 安装程序（喜羊羊修改版）
VersionInfoCopyright=Hoxien · PolyForm Noncommercial 1.0.0
VersionInfoProductName=GlassDesk
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
Source: "{#StageDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GlassDesk"; Filename: "{app}\GlassDesk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdValue}_is1"; ValueName: "InstallLocation"; ValueType: string; ValueData: "{app}"; Flags: uninsdeletevalue
; 系统「透明效果」开关：值缺失时补成开启。真正的修复动作在 [Run] 里，因为
; 只有从程序里广播主题变化，DWM 才会立刻重新读取；这里不覆盖已经存在的 0，
; 否则助手进程会以为已经开启而跳过广播。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"; ValueName: "EnableTransparency"; ValueType: dword; ValueData: "1"; Flags: createvalueifdoesntexist

[Run]
; 安装完成后打开系统透明效果，修复部分机型玻璃/亚克力被 DWM 当成不透明的问题。
; 助手发现已经是开启状态时什么都不做，因此重复安装不会来回改用户设置。
Filename: "{app}\GlassDesk.exe"; Parameters: "--enable-system-transparency"; Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "{app}\GlassDesk.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "GlassDesk.Exit"
Filename: "{app}\GlassDesk.exe"; Parameters: "--restore-desktop-icons"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "GlassDesk.RestoreDesktopIcons"

[Code]
#include "InstallerUpgrade.iss"
