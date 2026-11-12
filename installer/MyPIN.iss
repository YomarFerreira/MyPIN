; Instalador do MyPIN (Inno Setup 6)
; Não rode direto: use  tools\gerar_instalador.ps1  (ele gera o app e passa a versão).
;
; • Instala só para o usuário (não pede administrador): %LOCALAPPDATA%\Programs\MyPIN
; • Atalho no menu Iniciar; atalho na área de trabalho e "abrir com o Windows" são opcionais
; • Atualizar = rodar o instalador novo por cima (fecha o MyPIN aberto antes)
; • Desinstalar NÃO apaga as notas (%APPDATA%\MyPIN)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; não mudar o AppId: é ele que faz o instalador novo atualizar o antigo
AppId={{A7B9946E-146B-4AE7-96AA-A3CEB462B534}
AppName=MyPIN
AppVersion={#AppVersion}
AppVerName=MyPIN {#AppVersion}
AppPublisher=Yomar
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\MyPIN
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=MyPIN-Setup-{#AppVersion}
SetupIconFile=..\src\MyPIN\Assets\app.ico
UninstallDisplayIcon={app}\MyPIN.exe
UninstallDisplayName=MyPIN
WizardStyle=modern
; termos de uso: o colega precisa marcar "Aceito" para instalar
LicenseFile=termos.txt
Compression=lzma2/ultra64
SolidCompression=yes
; se o MyPIN estiver aberto, o instalador pede para fechar
AppMutex=MyPIN_SingleInstance_v1
CloseApplications=yes

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "startup"; Description: "Abrir o MyPIN junto com o Windows"
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\MyPIN"; Filename: "{app}\MyPIN.exe"
Name: "{userdesktop}\MyPIN"; Filename: "{app}\MyPIN.exe"; Tasks: desktopicon

[Registry]
; o mesmo valor que o menu da bandeja liga/desliga ("Iniciar com o Windows")
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MyPIN"; \
  ValueData: """{app}\MyPIN.exe"""; Tasks: startup

[Run]
Filename: "{app}\MyPIN.exe"; Description: "Abrir o MyPIN agora"; Flags: nowait postinstall skipifsilent

[Code]
// ao desinstalar, tira o "abrir com o Windows" mesmo que tenha sido ligado depois, pela bandeja
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MyPIN');
end;
