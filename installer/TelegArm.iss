; =============================================================================
;  TelegArm v1.1.0 - Inno Setup script
;
;  Compile with Inno Setup 6 (ISCC.exe):   ISCC.exe TelegArm.iss
;  Produces:  installer\Output\TelegArm-Setup-1.1.0.exe
;
;  PREREQUISITE: build Release first (TelegArm\bin\Release) with a real
;  ApiCredentials.Local.cs present, so the shipped binary authenticates.
;
;  PLATFORM NOTE: this Inno installer is an x86 executable, so it runs on
;  x86 / x64 Windows only. Windows RT 8.1 / Windows 10 ARM32 CANNOT execute
;  x86 installers - for those devices, deploy by copying the bin\Release
;  folder to the device (the AnyCPU/Prefer32Bit payload runs there as-is).
;
;  WRITE-LOCATION CAVEAT: TelegArm stores its session (accounts\), settings.json,
;  and logs BESIDE the executable (AppDomain.BaseDirectory). A Program Files
;  install therefore needs write access there - fine when run elevated /
;  single-user, but a standard-user run may fail to persist the session. If that
;  bites, either (a) switch DefaultDirName below to a per-user location:
;  DefaultDirName={localappdata}\Programs\TelegArm  (drop PrivilegesRequired), or
;  (b) move the app's storage base to %AppData%.
;
;  BRANDING IMAGES (RT-safe: stock modern wizard + images only; NO skin DLLs /
;  ISSkin / native plugins). Supply these BMPs in installer\ then uncomment the
;  WizardImageFile / WizardSmallImageFile lines in [Setup]:
;    wizard-large.bmp  -> WizardImageFile      (tall left panel, Welcome + Finish pages)
;                         Format: 24-bit BMP (NOT png/jpg). Base size 164 x 314 px @100%.
;                         For crisp HiDPI, provide a comma-separated set, largest first,
;                         e.g. 410x797,328x628,273x556,246x470,192x386,164x314.
;                         Magenta/purple themed, TelegArm logo - this is the dominant art.
;    wizard-small.bmp  -> WizardSmallImageFile (small logo, top-right of inner pages)
;                         Format: 24-bit BMP. Base size 55 x 58 px @100%; up to 138 x 140.
;                         Comma-separated HiDPI set allowed, e.g. 138x140,110x112,83x84,55x58.
;    (32-bit BMPs with alpha are supported if you also set WizardImageAlphaFormat=premultiplied.)
;  SetupIconFile uses the app's own icon (..\icon.ico) for the setup .exe.
; =============================================================================

#define AppName      "TelegArm"
#define AppVersion   "1.1.0"
#define AppPublisher "Hamed (hamed7ir)"
#define AppUrl       "https://github.com/hamed7ir/TelegArm"
#define AppExe       "TelegArm.exe"
#define SrcDir       "..\TelegArm\bin\Release"

