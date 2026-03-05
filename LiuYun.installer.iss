#define AppName "LiuYun"
#define AppVersion "2.0.0"
#define AppPublisher "Losst"
#define AppExeName "LiuYun.exe"
#define ProjectRoot AddBackslash(SourcePath)
#ifndef BuildArch
  #define BuildArch "x64"
#endif
#ifndef BuildConfig
  #define BuildConfig "Release"
#endif

#if BuildArch == "x64"
  #define RuntimeId "win-x64"
#elif BuildArch == "x86"
  #define RuntimeId "win-x86"
#else
  #error BuildArch must be x64 or x86.
#endif

#define BuildDir ProjectRoot + "bin\\" + BuildArch + "\\" + BuildConfig + "\\net8.0-windows10.0.19041.0\\" + RuntimeId + "\\publish"
#define IconFile ProjectRoot + "Assets\\LiuYun.ico"

[Setup]
AppId={{7B3AB5D4-56C7-4F92-8DD8-BE8A7F7E2A01}
AppName={#AppName}
AppVerName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
UninstallDisplayName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir={#ProjectRoot}dist
OutputBaseFilename=LiuYun_2.0.0_{#BuildArch}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#if BuildArch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
PrivilegesRequired=admin
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
ShowLanguageDialog=no
LanguageDetectionMethod=none
UsePreviousLanguage=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#IconFile}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
