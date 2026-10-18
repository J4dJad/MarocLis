; MarocLIS setup program (Inno Setup 6).
;
; One setup.exe, two setup types:
;   Serveur      SQL Server Express (instance SQLEXPRESS) when no SQL Server is installed, the
;                workstation application, and the MarocLIS.Api Windows service that the phones
;                reach through Tailscale (docs/manuel/10-administration.md, « Service de validation
;                à distance »).
;   Poste client the workstation application only, pointed at the server's SQL Server.
;
; Built by installer/build-installer.ps1, which publishes the Desktop and the Api self-contained
; for win-x64 (no .NET runtime to install) into the staging folder this script packages.
;
; Upgrades keep the same AppId: the service is stopped before its files are replaced and started
; again afterwards, and an existing appsettings.json is never overwritten. Uninstalling removes
; the service and the program files, never the database nor SQL Server.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef StagingDir
  #define StagingDir "build\staging"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif

#define AppName "MarocLIS"
#define ApiServiceName "MarocLIS.Api"
#define UsersGroup "MarocLIS-Utilisateurs"
#define DatabaseName "MarocLIS"
; The SQL Server Express download offered on https://www.microsoft.com/sql-server/sql-server-downloads
; ("Express", "Download now"): Microsoft's SQL2025-SSEI-Expr.exe bootstrapper.
#define SqlExpressBootstrapperUrl "https://go.microsoft.com/fwlink/p/?linkid=2216019&clcid=0x409&culture=en-us&country=us"
#define SqlExpressBootstrapperName "SQL2025-SSEI-Expr.exe"
; Microsoft SQL Server License Terms (SQL Server 2022 and later), as linked from the SQL Server
; command-line installation documentation.
#define SqlLicenseUrl "https://aka.ms/useterms"
; The privacy notice that /SUPPRESSPRIVACYSTATEMENTNOTICE agrees to, per the same documentation.
#define SqlPrivacyUrl "https://learn.microsoft.com/sql/sql-server/sql-server-privacy"

