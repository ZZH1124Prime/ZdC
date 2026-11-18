; Build after publishing: ISCC.exe installer\ZdC.iss
; This installer does not install a packet driver or change network settings.
#define AppVersion "0.1.0"
[Setup]
AppId={{D04744F1-2909-4A80-B108-295247AFBF11}
AppName=ZdC
AppVersion={#AppVersion}
AppPublisher=ZdC
DefaultDirName={autopf}\ZdC
DefaultGroupName=ZdC
UninstallDisplayIcon={app}\ZdC.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=ZdC-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableProgramGroupPage=yes
WizardStyle=modern
[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\ZdC"; Filename: "{app}\ZdC.exe"
Name: "{group}\卸载 ZdC"; Filename: "{uninstallexe}"
[Run]
; Deliberately no automatic application or driver launch after setup.
[UninstallDelete]
; Per-user settings are retained on uninstall.