[Setup]
AppId={{0BE14FF9-F66B-4B3D-B34B-47B8DE8F836B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
OutputBaseFilename=TelegArm-Setup-{#AppVersion}
OutputDir=Output
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
; Inno 6 hides the Welcome page by default (DisableWelcomePage=yes); show it so the branded
; WelcomeLabel text + the large WizardImageFile art appear on their own first page.
DisableWelcomePage=no
; --- Branding ---
SetupIconFile=..\img-icon\icon.ico
; Top-right small logo = our app icon (icon.ico) converted to white-background BMPs, multi-size
; so it stays crisp across DPI. Regenerate from icon.ico if the app icon changes.
WizardSmallImageFile=wizard-small-58.bmp,wizard-small-92.bmp,wizard-small-138.bmp
; Large left-panel art (Welcome + Finish pages): a branded magenta->purple panel with the app
; icon + name, generated from icon.ico (multi-size for DPI). Swap in hand-designed art anytime by
; replacing these BMPs (keep the same names, or update this line).
WizardImageFile=wizard-large-164.bmp,wizard-large-246.bmp,wizard-large-328.bmp,wizard-large-410.bmp

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Branded welcome + finish text (TelegArm identity).
WelcomeLabel1=Welcome to the {#AppName} Setup Wizard
WelcomeLabel2=This will install {#AppName} {#AppVersion} on your computer.%n%n{#AppName} is a lightweight, pure-managed Telegram client for jailbroken Windows RT 8.1 / Windows 10 ARM32 and desktop Windows.%n%nIt is recommended that you close all other applications before continuing.
FinishedHeadingLabel=TelegArm is ready
FinishedLabelNoIcons=Setup has finished installing {#AppName} {#AppVersion} on your computer.
FinishedLabel=Setup has finished installing {#AppName} {#AppVersion} on your computer. You can launch it from the shortcuts that were created.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; --- App ROOT: exe + config, and everything the app resolves BESIDE the exe (NOT via CLR
;     probing). Native libs + data zips are located by the app's own BaseDirectory logic
;     (NativeLibraries / VlcEnvironment / EmojiRenderer). Fonts are embedded in the exe.
;     Debug symbols (*.pdb) are not shipped.
;       *.zip = libvlc-arm32.zip, libvlc-x86.zip, noto-emoji.zip (read beside the exe)
;       *.png = TelegArm.png    *.ico = trayicon.ico, trayicon-unread.ico
Source: "{#SrcDir}\TelegArm.exe";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\TelegArm.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\*.zip";               DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\*.png";               DestDir: "{app}"; Flags: ignoreversion
Source: "{#SrcDir}\*.ico";               DestDir: "{app}"; Flags: ignoreversion
; native rlottie\{ARM,x86}\rlottie.dll (SetDllDirectory target) stays beside the exe:
Source: "{#SrcDir}\rlottie\*";           DestDir: "{app}\rlottie"; Flags: ignoreversion recursesubdirs createallsubdirs
; --- dll\ : all MANAGED dependency assemblies, found via <probing privatePath="dll">.
;     Top-level *.dll are all managed (native rlottie.dll is under rlottie\ above; native
;     libVLC ships inside the zips, extracted beside the exe on first run). ---
Source: "{#SrcDir}\*.dll";               DestDir: "{app}\dll"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove ALL user-profile data TelegArm creates at RUNTIME. The installer only tracks {app}; because
; Program Files is read-only, the app writes its session/cache/settings/logs under the user profile
; (resolved LocalAppData-first -> Documents; Countries cache in Roaming). Uninstall must clear those too.
; SAVED MEDIA in Documents\TelegArm is PRESERVED: only app-generated items are deleted, then that folder
; is dropped ONLY if it is now empty (dirifempty).
;
; App state + media cache (LocalAppData is the data root on desktop; the whole dir is app-managed):
Type: filesandordirs; Name: "{localappdata}\TelegArm"
; Countries-list cache (Roaming AppData; purely app-generated):
Type: filesandordirs; Name: "{userappdata}\TelegArm"
; App-generated items under Documents (logs/crash, extracted libVLC, and - on RT - the data root/cache):
Type: filesandordirs; Name: "{userdocs}\TelegArm\Cache"
Type: filesandordirs; Name: "{userdocs}\TelegArm\accounts"
Type: filesandordirs; Name: "{userdocs}\TelegArm\logs"
Type: filesandordirs; Name: "{userdocs}\TelegArm\libvlc"
Type: files;          Name: "{userdocs}\TelegArm\settings.json"
Type: files;          Name: "{userdocs}\TelegArm\crash.log"
Type: files;          Name: "{userdocs}\TelegArm\TelegArm.session"
Type: files;          Name: "{userdocs}\TelegArm\TelegArm.phone"
Type: files;          Name: "{userdocs}\TelegArm\TelegArm.updates"
; Drop Documents\TelegArm ONLY if empty (keeps any media the user saved there):
Type: dirifempty;     Name: "{userdocs}\TelegArm"
; Leftover runtime dirs if this was ever run as a portable/writable install (beside the exe):
Type: filesandordirs; Name: "{app}\accounts"
Type: filesandordirs; Name: "{app}\libvlc"
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\Cache"
Type: files;          Name: "{app}\settings.json"
Type: files;          Name: "{app}\crash.log"
Type: files;          Name: "{app}\TelegArm.session"
Type: files;          Name: "{app}\TelegArm.phone"
Type: files;          Name: "{app}\TelegArm.updates"