[Setup]
AppId={{0C3D267B-5E7F-478A-9460-98F9037D1B82}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={sd}\MarocLIS
DefaultGroupName=MarocLIS
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
ShowLanguageDialog=yes
; French first whatever the Windows display language; English stays one click away.
LanguageDetectionMethod=none
UsePreviousSetupType=yes
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Compression=lzma2/max
SolidCompression=yes
OutputDir=output
OutputBaseFilename=MarocLIS-Setup-{#AppVersion}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\Desktop\MarocLIS.exe

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "server"; Description: "{cm:TypeServer}"
Name: "client"; Description: "{cm:TypeClient}"

[Components]
Name: "sql"; Description: "{cm:CompSql}"; Types: server; Flags: fixed
Name: "desktop"; Description: "{cm:CompDesktop}"; Types: server client; Flags: fixed
Name: "api"; Description: "{cm:CompApi}"; Types: server; Flags: fixed

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Serilog writes logs\ in the working folder of the application; the operators are not
; administrators.
Name: "{app}\Desktop\logs"; Permissions: users-modify; Components: desktop
Name: "{app}\Api\logs"; Components: api

[Files]
Source: "{#StagingDir}\Desktop\*"; DestDir: "{app}\Desktop"; Excludes: "\appsettings.json,\appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: desktop
Source: "{#StagingDir}\Desktop\appsettings.json"; DestDir: "{app}\Desktop"; Flags: onlyifdoesntexist; Components: desktop
Source: "{#StagingDir}\Api\*"; DestDir: "{app}\Api"; Excludes: "\appsettings.json,\appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: api
Source: "{#StagingDir}\Api\appsettings.json"; DestDir: "{app}\Api"; Flags: onlyifdoesntexist; Components: api
Source: "{#RepoRoot}\scripts\install-api-service.ps1"; DestDir: "{app}\Api"; Flags: ignoreversion; Components: api
Source: "maroclis-setup.ps1"; Flags: dontcopy

[Icons]
Name: "{group}\MarocLIS"; Filename: "{app}\Desktop\MarocLIS.exe"; WorkingDir: "{app}\Desktop"
Name: "{autodesktop}\MarocLIS"; Filename: "{app}\Desktop\MarocLIS.exe"; WorkingDir: "{app}\Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\Desktop\MarocLIS.exe"; WorkingDir: "{app}\Desktop"; Description: "{cm:LaunchProgram,MarocLIS}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Stops and deletes the service; the published files go with the rest of {app}. The database and
; SQL Server are left alone.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Api\install-api-service.ps1"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveApiService"; Components: api

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Installer"

[CustomMessages]
french.TypeServer=Serveur : base de données, application et service de validation mobile
english.TypeServer=Server: database, application and mobile validation service
french.TypeClient=Poste client : application seule, reliée au serveur
english.TypeClient=Workstation: application only, connected to the server
french.CompSql=SQL Server Express (instance SQLEXPRESS), s'il n'est pas déjà installé
english.CompSql=SQL Server Express (SQLEXPRESS instance), unless already installed
french.CompDesktop=Application MarocLIS
english.CompDesktop=MarocLIS application
french.CompApi=Service MarocLIS.Api (validation depuis le téléphone)
english.CompApi=MarocLIS.Api service (validation from the phone)

french.TypePageCaption=Type d'installation
english.TypePageCaption=Setup type
french.TypePageDescription=Cet ordinateur est-il le serveur du laboratoire ou un poste de travail ?
english.TypePageDescription=Is this computer the laboratory server or a workstation?
french.TypePageLabel=Choisissez le rôle de cet ordinateur. Installez d'abord « Serveur » sur l'ordinateur qui garde la base de données, puis « Poste client » sur chacun des autres postes.
english.TypePageLabel=Choose this computer's role. Install "Server" first on the computer that keeps the database, then "Workstation" on each of the other computers.
french.TypeServerDetail=Un seul serveur par laboratoire. Installe :%n%n  - SQL Server Express (instance SQLEXPRESS), sauf si SQL Server est déjà installé ;%n  - l'application MarocLIS, reliée à la base de ce serveur ;%n  - le service Windows MarocLIS.Api, qui sert l'application du téléphone du biologiste (validation à distance, publiée par Tailscale) ;%n  - le pare-feu Windows ouvert à SQL Server pour le réseau local.
english.TypeServerDetail=One server per laboratory. Installs:%n%n  - SQL Server Express (SQLEXPRESS instance), unless SQL Server is already installed;%n  - the MarocLIS application, connected to this server's database;%n  - the MarocLIS.Api Windows service, which serves the biologist's phone application (remote validation, published through Tailscale);%n  - the Windows firewall opened to SQL Server for the local network.
french.TypeClientDetail=Pour chaque poste de travail du laboratoire. Installe l'application MarocLIS seule ; elle utilise la base de données du serveur, dont le nom vous sera demandé.%n%nRien d'autre n'est à installer : l'application contient tout ce qu'il lui faut (.NET compris).
english.TypeClientDetail=For each workstation of the laboratory. Installs the MarocLIS application only; it uses the server's database, whose name you will be asked for.%n%nNothing else needs installing: the application carries everything it needs (.NET included).

french.SqlPageCaption=SQL Server
english.SqlPageCaption=SQL Server
french.SqlPageDescription=La base de données MarocLIS est hébergée par SQL Server sur ce serveur.
english.SqlPageDescription=The MarocLIS database is hosted by SQL Server on this server.
french.SqlFound=SQL Server est déjà installé sur cet ordinateur. Instance utilisée par MarocLIS :
english.SqlFound=SQL Server is already installed on this computer. Instance used by MarocLIS:
french.SqlFoundNote=L'installation crée la base « MarocLIS » si elle n'existe pas, donne l'accès au groupe Windows local « {#UsersGroup} » et au service MarocLIS.Api, active SQL Server Browser et ouvre le pare-feu Windows au réseau local. Votre compte doit être administrateur (sysadmin) de cette instance.
english.SqlFoundNote=Setup creates the "MarocLIS" database if it does not exist, grants access to the local Windows group "{#UsersGroup}" and to the MarocLIS.Api service, enables SQL Server Browser and opens the Windows firewall to the local network. Your account must be an administrator (sysadmin) of this instance.
french.SqlMissing=Aucun SQL Server n'est installé sur cet ordinateur. L'installation va installer Microsoft SQL Server 2025 Express :%n%n  - moteur de base de données seul, instance SQLEXPRESS ;%n  - TCP/IP activé et pare-feu ouvert au réseau local, pour les postes clients ;%n  - les administrateurs de cet ordinateur administrent SQL Server.%n%nSQL Server Express est gratuit ; sa licence Microsoft vous est présentée à la page suivante.
english.SqlMissing=No SQL Server is installed on this computer. Setup will install Microsoft SQL Server 2025 Express:%n%n  - database engine only, SQLEXPRESS instance;%n  - TCP/IP enabled and the firewall opened to the local network, for the workstations;%n  - the administrators of this computer administer SQL Server.%n%nSQL Server Express is free; its Microsoft licence is shown on the next page.
french.SqlSourceOffline=Support d'installation hors ligne trouvé à côté du programme d'installation : %1. Aucune connexion Internet n'est nécessaire.
english.SqlSourceOffline=Offline installation media found next to the setup program: %1. No Internet connection is needed.
french.SqlSourceDownload=Aucun support hors ligne à côté du programme d'installation : SQL Server Express sera téléchargé depuis Microsoft (environ 300 Mo, connexion Internet nécessaire). Pour installer sans Internet, placez SQLEXPR_x64_ENU.exe à côté de ce programme d'installation.
english.SqlSourceDownload=No offline media next to the setup program: SQL Server Express will be downloaded from Microsoft (about 300 MB, Internet connection required). To install without Internet, put SQLEXPR_x64_ENU.exe next to this setup program.

french.LicPageCaption=Licence de Microsoft SQL Server Express
english.LicPageCaption=Microsoft SQL Server Express licence
french.LicPageDescription=Lisez les termes de la licence Microsoft avant d'installer SQL Server Express.
english.LicPageDescription=Read the Microsoft licence terms before installing SQL Server Express.
french.LicText=SQL Server Express est un logiciel de Microsoft, distribué sous les « Termes du contrat de licence logiciel Microsoft SQL Server ». MarocLIS ne le modifie pas et ne le redistribue pas : il est installé tel que Microsoft le fournit.%n%nL'installation se fait sans les écrans de Microsoft ; c'est donc ici que vous acceptez sa licence. Le texte complet est publié par Microsoft à l'adresse ci-dessous ; lisez-le avant de continuer.%n%nEn installant SQL Server, vous acceptez aussi que Microsoft recueille les données décrites dans sa déclaration de confidentialité ({#SqlPrivacyUrl}) ; elles se règlent ensuite dans SQL Server.
english.LicText=SQL Server Express is Microsoft software, distributed under the "Microsoft SQL Server Software License Terms". MarocLIS neither modifies nor redistributes it: it is installed as Microsoft supplies it.%n%nSetup installs it without Microsoft's own screens, so this is where you accept its licence. Microsoft publishes the full text at the address below; read it before going on.%n%nBy installing SQL Server you also accept that Microsoft collects the data described in its privacy statement ({#SqlPrivacyUrl}); this is configured afterwards in SQL Server.
french.LicLink=Lire la licence Microsoft SQL Server : {#SqlLicenseUrl}
english.LicLink=Read the Microsoft SQL Server licence: {#SqlLicenseUrl}
french.LicAccept=J'accepte la licence Microsoft SQL Server
english.LicAccept=I accept the Microsoft SQL Server licence
french.LicDecline=Je n'accepte pas (SQL Server ne sera pas installé)
english.LicDecline=I do not accept (SQL Server will not be installed)

french.SrvPageCaption=Serveur du laboratoire
english.SrvPageCaption=Laboratory server
french.SrvPageDescription=Ce poste se connecte à la base MarocLIS du serveur.
english.SrvPageDescription=This workstation connects to the server's MarocLIS database.
french.SrvPageSubCaption=Indiquez le nom de l'ordinateur serveur (celui où « Serveur » a été installé) et l'instance SQL Server. La connexion se fait avec le compte Windows de l'utilisateur, qui doit faire partie du groupe « {#UsersGroup} » du serveur.
english.SrvPageSubCaption=Enter the name of the server computer (where "Server" was installed) and the SQL Server instance. The connection uses the user's Windows account, which must belong to the server's "{#UsersGroup}" group.
french.SrvName=&Nom du serveur :
english.SrvName=Server &name:
french.SrvInstance=&Instance SQL Server :
english.SrvInstance=SQL Server &instance:
french.SrvTest=&Tester la connexion
english.SrvTest=&Test the connection
french.SrvNameRequired=Indiquez le nom du serveur.
english.SrvNameRequired=Enter the server name.
french.SrvNameInvalid=Le nom du serveur ne doit contenir ni espace, ni « \ », ni « ; ». L'instance se saisit dans le champ suivant.
english.SrvNameInvalid=The server name must not contain spaces, "\" or ";". The instance goes in the next field.
french.SrvTestOk=Connexion réussie à la base MarocLIS sur %1 (avec le compte Windows qui exécute l'installation).
english.SrvTestOk=Connected to the MarocLIS database on %1 (with the Windows account running setup).
french.SrvTestFailed=Connexion impossible à %1 :%n%n%2%n%nVérifiez le nom, que le serveur est allumé, et que ce compte Windows a accès à MarocLIS. Vous pouvez continuer : la chaîne de connexion sera écrite telle quelle.
english.SrvTestFailed=Cannot connect to %1:%n%n%2%n%nCheck the name, that the server is on, and that this Windows account may use MarocLIS. You may continue: the connection string will be written as entered.
french.SrvKept=Ce poste a déjà un fichier appsettings.json : sa chaîne de connexion est conservée.
english.SrvKept=This workstation already has an appsettings.json: its connection string is kept.

french.LicFilePageCaption=Licence MarocLIS
english.LicFilePageCaption=MarocLIS licence
french.LicFilePageDescription=Le fichier de licence reçu de l'éditeur, si vous l'avez déjà.
english.LicFilePageDescription=The licence file received from the vendor, if you already have it.
french.LicFilePageSubCaption=Si vous avez reçu un fichier de licence (.lic), indiquez-le : il est copié à côté de MarocLIS et importé au premier démarrage. Sinon laissez vide : au premier démarrage, MarocLIS affiche l'identifiant du serveur et exporte la demande à envoyer à l'éditeur, puis la licence s'importe depuis Paramètres › Licence.
english.LicFilePageSubCaption=If you received a licence file (.lic), select it: it is copied beside MarocLIS and imported on first start. Otherwise leave it empty: on first start, MarocLIS shows the server's identifier and exports the request to send to the vendor; the licence is then imported from Settings > Licence.
french.LicFilePrompt=&Fichier de licence (facultatif) :
english.LicFilePrompt=&Licence file (optional):
french.LicFileFilter=Licence MarocLIS (*.lic)|*.lic|Tous les fichiers (*.*)|*.*
english.LicFileFilter=MarocLIS licence (*.lic)|*.lic|All files (*.*)|*.*
french.LicFileInvalid=Ce fichier n'est pas une licence MarocLIS (un fichier .lic de moins de 64 Ko).
english.LicFileInvalid=This file is not a MarocLIS licence (a .lic file under 64 KB).
french.ReadyLicence=Licence : %1, importée au premier démarrage
english.ReadyLicence=Licence: %1, imported on first start
french.ReadyNoLicence=Licence : à importer au premier démarrage (Paramètres › Licence)
english.ReadyNoLicence=Licence: to import on first start (Settings > Licence)
french.PostLicence=Copie du fichier de licence…
english.PostLicence=Copying the licence file…

french.ProgressCaption=Installation de SQL Server Express
english.ProgressCaption=Installing SQL Server Express
french.ProgressDescription=Cette étape peut durer de 10 à 30 minutes. Ne fermez pas cette fenêtre.
english.ProgressDescription=This step can take 10 to 30 minutes. Do not close this window.
french.ProgressDownloadMedia=Téléchargement de SQL Server Express depuis Microsoft…
english.ProgressDownloadMedia=Downloading SQL Server Express from Microsoft…
french.ProgressExtract=Extraction du support d'installation…
english.ProgressExtract=Extracting the installation media…
french.ProgressSetup=Installation de SQL Server Express (instance SQLEXPRESS)…
english.ProgressSetup=Installing SQL Server Express (SQLEXPRESS instance)…
french.SqlDownloadFailed=Le téléchargement de SQL Server Express a échoué : %1%n%nVérifiez la connexion Internet, ou placez SQLEXPR_x64_ENU.exe à côté du programme d'installation et relancez-le.
english.SqlDownloadFailed=Downloading SQL Server Express failed: %1%n%nCheck the Internet connection, or put SQLEXPR_x64_ENU.exe next to the setup program and run it again.
french.SqlMediaNotFound=Le support d'installation de SQL Server Express est introuvable après le téléchargement.
english.SqlMediaNotFound=The SQL Server Express installation media was not found after downloading.
french.SqlSetupNoInstance=L'installation de SQL Server Express s'est terminée, mais aucune instance SQL Server n'est enregistrée sur cet ordinateur. Le détail est dans le journal de SQL Server : %1.
english.SqlSetupNoInstance=SQL Server Express setup ended, but no SQL Server instance is registered on this computer. See SQL Server's setup log: %1.
french.SqlSetupFailed=L'installation de SQL Server Express a échoué (code %1). Le détail est dans le journal de SQL Server : %2.
english.SqlSetupFailed=SQL Server Express setup failed (code %1). See SQL Server's setup log: %2.

french.PostConnection=Écriture de la chaîne de connexion…
english.PostConnection=Writing the connection string…
french.PostServer=Base de données, accès et pare-feu…
english.PostServer=Database, access and firewall…
french.PostService=Service MarocLIS.Api…
english.PostService=MarocLIS.Api service…
french.PostStopService=Arrêt du service MarocLIS.Api…
english.PostStopService=Stopping the MarocLIS.Api service…
french.StepFailed=Étape non terminée : %1. Voir le journal %2.
english.StepFailed=Step not completed: %1. See the log %2.
french.StepWarning=Étape terminée avec un avertissement : %1. Voir le journal %2.
english.StepWarning=Step completed with a warning: %1. See the log %2.

french.ReadyType=Type d'installation :
english.ReadyType=Setup type:
french.ReadySqlInstall=SQL Server : installation de SQL Server 2025 Express, instance SQLEXPRESS
english.ReadySqlInstall=SQL Server: install SQL Server 2025 Express, SQLEXPRESS instance
french.ReadySqlExisting=SQL Server : instance existante %1
english.ReadySqlExisting=SQL Server: existing instance %1
french.ReadySourceOffline=depuis le support hors ligne %1
english.ReadySourceOffline=from the offline media %1
french.ReadySourceDownload=téléchargé depuis Microsoft (connexion Internet nécessaire)
english.ReadySourceDownload=downloaded from Microsoft (Internet connection required)
french.ReadyConnection=Chaîne de connexion :
english.ReadyConnection=Connection string:
french.ReadyService=Service Windows MarocLIS.Api :%n%1compte LocalSystem, http://127.0.0.1:5080, démarrage automatique différé
english.ReadyService=MarocLIS.Api Windows service:%n%1LocalSystem account, http://127.0.0.1:5080, delayed automatic start
french.ReadyUpgrade=Mise à jour : le service est arrêté pendant la copie puis redémarré ; les fichiers appsettings.json existants sont conservés.
english.ReadyUpgrade=Upgrade: the service is stopped while files are copied, then restarted; existing appsettings.json files are kept.

french.FinishedServer=MarocLIS est installé sur ce serveur.%n%nAu premier démarrage, MarocLIS crée les tables de la base (identifiant « admin », mot de passe initial « Maroc-LIS-2026! », à changer aussitôt).%n%nPostes clients : ajoutez les comptes Windows des utilisateurs au groupe local « {#UsersGroup} » de ce serveur (en groupe de travail, créez sur le serveur un compte de même nom et même mot de passe que sur le poste), puis installez « Poste client » sur chaque poste.%n%nValidation depuis le téléphone : installez Tailscale sur ce serveur puis exécutez « tailscale serve --bg 5080 » (manuel, Administration › Service de validation à distance).
english.FinishedServer=MarocLIS is installed on this server.%n%nOn its first start MarocLIS creates the database tables (user "admin", initial password "Maroc-LIS-2026!", to be changed at once).%n%nWorkstations: add the users' Windows accounts to this server's local group "{#UsersGroup}" (in a workgroup, create on the server an account with the same name and password as on the workstation), then install "Workstation" on each one.%n%nValidation from the phone: install Tailscale on this server, then run "tailscale serve --bg 5080" (manual, Administration > Remote validation service).
french.FinishedClient=MarocLIS est installé sur ce poste. Il utilise la base du serveur indiqué ; l'utilisateur Windows doit faire partie du groupe « {#UsersGroup} » du serveur.
english.FinishedClient=MarocLIS is installed on this workstation. It uses the database of the server entered; the Windows user must belong to the server's "{#UsersGroup}" group.
french.FinishedUpgradeNote=Mise à jour : lancez d'abord MarocLIS sur un poste (il met la base à jour), puis redémarrez le service MarocLIS.Api.
english.FinishedUpgradeNote=Upgrade: start MarocLIS on a workstation first (it updates the database), then restart the MarocLIS.Api service.
french.FinishedWarnings=À vérifier :
english.FinishedWarnings=To check:

[Code]
const
  SqlInstancesKey = 'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL';
  SqlInstallInstance = 'SQLEXPRESS';
  SQL_SETUP_REBOOT = 3010;

var
  SqlInstances: TArrayOfString;      { instance names found in the registry }
  SqlInstance: String;               { instance MarocLIS uses on the server }
  SqlOfflineMedia: String;           { setup.exe or SQLEXPR*.exe next to setup, if any }
  SqlRebootNeeded: Boolean;

  SqlPage: TWizardPage;
  SqlInfoLabel: TNewStaticText;
  SqlInstanceCombo: TNewComboBox;
  SqlNoteLabel: TNewStaticText;

  LicensePage: TWizardPage;
  LicenseAccept: TNewRadioButton;
  LicenseDecline: TNewRadioButton;

  ServerPage: TInputQueryWizardPage;
  LicenceFilePage: TInputFileWizardPage;
  ServerKeptLabel: TNewStaticText;

  TypeDetailLabel: TNewStaticText;
  TypesComboChange: TNotifyEvent;

  DownloadPage: TDownloadWizardPage;
  ProgressPage: TOutputProgressWizardPage;

  ServiceExisted: Boolean;
  DesktopSettingsExisted: Boolean;
  ApiSettingsExisted: Boolean;
  HelperPath: String;
  LogPath: String;
  StepMessages: String;

{ ---------------------------------------------------------------------------------------------- }
{ Detection                                                                                       }
{ ---------------------------------------------------------------------------------------------- }

function IsServer: Boolean;
begin
  Result := WizardSetupType(False) = 'server';
end;

function SqlInstalled: Boolean;
begin
  Result := GetArrayLength(SqlInstances) > 0;
end;

function MustInstallSql: Boolean;
begin
  Result := IsServer and not SqlInstalled;
end;

procedure DetectSqlInstances;
var
  I: Integer;
begin
  if not RegGetValueNames(HKLM64, SqlInstancesKey, SqlInstances) then
    SetArrayLength(SqlInstances, 0);
  SqlInstance := SqlInstallInstance;
  if SqlInstalled then begin
    { SQLEXPRESS, as the shipped connection strings say, when it is there; else the first one. }
    SqlInstance := SqlInstances[0];
    for I := 0 to GetArrayLength(SqlInstances) - 1 do
      if CompareText(SqlInstances[I], SqlInstallInstance) = 0 then
        SqlInstance := SqlInstances[I];
  end;
end;

function FindFirstMatch(const Pattern: String): String;
var
  Rec: TFindRec;
begin
  Result := '';
  if FindFirst(Pattern, Rec) then begin
    try
      repeat
        if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then begin
          Result := AddBackslash(ExtractFileDir(Pattern)) + Rec.Name;
          Exit;
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

{ Searches Folder and its subfolders (depth-limited) for the first file matching Name. }
function FindFileBelow(const Folder, Name: String; Depth: Integer): String;
var
  Rec: TFindRec;
begin
  Result := FindFirstMatch(AddBackslash(Folder) + Name);
  if (Result <> '') or (Depth <= 0) then Exit;
  if FindFirst(AddBackslash(Folder) + '*', Rec) then begin
    try
      repeat
        if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Rec.Name <> '.') and (Rec.Name <> '..') then begin
          Result := FindFileBelow(AddBackslash(Folder) + Rec.Name, Name, Depth - 1);
          if Result <> '' then Exit;
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

{ Offline media next to setup.exe: an extracted folder (SQLEXPR\setup.exe or
  SQLEXPR_x64_ENU\setup.exe) or Microsoft's self-extracting package (SQLEXPR*.exe). }
procedure DetectOfflineMedia;
var
  Src: String;
begin
  Src := ExpandConstant('{src}');
  SqlOfflineMedia := '';
  if FileExists(Src + '\SQLEXPR\setup.exe') then
    SqlOfflineMedia := Src + '\SQLEXPR\setup.exe'
  else if FileExists(Src + '\SQLEXPR_x64_ENU\setup.exe') then
    SqlOfflineMedia := Src + '\SQLEXPR_x64_ENU\setup.exe'
  else
    SqlOfflineMedia := FindFirstMatch(Src + '\SQLEXPR*.exe');
end;

{ The media as the user put it: SQLEXPR_x64_ENU.exe, or SQLEXPR\setup.exe for an unpacked one. }
function OfflineMediaName: String;
begin
  Result := ExtractFileName(SqlOfflineMedia);
  if CompareText(Result, 'setup.exe') = 0 then
    Result := ExtractFileName(ExtractFileDir(SqlOfflineMedia)) + '\' + Result;
end;

function SqlDataSource(const Server, Instance: String): String;
begin
  if (Instance = '') or (CompareText(Instance, 'MSSQLSERVER') = 0) then
    Result := Server
  else
    Result := Server + '\' + Instance;
end;

function ClientDataSource: String;
begin
  Result := SqlDataSource(Trim(ServerPage.Values[0]), Trim(ServerPage.Values[1]));
end;

function ServerDataSource: String;
begin
  Result := SqlDataSource('localhost', SqlInstance);
end;

function ConnectionStringFor(const DataSource: String): String;
begin
  Result := 'Server=' + DataSource + ';Database={#DatabaseName};Integrated Security=true';
end;

{ ---------------------------------------------------------------------------------------------- }
{ Running the helper script                                                                       }
{ ---------------------------------------------------------------------------------------------- }

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function Quote(const S: String): String;
begin
  Result := '"' + S + '"';
end;

procedure AddStepMessage(const Msg: String);
begin
  StepMessages := StepMessages + #13#10 + '- ' + Msg;
  Log(Msg);
end;

{ Runs maroclis-setup.ps1 -Action <Action> <Args>; 0 done, 3 done with a warning. }
function RunHelper(const Action, Args, StepName: String): Boolean;
var
  ResultCode: Integer;
  Params: String;
begin
  if HelperPath = '' then begin
    ExtractTemporaryFile('maroclis-setup.ps1');
    HelperPath := ExpandConstant('{tmp}\maroclis-setup.ps1');
  end;
  Params := '-NoProfile -ExecutionPolicy Bypass -File ' + Quote(HelperPath) + ' -Action ' + Action +
            ' -LogFile ' + Quote(LogPath) + ' ' + Args;
  Log('Running helper: ' + Params);
  if not Exec(PowerShellExe, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
    AddStepMessage(FmtMessage(CustomMessage('StepFailed'), [StepName + ' (' + SysErrorMessage(ResultCode) + ')', LogPath]));
    Result := False;
    Exit;
  end;
  Log(Format('Helper %s exit code %d', [Action, ResultCode]));
  Result := ResultCode = 0;
  if ResultCode = 3 then begin
    AddStepMessage(FmtMessage(CustomMessage('StepWarning'), [StepName, LogPath]));
    Result := True;
  end
  else if ResultCode <> 0 then
    AddStepMessage(FmtMessage(CustomMessage('StepFailed'), [StepName, LogPath]));
end;

function ServiceInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\{#ApiServiceName}');
end;

{ ---------------------------------------------------------------------------------------------- }
{ Wizard pages                                                                                    }
{ ---------------------------------------------------------------------------------------------- }

procedure LicenseRadioClick(Sender: TObject);
begin
  WizardForm.NextButton.Enabled := LicenseAccept.Checked;
end;

procedure LicenseLinkClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExecAsOriginalUser('open', '{#SqlLicenseUrl}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure TestConnectionClick(Sender: TObject);
var
  Connection: Variant;
  Target: String;
begin
  Target := ClientDataSource;
  try
    Connection := CreateOleObject('ADODB.Connection');
    Connection.ConnectionTimeout := 10;
    Connection.ConnectionString := 'Provider=SQLOLEDB;Data Source=' + Target +
                                   ';Initial Catalog={#DatabaseName};Integrated Security=SSPI;';
    Connection.Open;
    Connection.Close;
    MsgBox(FmtMessage(CustomMessage('SrvTestOk'), [Target]), mbInformation, MB_OK);
  except
    MsgBox(FmtMessage(CustomMessage('SrvTestFailed'), [Target, GetExceptionMessage]), mbError, MB_OK);
  end;
end;

procedure CreateSqlPage;
var
  I: Integer;
begin
  SqlPage := CreateCustomPage(wpSelectComponents, CustomMessage('SqlPageCaption'), CustomMessage('SqlPageDescription'));

  SqlInfoLabel := TNewStaticText.Create(SqlPage);
  SqlInfoLabel.Parent := SqlPage.Surface;
  SqlInfoLabel.AutoSize := False;
  SqlInfoLabel.WordWrap := True;
  SqlInfoLabel.Width := SqlPage.SurfaceWidth;
  SqlInfoLabel.Top := 0;

  SqlInstanceCombo := TNewComboBox.Create(SqlPage);
  SqlInstanceCombo.Parent := SqlPage.Surface;
  SqlInstanceCombo.Style := csDropDownList;
  SqlInstanceCombo.Width := ScaleX(220);

  SqlNoteLabel := TNewStaticText.Create(SqlPage);
  SqlNoteLabel.Parent := SqlPage.Surface;
  SqlNoteLabel.AutoSize := False;
  SqlNoteLabel.WordWrap := True;
  SqlNoteLabel.Width := SqlPage.SurfaceWidth;

  if SqlInstalled then begin
    SqlInfoLabel.Caption := CustomMessage('SqlFound');
    SqlInfoLabel.Height := ScaleY(32);
    SqlInstanceCombo.Top := SqlInfoLabel.Top + SqlInfoLabel.Height + ScaleY(4);
    for I := 0 to GetArrayLength(SqlInstances) - 1 do begin
      SqlInstanceCombo.Items.Add(SqlInstances[I]);
      if SqlInstances[I] = SqlInstance then SqlInstanceCombo.ItemIndex := I;
    end;
    SqlNoteLabel.Top := SqlInstanceCombo.Top + SqlInstanceCombo.Height + ScaleY(12);
    SqlNoteLabel.Height := SqlPage.SurfaceHeight - SqlNoteLabel.Top;
    SqlNoteLabel.Caption := CustomMessage('SqlFoundNote');
  end else begin
    SqlInstanceCombo.Visible := False;
    SqlInfoLabel.Caption := CustomMessage('SqlMissing');
    SqlInfoLabel.Height := ScaleY(150);
    SqlNoteLabel.Top := SqlInfoLabel.Top + SqlInfoLabel.Height + ScaleY(8);
    SqlNoteLabel.Height := SqlPage.SurfaceHeight - SqlNoteLabel.Top;
    if SqlOfflineMedia <> '' then
      SqlNoteLabel.Caption := FmtMessage(CustomMessage('SqlSourceOffline'), [OfflineMediaName])
    else
      SqlNoteLabel.Caption := CustomMessage('SqlSourceDownload');
  end;
end;

procedure CreateLicensePage;
var
  Memo: TNewMemo;
  Link: TNewStaticText;
begin
  LicensePage := CreateCustomPage(SqlPage.ID, CustomMessage('LicPageCaption'), CustomMessage('LicPageDescription'));

  Memo := TNewMemo.Create(LicensePage);
  Memo.Parent := LicensePage.Surface;
  Memo.ReadOnly := True;
  Memo.ScrollBars := ssVertical;
  Memo.WordWrap := True;
  Memo.Width := LicensePage.SurfaceWidth;
  Memo.Height := LicensePage.SurfaceHeight - ScaleY(80);
  Memo.Text := CustomMessage('LicText');

  Link := TNewStaticText.Create(LicensePage);
  Link.Parent := LicensePage.Surface;
  Link.Top := Memo.Top + Memo.Height + ScaleY(6);
  Link.AutoSize := False;
  Link.Width := LicensePage.SurfaceWidth;
  Link.Height := ScaleY(16);
  Link.Caption := CustomMessage('LicLink');
  Link.Cursor := crHand;
  Link.Font.Color := clBlue;
  Link.Font.Style := [fsUnderline];
  Link.OnClick := @LicenseLinkClick;

  LicenseAccept := TNewRadioButton.Create(LicensePage);
  LicenseAccept.Parent := LicensePage.Surface;
  LicenseAccept.Top := Link.Top + ScaleY(22);
  LicenseAccept.Width := LicensePage.SurfaceWidth;
  LicenseAccept.Caption := CustomMessage('LicAccept');
  LicenseAccept.OnClick := @LicenseRadioClick;

  LicenseDecline := TNewRadioButton.Create(LicensePage);
  LicenseDecline.Parent := LicensePage.Surface;
  LicenseDecline.Top := LicenseAccept.Top + ScaleY(20);
  LicenseDecline.Width := LicensePage.SurfaceWidth;
  LicenseDecline.Caption := CustomMessage('LicDecline');
  LicenseDecline.Checked := True;
  LicenseDecline.OnClick := @LicenseRadioClick;
end;

procedure CreateServerPage;
var
  TestButton: TNewButton;
begin
  ServerPage := CreateInputQueryPage(LicensePage.ID, CustomMessage('SrvPageCaption'),
    CustomMessage('SrvPageDescription'), CustomMessage('SrvPageSubCaption'));
  ServerPage.Add(CustomMessage('SrvName'), False);
  ServerPage.Add(CustomMessage('SrvInstance'), False);
  ServerPage.Values[0] := GetPreviousData('ServerName', '');
  ServerPage.Values[1] := GetPreviousData('ServerInstance', SqlInstallInstance);

  TestButton := TNewButton.Create(ServerPage);
  TestButton.Parent := ServerPage.Surface;
  TestButton.Caption := CustomMessage('SrvTest');
  TestButton.Width := ScaleX(160);
  TestButton.Height := WizardForm.NextButton.Height;
  TestButton.Top := ServerPage.Edits[1].Top + ServerPage.Edits[1].Height + ScaleY(12);
  TestButton.OnClick := @TestConnectionClick;

  ServerKeptLabel := TNewStaticText.Create(ServerPage);
  ServerKeptLabel.Parent := ServerPage.Surface;
  ServerKeptLabel.AutoSize := False;
  ServerKeptLabel.WordWrap := True;
  ServerKeptLabel.Width := ServerPage.SurfaceWidth;
  ServerKeptLabel.Height := ScaleY(32);
  ServerKeptLabel.Top := TestButton.Top + TestButton.Height + ScaleY(12);
  ServerKeptLabel.Caption := CustomMessage('SrvKept');
  ServerKeptLabel.Visible := False;
end;

function DesktopSettingsPresent: Boolean;
begin
  Result := FileExists(AddBackslash(WizardDirValue) + 'Desktop\appsettings.json');
end;

procedure UpdateTypeDetail;
begin
  if IsServer then
    TypeDetailLabel.Caption := CustomMessage('TypeServerDetail')
  else
    TypeDetailLabel.Caption := CustomMessage('TypeClientDetail');
end;

procedure TypesComboChanged(Sender: TObject);
begin
  if TypesComboChange <> nil then TypesComboChange(Sender);
  UpdateTypeDetail;
end;

{ The components page is used as a setup-type page: the two types are the choice, and the
  component list (all fixed per type) is replaced by what the chosen type installs. }
procedure CustomizeTypePage;
begin
  WizardForm.ComponentsList.Visible := False;
  WizardForm.SelectComponentsLabel.Caption := CustomMessage('TypePageLabel');
  TypeDetailLabel := TNewStaticText.Create(WizardForm);
  TypeDetailLabel.Parent := WizardForm.SelectComponentsPage;
  TypeDetailLabel.AutoSize := False;
  TypeDetailLabel.WordWrap := True;
  TypeDetailLabel.Left := WizardForm.ComponentsList.Left;
  TypeDetailLabel.Top := WizardForm.TypesCombo.Top + WizardForm.TypesCombo.Height + ScaleY(14);
  TypeDetailLabel.Width := WizardForm.ComponentsList.Width;
  TypeDetailLabel.Height := WizardForm.ComponentsDiskSpaceLabel.Top - TypeDetailLabel.Top - ScaleY(6);
  TypesComboChange := WizardForm.TypesCombo.OnChange;
  WizardForm.TypesCombo.OnChange := @TypesComboChanged;
end;

{ The licence file, optional: MarocLIS imports a licence.lic it finds beside itself on its first
  start, on an installation that has none yet. Most licences are bound to the server and asked for
  once it is installed, so the page is usually left empty. }
procedure CreateLicenceFilePage;
begin
  LicenceFilePage := CreateInputFilePage(ServerPage.ID, CustomMessage('LicFilePageCaption'),
    CustomMessage('LicFilePageDescription'), CustomMessage('LicFilePageSubCaption'));
  LicenceFilePage.Add(CustomMessage('LicFilePrompt'), CustomMessage('LicFileFilter'), '.lic');
end;

function LicenceFileChosen: String;
begin
  Result := Trim(LicenceFilePage.Values[0]);
end;

procedure InitializeWizard;
begin
  DetectSqlInstances;
  DetectOfflineMedia;
  CustomizeTypePage;
  CreateSqlPage;
  CreateLicensePage;
  CreateServerPage;
  CreateLicenceFilePage;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), CustomMessage('ProgressDownloadMedia'), nil);
  ProgressPage := CreateOutputProgressPage(CustomMessage('ProgressCaption'), CustomMessage('ProgressDescription'));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = SqlPage.ID then
    Result := not IsServer
  else if PageID = LicensePage.ID then
    Result := not MustInstallSql
  else if PageID = ServerPage.ID then
    Result := IsServer;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectComponents then begin
    WizardForm.PageNameLabel.Caption := CustomMessage('TypePageCaption');
    WizardForm.PageDescriptionLabel.Caption := CustomMessage('TypePageDescription');
    UpdateTypeDetail;
  end
  else if CurPageID = LicensePage.ID then
    WizardForm.NextButton.Enabled := LicenseAccept.Checked
  else if CurPageID = ServerPage.ID then begin
    { An existing appsettings.json is kept as it is: say so rather than ask for a name that
      would not be written. }
    ServerKeptLabel.Visible := DesktopSettingsPresent;
    ServerPage.Edits[0].Enabled := not DesktopSettingsPresent;
    ServerPage.Edits[1].Enabled := not DesktopSettingsPresent;
  end
  else if CurPageID = wpFinished then begin
    if IsServer then
      WizardForm.FinishedLabel.Caption := CustomMessage('FinishedServer')
    else
      WizardForm.FinishedLabel.Caption := CustomMessage('FinishedClient');
    if ServiceExisted then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 + CustomMessage('FinishedUpgradeNote');
    if StepMessages <> '' then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
        CustomMessage('FinishedWarnings') + StepMessages;
    WizardForm.FinishedLabel.AutoSize := False;
    WizardForm.FinishedLabel.Height := WizardForm.FinishedPage.Height - WizardForm.FinishedLabel.Top - ScaleY(60);
    WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(4);
  end;
end;

function ValidServerName(const S: String): Boolean;
begin
  Result := (Pos(' ', S) = 0) and (Pos('\', S) = 0) and (Pos(';', S) = 0) and (Pos('"', S) = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = SqlPage.ID then begin
    if SqlInstalled and (SqlInstanceCombo.ItemIndex >= 0) then
      SqlInstance := SqlInstanceCombo.Items[SqlInstanceCombo.ItemIndex];
  end
  else if (CurPageID = ServerPage.ID) and not DesktopSettingsPresent then begin
    if Trim(ServerPage.Values[0]) = '' then begin
      MsgBox(CustomMessage('SrvNameRequired'), mbError, MB_OK);
      Result := False;
    end
    else if not ValidServerName(Trim(ServerPage.Values[0])) or not ValidServerName(Trim(ServerPage.Values[1])) then begin
      MsgBox(CustomMessage('SrvNameInvalid'), mbError, MB_OK);
      Result := False;
    end;
  end
  else if (CurPageID = LicenceFilePage.ID) and (LicenceFileChosen <> '') then begin
    { MarocLIS refuses a file over 64 KB or that is not a licence; said here rather than at first start. }
    if not FileExists(LicenceFileChosen) or (CompareText(ExtractFileExt(LicenceFileChosen), '.lic') <> 0) then begin
      MsgBox(CustomMessage('LicFileInvalid'), mbError, MB_OK);
      Result := False;
    end;
  end
  else if (CurPageID = wpReady) and MustInstallSql and (SqlOfflineMedia = '') then begin
    { Microsoft's bootstrapper is small; it downloads the media itself in PrepareToInstall. }
    DownloadPage.Clear;
    DownloadPage.Add('{#SqlExpressBootstrapperUrl}', '{#SqlExpressBootstrapperName}', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Log('Download aborted by the user.')
        else
          SuppressibleMsgBox(FmtMessage(CustomMessage('SqlDownloadFailed'), [GetExceptionMessage]), mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'ServerName', Trim(ServerPage.Values[0]));
  SetPreviousData(PreviousDataKey, 'ServerInstance', Trim(ServerPage.Values[1]));
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S, Source: String;
begin
  S := MemoDirInfo + NewLine + NewLine + MemoTypeInfo + NewLine + NewLine;
  if IsServer then begin
    if MustInstallSql then begin
      if SqlOfflineMedia <> '' then Source := FmtMessage(CustomMessage('ReadySourceOffline'), [OfflineMediaName])
      else Source := CustomMessage('ReadySourceDownload');
      S := S + CustomMessage('ReadySqlInstall') + NewLine + Space + Source + NewLine;
    end else
      S := S + FmtMessage(CustomMessage('ReadySqlExisting'), [SqlInstance]) + NewLine;
    S := S + CustomMessage('ReadyConnection') + NewLine + Space + ConnectionStringFor(ServerDataSource) + NewLine;
    S := S + FmtMessage(CustomMessage('ReadyService'), [Space]) + NewLine;
  end else if not DesktopSettingsPresent then
    S := S + CustomMessage('ReadyConnection') + NewLine + Space + ConnectionStringFor(ClientDataSource) + NewLine
  else
    S := S + CustomMessage('SrvKept') + NewLine;
  if LicenceFileChosen <> '' then
    S := S + FmtMessage(CustomMessage('ReadyLicence'), [ExtractFileName(LicenceFileChosen)]) + NewLine
  else
    S := S + CustomMessage('ReadyNoLicence') + NewLine;
  if ServiceInstalled or DesktopSettingsPresent then
    S := S + NewLine + CustomMessage('ReadyUpgrade') + NewLine;
  if MemoTasksInfo <> '' then
    S := S + NewLine + MemoTasksInfo + NewLine;
  Result := S;
end;

{ ---------------------------------------------------------------------------------------------- }
{ SQL Server Express                                                                              }
{ ---------------------------------------------------------------------------------------------- }

function SqlSetupLogHint: String;
begin
  Result := ExpandConstant('{commonpf64}\Microsoft SQL Server\<version>\Setup Bootstrap\Log\Summary.txt');
end;

{ Returns setup.exe of the media, downloading or extracting it first; '' with Error set if not. }
function PrepareSqlMedia(var Error: String): String;
var
  WorkDir, Package, Bootstrapper: String;
  ResultCode: Integer;
begin
  Result := '';
  WorkDir := ExpandConstant('{tmp}\sqlexpress');
  ForceDirectories(WorkDir);

  Package := '';
  if SqlOfflineMedia <> '' then begin
    if CompareText(ExtractFileName(SqlOfflineMedia), 'setup.exe') = 0 then begin
      Result := SqlOfflineMedia;
      Exit;
    end;
    Package := SqlOfflineMedia;
  end else begin
    { SQL2025-SSEI-Expr.exe /Action=Download /MediaType=Core: the engine-only package. }
    ProgressPage.SetText(CustomMessage('ProgressDownloadMedia'), '');
    Bootstrapper := ExpandConstant('{tmp}\{#SqlExpressBootstrapperName}');
    if not Exec(Bootstrapper, '/Action=Download /MediaType=Core /MediaPath=' + Quote(WorkDir + '\media') + ' /Quiet',
                '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then begin
      Error := FmtMessage(CustomMessage('SqlDownloadFailed'), [IntToStr(ResultCode)]);
      Exit;
    end;
    Package := FindFileBelow(WorkDir + '\media', 'SQLEXPR*.exe', 2);
    if Package = '' then begin
      Result := FindFileBelow(WorkDir + '\media', 'setup.exe', 2);
      if Result = '' then Error := CustomMessage('SqlMediaNotFound');
      Exit;
    end;
  end;

  { Microsoft's package is a self-extracting archive: /q /x:<folder> unpacks setup.exe. }
  ProgressPage.SetText(CustomMessage('ProgressExtract'), Package);
  if Exec(Package, '/q /x:' + Quote(WorkDir + '\setup'), '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
     and FileExists(WorkDir + '\setup\setup.exe') then
    Result := WorkDir + '\setup\setup.exe'
  else
    { The package also passes setup parameters through when it cannot be unpacked first. }
    Result := Package;
end;

function InstallSqlExpress: String;
var
  SetupExe, Params, Error: String;
  ResultCode: Integer;
begin
  Result := '';
  ProgressPage.Show;
  try
    { The bar only shows once a progress has been set; the step has no measurable progress. }
    ProgressPage.SetProgress(0, 100);
    ProgressPage.ProgressBar.Style := npbstMarquee;
    SetupExe := PrepareSqlMedia(Error);
    if SetupExe = '' then begin
      Result := Error;
      Exit;
    end;

    { Switches as documented in "Install and configure SQL Server on Windows from the command
      prompt" (learn.microsoft.com). The licence was accepted on the wizard's licence page. }
    Params := '/QS /ACTION=Install /FEATURES=SQLEngine /INSTANCENAME=' + SqlInstallInstance +
              ' /SQLSVCACCOUNT="NT Service\MSSQL$' + SqlInstallInstance + '"' +
              ' /SQLSVCSTARTUPTYPE=Automatic /BROWSERSVCSTARTUPTYPE=Automatic /TCPENABLED=1' +
              ' /SQLSYSADMINACCOUNTS="BUILTIN\Administrators"' +
              ' /UpdateEnabled=False /IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE';
    { English media on a French Windows refuses to install without /ENU. }
    if (Pos('_ENU', Uppercase(SetupExe)) > 0) or DirExists(ExtractFileDir(SetupExe) + '\1033_ENU_LP') then
      Params := Params + ' /ENU';

    ProgressPage.SetText(CustomMessage('ProgressSetup'), '');
    Log('SQL Server setup: ' + SetupExe + ' ' + Params);
    if not Exec(SetupExe, Params, ExtractFileDir(SetupExe), SW_SHOW, ewWaitUntilTerminated, ResultCode) then begin
      Result := FmtMessage(CustomMessage('SqlSetupFailed'), [SysErrorMessage(ResultCode), SqlSetupLogHint]);
      Exit;
    end;
    Log(Format('SQL Server setup exit code %d', [ResultCode]));
    if ResultCode = SQL_SETUP_REBOOT then
      SqlRebootNeeded := True
    else if ResultCode <> 0 then begin
      Result := FmtMessage(CustomMessage('SqlSetupFailed'), [IntToStr(ResultCode), SqlSetupLogHint]);
      Exit;
    end;

    DetectSqlInstances;
    if not SqlInstalled then
      Result := FmtMessage(CustomMessage('SqlSetupNoInstance'), [SqlSetupLogHint])
    else
      SqlInstance := SqlInstallInstance;
  finally
    ProgressPage.Hide;
  end;
end;

{ ---------------------------------------------------------------------------------------------- }
{ Install steps                                                                                   }
{ ---------------------------------------------------------------------------------------------- }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  LogPath := AddBackslash(WizardDirValue) + 'Installer\install.log';
  DesktopSettingsExisted := FileExists(AddBackslash(WizardDirValue) + 'Desktop\appsettings.json');
  ApiSettingsExisted := FileExists(AddBackslash(WizardDirValue) + 'Api\appsettings.json');
  ServiceExisted := ServiceInstalled;

  if MustInstallSql then begin
    Result := InstallSqlExpress;
    if Result <> '' then Exit;
  end;

  { Upgrade: the service holds its files open. }
  if ServiceExisted then begin
    WizardForm.PreparingLabel.Caption := CustomMessage('PostStopService');
    RunHelper('StopService', '', CustomMessage('PostStopService'));
  end;
end;

procedure SetStatus(const Text: String);
begin
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.FilenameLabel.Caption := '';
end;

{ Runs on the installing page, after the files are in place. }
procedure ConfigureAfterInstall;
begin
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    SetStatus(CustomMessage('PostConnection'));
    if IsServer then begin
      if not DesktopSettingsExisted then
        RunHelper('SetConnectionString', '-File ' + Quote(ExpandConstant('{app}\Desktop\appsettings.json')) +
                  ' -Server ' + Quote(ServerDataSource), CustomMessage('PostConnection'));
      if not ApiSettingsExisted then
        RunHelper('SetConnectionString', '-File ' + Quote(ExpandConstant('{app}\Api\appsettings.json')) +
                  ' -Server ' + Quote(ServerDataSource), CustomMessage('PostConnection'));

      SetStatus(CustomMessage('PostServer'));
      RunHelper('ConfigureServer', '-Instance ' + Quote(SqlInstance) + ' -UsersGroup ' + Quote('{#UsersGroup}') +
                ' -Database ' + Quote('{#DatabaseName}'), CustomMessage('PostServer'));

      SetStatus(CustomMessage('PostService'));
      if ServiceExisted then
        RunHelper('StartService', '', CustomMessage('PostService'))
      else
        RunHelper('InstallService', '-ApiDirectory ' + Quote(ExpandConstant('{app}\Api')), CustomMessage('PostService'));
    end
    else if not DesktopSettingsExisted then
      RunHelper('SetConnectionString', '-File ' + Quote(ExpandConstant('{app}\Desktop\appsettings.json')) +
                ' -Server ' + Quote(ClientDataSource), CustomMessage('PostConnection'));
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

procedure CopyLicenceFile;
begin
  if LicenceFileChosen = '' then exit;
  WizardForm.StatusLabel.Caption := CustomMessage('PostLicence');
  if not CopyFile(LicenceFileChosen, ExpandConstant('{app}\Desktop\licence.lic'), False) then
    StepMessages := StepMessages + #13#10 + '- ' + CustomMessage('PostLicence');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    CopyLicenceFile;
    ConfigureAfterInstall;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := SqlRebootNeeded;
end;
