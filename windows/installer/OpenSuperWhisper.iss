; OpenSuperWhisper installer.
;
; Built by build-installer.ps1, which publishes the app first and passes the version
; in. Do not run this directly with ISCC unless build\publish is already populated.
;
; Decisions worth knowing before changing anything here:
;
;   * Self-contained. The .NET runtime ships inside the payload, so a fresh machine
;     needs nothing installed first. A runtime prerequisite is exactly where consumer
;     installs get abandoned, and the app already promises that first run works
;     offline because the whisper model is bundled too.
;
;   * Per-user install, no elevation. The app registers a keyboard hook and
;     synthesises input; UIPI means an elevated app cannot be reached by SendInput
;     from a non-elevated one, and installing to Program Files would also demand a UAC
;     prompt for something that only ever touches one user's files. It also keeps
;     uninstall from needing admin.
;
;   * Unpackaged, not MSIX. MSIX would sandbox the hooks and SendInput this app is
;     built on - see the plan's section 2.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "OpenSuperWhisper"
#define AppPublisher "OpenSuperWhisper"
#define AppExeName "OpenSuperWhisper.exe"
#define AppMutex "Global\OpenSuperWhisper.SingleInstance"

[Setup]
AppId={{B5F2D9E4-3A1C-4F7B-9E2D-6C8A1B4F0D73}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; Per-user: no UAC prompt, and nothing written outside this user's profile.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Rev. 3 floor: Windows 11, x64 only. Saying so here is what turns "it installs and
; then misbehaves" into a clear refusal.
MinVersion=10.0.22000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; The app holds this while running. Inno checks it and asks the user to close the app
; rather than writing over files that are in use - which, with a keyboard hook loaded,
; would otherwise need a reboot to finish.
AppMutex={#AppMutex}
CloseApplications=yes
RestartApplications=no

OutputDir=..\build\installer
OutputBaseFilename=OpenSuperWhisper-{#AppVersion}-x64
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern

; The payload is mostly the bundled model and the .NET runtime, both of which compress
; well and neither of which changes often.
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts"; Flags: unchecked

[Files]
; Everything dotnet publish produced: the app, the .NET runtime, whisper.dll and its
; ggml companions, and the bundled whisper and VAD models.
Source: "..\build\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} Settings"; Filename: "{app}\{#AppExeName}"; Parameters: "--settings"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Sign-in startup, only when asked for. The same value the app's own settings checkbox
; manages, in the same place, so the two cannot disagree - and uninstall deletes it
; whether it was set here or in the app.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExeName}"" --autostart"; \
    Flags: uninsdeletevalue; Tasks: startup

[Run]
; Unchecked by default: this is a dictation tool that installs a keyboard hook, and
; starting it the instant setup closes is presumptuous.
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; \
    Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; The log, which lives in the data directory and is not user content.
Type: files; Name: "{localappdata}\{#AppName}\log.txt"
Type: files; Name: "{localappdata}\{#AppName}\log-previous.txt"
Type: files; Name: "{localappdata}\{#AppName}\log-cli.txt"

[Code]
// Offers to remove the user's transcripts, recordings and settings at uninstall.
//
// Asked rather than assumed, in both directions. Deleting them silently would destroy
// content the user may still want; leaving them silently would abandon a folder of
// recorded audio on a machine someone believes they have just cleaned. Default is to
// keep, so reinstalling to fix something does not cost anyone their history.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;

  // A silent uninstall cannot ask - and Inno answers a suppressed MsgBox with YES
  // regardless of which button the script marks as default. So the prompt below,
  // under /VERYSILENT /SUPPRESSMSGBOXES, deleted the data without anyone seeing a
  // question. Caught by running exactly that command, after it had already destroyed
  // a real recordings directory.
  //
  // No answer therefore means keep. Unattended removal is where a wrong default does
  // the most damage, because nobody is watching it happen.
  if UninstallSilent then
    exit;

  DataDir := ExpandConstant('{localappdata}\{#AppName}');

  if not DirExists(DataDir) then
    exit;

  if MsgBox('Also delete your transcripts, recordings and settings?' + #13#10#13#10
            + DataDir + #13#10#13#10
            + 'Choose No to keep them for a future install.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;
