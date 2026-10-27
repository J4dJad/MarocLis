; MarocLIS setup program (Inno Setup 6).
;
; One setup.exe, three setup types:
;   Serveur        (Windows 10, 11 or Server, x64) SQL Server Express (instance SQLEXPRESS) when no
;                  SQL Server is installed, the workstation application, the MarocLIS.Api Windows
;                  service that the phones reach through Tailscale (docs/manuel/10-administration.md,
;                  « Service de validation à distance »), and the MarocLIS.Web Windows service:
;                  MarocLIS in the browsers of the local network, https://<server>:5443, with the
;                  server's own self-signed certificate (scripts/install-web-service.ps1).
;   Poste Windows  (Windows 10 or 11, x64) the workstation application only, pointed at the
;                  server's SQL Server.
;   Poste Web      (Windows 7 SP1, 8, 8.1, 10, 11, 32 or 64 bits) no MarocLIS program: the server's
;                  certificate trusted by Windows (and Firefox), a browser that can run MarocLIS Web
;                  (Chrome or Edge 109+, Firefox 115+; else Firefox ESR 115, bundled, installed
;                  silently) and a « MarocLIS » shortcut that opens https://<server>:5443/ in it.
;                  Nothing of this type needs PowerShell: Windows 7 only has PowerShell 2.0.
;
; .NET 10, which the Windows application and the services are built on, runs on Windows 10 and
; later only: on Windows 7, 8 and 8.1 the type page preselects « Poste Web » and refuses the other
; two, saying why. Inno Setup 6.7 itself runs on Windows 7 SP1 and later (its MinVersion floor).
;
; Built by installer/build-installer.ps1, which publishes the Desktop, the Api and the Web
; self-contained for win-x64 (no .NET runtime to install) into the staging folder this script
; packages, and fetches the Firefox ESR 115 installer (French, 32 bits: it runs on every
; workstation) into installer\prereqs, its Mozilla signature checked.
;
; The server type also registers the scheduled task "MarocLIS - Sauvegarde" (scripts/maroclis-backup.ps1,
; every 15 minutes as SYSTEM), which backs the database up following the schedule kept in the
; database and changed in MarocLIS (Paramètres › Sauvegardes); the wizard's « Sauvegarde » page
; gives its first values.
;
; Upgrades keep the same AppId: the service is stopped before its files are replaced and started
; again afterwards, and an existing appsettings.json is never overwritten. Uninstalling removes
; the service, the backup task and the program files, never the database, SQL Server nor the
; backup files.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef StagingDir
  #define StagingDir "build\staging"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif
; The Firefox ESR 115 offline installer (Mozilla's own, French, win32) and its version, as
; build-installer.ps1 fetched and checked them.
#ifndef FirefoxSetupFile
  #define FirefoxSetupFile "prereqs\Firefox-Setup-esr115-fr-win32.exe"
#endif
#ifndef FirefoxVersion
  #define FirefoxVersion "115 ESR"
#endif

#define AppName "MarocLIS"
#define ApiServiceName "MarocLIS.Api"
#define WebServiceName "MarocLIS.Web"
#define WebPort "5443"
; Chrome and Edge 109, Firefox 115: the last versions Windows 7, 8 and 8.1 receive, and the oldest
; MarocLIS Web supports (src/MarocLIS.Web/wwwroot/js/compat.js says the same to an older one).
#define MinChromium "109"
#define MinFirefox "115"
#define UsersGroup "MarocLIS-Utilisateurs"
#define DatabaseName "MarocLIS"
#define BackupTaskName "MarocLIS - Sauvegarde"
#define DefaultBackupFolder "C:\MarocLIS\Sauvegardes"
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
; A « Poste Web » may be a 32-bit Windows 7; the other two types need x64 and are refused there.
ArchitecturesAllowed=x86compatible x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 7 SP1 for the « Poste Web »; the types built on .NET 10 check for Windows 10 themselves.
MinVersion=6.1sp1
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
UninstallDisplayIcon={code:UninstallIcon}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
; The order is the combo's: « Poste Web » last, preselected in code on Windows 7, 8 and 8.1.
Name: "server"; Description: "{cm:TypeServer}"
Name: "client"; Description: "{cm:TypeClient}"
Name: "web"; Description: "{cm:TypeWeb}"

[Components]
Name: "sql"; Description: "{cm:CompSql}"; Types: server; Flags: fixed
Name: "desktop"; Description: "{cm:CompDesktop}"; Types: server client; Flags: fixed
Name: "api"; Description: "{cm:CompApi}"; Types: server; Flags: fixed
Name: "web"; Description: "{cm:CompWeb}"; Types: server; Flags: fixed
Name: "webstation"; Description: "{cm:CompWebStation}"; Types: web; Flags: fixed

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Dirs]
; Serilog writes logs\ in the working folder of the application; the operators are not
; administrators.
Name: "{app}\Desktop\logs"; Permissions: users-modify; Components: desktop
Name: "{app}\Api\logs"; Components: api
Name: "{app}\Sauvegarde\logs"; Components: api
Name: "{app}\Web\logs"; Components: web

[Files]
Source: "{#StagingDir}\Desktop\*"; DestDir: "{app}\Desktop"; Excludes: "\appsettings.json,\appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: desktop
Source: "{#StagingDir}\Desktop\appsettings.json"; DestDir: "{app}\Desktop"; Flags: onlyifdoesntexist; Components: desktop
Source: "{#StagingDir}\Api\*"; DestDir: "{app}\Api"; Excludes: "\appsettings.json,\appsettings.Development.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: api
Source: "{#StagingDir}\Api\appsettings.json"; DestDir: "{app}\Api"; Flags: onlyifdoesntexist; Components: api
Source: "{#RepoRoot}\scripts\install-api-service.ps1"; DestDir: "{app}\Api"; Flags: ignoreversion; Components: api
Source: "{#RepoRoot}\scripts\maroclis-backup.ps1"; DestDir: "{app}\Sauvegarde"; Flags: ignoreversion; Components: api
Source: "{#StagingDir}\Web\*"; DestDir: "{app}\Web"; Excludes: "\appsettings.json,\appsettings.Development.json,\certificat-serveur.*"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: web
Source: "{#StagingDir}\Web\appsettings.json"; DestDir: "{app}\Web"; Flags: onlyifdoesntexist; Components: web
Source: "{#RepoRoot}\scripts\install-web-service.ps1"; DestDir: "{app}\Web"; Flags: ignoreversion; Components: web
; The « MarocLIS » shortcut's icon (the web's FlaskConical glyph, Lucide, ISC licence).
Source: "maroclis-web.ico"; DestDir: "{app}"; Flags: ignoreversion; Components: webstation
Source: "maroclis-setup.ps1"; Flags: dontcopy
; Installed by the « Poste Web » type only when no browser of the workstation can run MarocLIS Web.
Source: "{#FirefoxSetupFile}"; DestName: "firefox-esr-setup.exe"; Flags: dontcopy nocompression

[Icons]
Name: "{group}\MarocLIS"; Filename: "{app}\Desktop\MarocLIS.exe"; WorkingDir: "{app}\Desktop"; Components: desktop
Name: "{autodesktop}\MarocLIS"; Filename: "{app}\Desktop\MarocLIS.exe"; WorkingDir: "{app}\Desktop"; Tasks: desktopicon; Components: desktop
; « Poste Web »: the browser chosen (or installed) on the server's address.
Name: "{group}\MarocLIS"; Filename: "{code:WebBrowserExe}"; Parameters: "{code:WebBrowserArgs}"; IconFilename: "{app}\maroclis-web.ico"; Comment: "{code:WebUrl}"; Components: webstation
Name: "{autodesktop}\MarocLIS"; Filename: "{code:WebBrowserExe}"; Parameters: "{code:WebBrowserArgs}"; IconFilename: "{app}\maroclis-web.ico"; Comment: "{code:WebUrl}"; Tasks: desktopicon; Components: webstation

[Run]
Filename: "{app}\Desktop\MarocLIS.exe"; WorkingDir: "{app}\Desktop"; Description: "{cm:LaunchProgram,MarocLIS}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: desktop
Filename: "{code:WebBrowserExe}"; Parameters: "{code:WebBrowserArgs}"; Description: "{cm:LaunchProgram,MarocLIS}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Components: webstation

[UninstallRun]
; Stops and deletes the service; the published files go with the rest of {app}. The database and
; SQL Server are left alone.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Api\install-api-service.ps1"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveApiService"; Components: api
; The backup task goes; the backup files stay where they are, whatever folder they are in.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#BackupTaskName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveBackupTask"; Components: api
; The web service and its firewall rule go; its certificate stays, so a reinstall keeps the « Poste Web »
; workstations that trust it working.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Web\install-web-service.ps1"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveWebService"; Components: web

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Installer"

[CustomMessages]
french.TypeServer=Serveur : base de données, application, version Web, validation mobile
english.TypeServer=Server: database, application, Web version, mobile validation
french.TypeClient=Poste Windows : application MarocLIS (Windows 10 ou 11)
english.TypeClient=Windows workstation: MarocLIS application (Windows 10 or 11)
french.TypeWeb=Poste Web : MarocLIS dans le navigateur (Windows 7 à 11)
english.TypeWeb=Web workstation: MarocLIS in the browser (Windows 7 to 11)
french.CompSql=SQL Server Express (instance SQLEXPRESS), s'il n'est pas déjà installé
english.CompSql=SQL Server Express (SQLEXPRESS instance), unless already installed
french.CompDesktop=Application MarocLIS
english.CompDesktop=MarocLIS application
french.CompApi=Service MarocLIS.Api (validation depuis le téléphone)
english.CompApi=MarocLIS.Api service (validation from the phone)
french.CompWeb=Service MarocLIS.Web (version Web, https://serveur:{#WebPort})
english.CompWeb=MarocLIS.Web service (Web version, https://server:{#WebPort})
french.CompWebStation=Poste Web : certificat du serveur, navigateur et raccourci MarocLIS
english.CompWebStation=Web workstation: server certificate, browser and MarocLIS shortcut

french.TypePageCaption=Type d'installation
english.TypePageCaption=Setup type
french.TypePageDescription=Cet ordinateur est-il le serveur du laboratoire ou un poste de travail ?
english.TypePageDescription=Is this computer the laboratory server or a workstation?
french.TypePageLabel=Installez d'abord « Serveur » sur l'ordinateur qui garde la base, puis, sur chaque autre poste, « Poste Windows » (Windows 10 ou 11) ou « Poste Web » (dès Windows 7).
english.TypePageLabel=Install "Server" first on the computer that keeps the database, then, on each other computer, "Windows workstation" (Windows 10 or 11) or "Web workstation" (from Windows 7).
french.TypeServerDetail=Un seul serveur par laboratoire, sous Windows 10, 11 ou Windows Server (64 bits). Installe :%n%n  - SQL Server Express (instance SQLEXPRESS), sauf si SQL Server est déjà installé ;%n  - l'application MarocLIS, reliée à la base de ce serveur ;%n  - le service Windows MarocLIS.Api, qui sert l'application du téléphone du biologiste (validation à distance, publiée par Tailscale) ;%n  - le service Windows MarocLIS.Web : MarocLIS dans le navigateur des postes, https://<serveur>:{#WebPort}, avec un certificat HTTPS propre à ce serveur ;%n  - le pare-feu Windows ouvert à SQL Server et au port {#WebPort} pour le réseau local.
english.TypeServerDetail=One server per laboratory, on Windows 10, 11 or Windows Server (64-bit). Installs:%n%n  - SQL Server Express (SQLEXPRESS instance), unless SQL Server is already installed;%n  - the MarocLIS application, connected to this server's database;%n  - the MarocLIS.Api Windows service, which serves the biologist's phone application (remote validation, published through Tailscale);%n  - the MarocLIS.Web Windows service: MarocLIS in the workstations' browser, https://<server>:{#WebPort}, with this server's own HTTPS certificate;%n  - the Windows firewall opened to SQL Server and to port {#WebPort} for the local network.
french.TypeClientDetail=Pour chaque poste de travail du laboratoire. Installe l'application MarocLIS seule ; elle utilise la base de données du serveur, dont le nom vous sera demandé.%n%nRien d'autre n'est à installer : l'application contient tout ce qu'il lui faut (.NET compris).
english.TypeClientDetail=For each workstation of the laboratory. Installs the MarocLIS application only; it uses the server's database, whose name you will be asked for.%n%nNothing else needs installing: the application carries everything it needs (.NET included).
french.TypeWebDetail=Pour un poste qui travaille dans le navigateur, quelle que soit sa version de Windows (Windows 7 SP1, 8, 8.1, 10 ou 11). Rien de MarocLIS ne s'exécute sur le poste ; l'installation :%n%n  - fait confiance au certificat HTTPS du serveur, après vérification de son empreinte ;%n  - garde le navigateur du poste s'il convient (Chrome ou Edge {#MinChromium} ou plus, Firefox {#MinFirefox} ou plus), sinon installe Firefox ESR 115 ;%n  - crée le raccourci « MarocLIS », qui ouvre https://<serveur>:{#WebPort}/.%n%nLe serveur doit déjà être installé.
english.TypeWebDetail=For a workstation that works in the browser, whatever its Windows version (Windows 7 SP1, 8, 8.1, 10 or 11). Nothing of MarocLIS runs on the workstation; setup:%n%n  - trusts the server's HTTPS certificate, once its fingerprint is checked;%n  - keeps the workstation's browser if it is suitable (Chrome or Edge {#MinChromium} or later, Firefox {#MinFirefox} or later), else installs Firefox ESR 115;%n  - creates the "MarocLIS" shortcut, which opens https://<server>:{#WebPort}/.%n%nThe server must already be installed.
french.TypeNeedsWindows10=« %1 » a besoin de Windows 10 ou 11 (64 bits) : MarocLIS pour Windows et ses services sont construits sur .NET 10, que Microsoft ne fournit pas pour Windows 7, 8 ni 8.1.%n%nCet ordinateur a %2. Choisissez « Poste Web » : MarocLIS s'ouvrira dans le navigateur, depuis le serveur du laboratoire, avec les mêmes écrans.
english.TypeNeedsWindows10="%1" needs Windows 10 or 11 (64-bit): MarocLIS for Windows and its services are built on .NET 10, which Microsoft does not provide for Windows 7, 8 or 8.1.%n%nThis computer has %2. Choose "Web workstation": MarocLIS will open in the browser, from the laboratory's server, with the same screens.
french.TypeNeedsX64=« %1 » a besoin d'un Windows 64 bits (x64) : MarocLIS pour Windows et ses services sont construits pour x64. Cet ordinateur a un Windows 32 bits : choisissez « Poste Web ».
french.TypeServerShort=Serveur
english.TypeServerShort=Server
french.TypeClientShort=Poste Windows
english.TypeClientShort=Windows workstation
english.TypeNeedsX64="%1" needs a 64-bit Windows (x64): MarocLIS for Windows and its services are built for x64. This computer has a 32-bit Windows: choose "Web workstation".

french.SqlPageCaption=SQL Server
english.SqlPageCaption=SQL Server
french.SqlPageDescription=La base de données MarocLIS est hébergée par SQL Server sur ce serveur.
english.SqlPageDescription=The MarocLIS database is hosted by SQL Server on this server.
french.SqlFound=SQL Server est déjà installé sur cet ordinateur. Instance utilisée par MarocLIS :
english.SqlFound=SQL Server is already installed on this computer. Instance used by MarocLIS:
french.SqlFoundNote=L'installation crée la base « MarocLIS » si elle n'existe pas, donne l'accès au groupe Windows local « {#UsersGroup} » et au service MarocLIS.Api, active SQL Server Browser et ouvre le pare-feu Windows au réseau local. Votre compte doit être administrateur (sysadmin) de cette instance.
english.SqlFoundNote=Setup creates the "MarocLIS" database if it does not exist, grants access to the local Windows group "{#UsersGroup}" and to the MarocLIS.Api service, enables SQL Server Browser and opens the Windows firewall to the local network. Your account must be an administrator (sysadmin) of this instance.
french.SqlMissing=Aucun SQL Server n'est installé sur cet ordinateur. L'installation va installer Microsoft SQL Server 2025 Express :%n%n  - moteur de base de données seul, instance SQLEXPRESS ;%n  - TCP/IP activé et pare-feu ouvert au réseau local, pour les postes Windows ;%n  - les administrateurs de cet ordinateur administrent SQL Server.%n%nSQL Server Express est gratuit ; sa licence Microsoft vous est présentée à la page suivante.
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

french.WebPageCaption=Serveur MarocLIS Web
english.WebPageCaption=MarocLIS Web server
french.WebPageDescription=Ce poste ouvre MarocLIS dans un navigateur, depuis le serveur du laboratoire.
english.WebPageDescription=This workstation opens MarocLIS in a browser, from the laboratory's server.
french.WebPageSubCaption=Indiquez le nom ou l'adresse IP de l'ordinateur où « Serveur » a été installé. L'installation lui demande son certificat HTTPS et vous en montre l'empreinte, à comparer à celle affichée à la fin de l'installation du serveur.
english.WebPageSubCaption=Enter the name or IP address of the computer where "Server" was installed. Setup asks it for its HTTPS certificate and shows you its fingerprint, to compare with the one shown at the end of the server's setup.
french.WebServerName=&Nom ou adresse du serveur :
english.WebServerName=Server &name or address:
french.WebCheck=&Vérifier le serveur
english.WebCheck=&Check the server
french.WebCertFile=&Ou le fichier certificat-serveur.cer copié du serveur (facultatif) :
english.WebCertFile=&Or the certificat-serveur.cer file copied from the server (optional):
french.WebCertFilter=Certificat (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|Tous les fichiers (*.*)|*.*
english.WebCertFilter=Certificate (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|All files (*.*)|*.*
french.WebNameRequired=Indiquez le nom ou l'adresse du serveur.
english.WebNameRequired=Enter the server's name or address.
french.WebNameInvalid=Le nom du serveur ne contient que des lettres, des chiffres, des points et des tirets (par exemple SERVEUR-LABO ou 192.168.1.10), sans « https:// » ni numéro de port.
english.WebNameInvalid=The server name holds only letters, digits, dots and hyphens (for example LAB-SERVER or 192.168.1.10), without "https://" or a port number.
french.WebFetchFailed=Le certificat n'a pas pu être demandé à %1 :%n%n%2%n%nVérifiez le nom, que le serveur est allumé et que le service MarocLIS.Web y fonctionne. Sous Windows 7 ou 8 sans ses mises à jour, la connexion peut échouer : copiez alors le fichier certificat-serveur.cer du serveur (dossier C:\MarocLIS\Web) sur une clé USB et indiquez-le dans le second champ.
english.WebFetchFailed=The certificate could not be requested from %1:%n%n%2%n%nCheck the name, that the server is on and that the MarocLIS.Web service runs there. On a Windows 7 or 8 without its updates the connection may fail: then copy the server's certificat-serveur.cer file (folder C:\MarocLIS\Web) to a USB key and select it in the second field.
french.WebCertFileInvalid=Ce fichier n'est pas un certificat lisible (un fichier .cer de moins de 64 Ko, binaire ou texte « BEGIN CERTIFICATE »).
english.WebCertFileInvalid=This file is not a readable certificate (a .cer file under 64 KB, binary or "BEGIN CERTIFICATE" text).
french.WebConfirmFingerprint=Certificat de %1.%n%nEmpreinte SHA-256 :%n%2%n%nComparez-la, caractère par caractère, à celle affichée à la fin de l'installation du serveur (ou dans son fichier C:\MarocLIS\Web\certificat-serveur.txt).%n%nEst-elle identique ?
english.WebConfirmFingerprint=Certificate of %1.%n%nSHA-256 fingerprint:%n%2%n%nCompare it, character by character, with the one shown at the end of the server's setup (or in its file C:\MarocLIS\Web\certificat-serveur.txt).%n%nIs it the same?
french.WebFingerprintRefused=Le certificat n'est pas accepté. Si l'empreinte diffère, ne continuez pas : un autre ordinateur pourrait se faire passer pour le serveur. Vérifiez le nom du serveur, ou utilisez le fichier certificat-serveur.cer copié depuis le serveur.
english.WebFingerprintRefused=The certificate is not accepted. If the fingerprint differs, do not go on: another computer could be posing as the server. Check the server name, or use the certificat-serveur.cer file copied from the server.
french.WebCheckOk=Serveur joint, certificat reçu. Empreinte SHA-256 :%n%1
english.WebCheckOk=Server reached, certificate received. SHA-256 fingerprint:%n%1
french.WebBrowserFound=Navigateur : %1 %2 convient ; le raccourci MarocLIS l'utilisera.
english.WebBrowserFound=Browser: %1 %2 is suitable; the MarocLIS shortcut will use it.
french.WebBrowserInstall=Navigateur : %1 ; Firefox ESR {#FirefoxVersion} sera installé et utilisé par le raccourci MarocLIS.
english.WebBrowserInstall=Browser: %1; Firefox ESR {#FirefoxVersion} will be installed and used by the MarocLIS shortcut.
french.WebBrowserNone=aucun navigateur récent sur ce poste
english.WebBrowserNone=no recent browser on this workstation
french.WebDefaultBrowser=le navigateur par défaut
english.WebDefaultBrowser=the default browser
french.WebBrowserTooOld=%1 %2, trop ancien
english.WebBrowserTooOld=%1 %2, too old
french.ReadyWebServer=Serveur MarocLIS Web :%n%1%2
english.ReadyWebServer=MarocLIS Web server:%n%1%2
french.ReadyWebCert=Certificat du serveur, ajouté aux autorités de confiance de Windows ; empreinte SHA-256 :%n%1%2
english.ReadyWebCert=Server certificate, added to Windows' trusted authorities; SHA-256 fingerprint:%n%1%2
french.ReadyWebBrowser=Navigateur du raccourci « MarocLIS » :%n%1%2
english.ReadyWebBrowser="MarocLIS" shortcut's browser:%n%1%2
french.ReadyWebFirefoxInstall=Firefox ESR {#FirefoxVersion}, installé par ce programme (%1)
english.ReadyWebFirefoxInstall=Firefox ESR {#FirefoxVersion}, installed by this setup (%1)
french.ReadyWebFirefoxPolicies=Firefox : certificat du serveur reconnu, page d'accueil MarocLIS (distribution\policies.json)
english.ReadyWebFirefoxPolicies=Firefox: server certificate trusted, MarocLIS home page (distribution\policies.json)
french.ReadyWebService=Service Windows MarocLIS.Web :%n%1compte LocalSystem, https://%2:{#WebPort}/, certificat HTTPS propre à ce serveur, pare-feu ouvert au réseau local
english.ReadyWebService=MarocLIS.Web Windows service:%n%1LocalSystem account, https://%2:{#WebPort}/, this server's own HTTPS certificate, firewall opened to the local network
french.PostWeb=Service MarocLIS.Web (certificat, pare-feu)…
english.PostWeb=MarocLIS.Web service (certificate, firewall)…
french.PostStopWebService=Arrêt du service MarocLIS.Web…
english.PostStopWebService=Stopping the MarocLIS.Web service…
french.PostFirefox=Installation de Firefox ESR {#FirefoxVersion}…
english.PostFirefox=Installing Firefox ESR {#FirefoxVersion}…
french.PostWebCertificate=Certificat du serveur…
english.PostWebCertificate=Server certificate…
french.PostFirefoxPolicies=Réglages de Firefox…
english.PostFirefoxPolicies=Firefox settings…
french.FirefoxInstallFailed=Firefox ESR n'a pas pu être installé (%1) : le raccourci MarocLIS ouvrira le navigateur par défaut, qui doit être Chrome ou Edge {#MinChromium} ou plus récent, ou Firefox {#MinFirefox} ou plus récent.
english.FirefoxInstallFailed=Firefox ESR could not be installed (%1): the MarocLIS shortcut will open the default browser, which must be Chrome or Edge {#MinChromium} or later, or Firefox {#MinFirefox} or later.
french.FirefoxPoliciesKept=Firefox (%1) a déjà ses propres stratégies (distribution\policies.json), conservées : dans Firefox, Paramètres › Vie privée et sécurité › Certificats, cochez « Autoriser Firefox à faire confiance aux certificats racines tiers ».
english.FirefoxPoliciesKept=Firefox (%1) already has its own policies (distribution\policies.json), kept: in Firefox, Settings > Privacy & Security > Certificates, tick "Allow Firefox to automatically trust third-party root certificates".
french.CertImportFailed=Le certificat du serveur n'a pas pu être ajouté aux autorités de confiance de Windows (certutil, code %1).
english.CertImportFailed=The server certificate could not be added to Windows' trusted authorities (certutil, code %1).

french.BkPageCaption=Sauvegarde automatique
english.BkPageCaption=Automatic backup
french.BkPageDescription=Sauvegarde de la base MarocLIS par le Planificateur de tâches de Windows.
english.BkPageDescription=Backup of the MarocLIS database by the Windows Task Scheduler.
french.BkPageSubCaption=Dossier où SQL Server écrit les sauvegardes : un dossier de ce serveur, un disque externe ou un partage réseau (\\serveur\partage). Évitez une lettre de lecteur réseau : un service n'en a pas.
english.BkPageSubCaption=Folder where SQL Server writes the backups: a folder of this server, an external disk or a network share (\\server\share). Avoid a mapped drive letter: a service has none.
french.BkEnabled=&Activer la sauvegarde automatique
english.BkEnabled=&Enable the automatic backup
french.BkTime=&Heure de la sauvegarde (HH:MM) :
english.BkTime=Daily backup &time (HH:MM):
french.BkRetention=&Conservation (jours) :
english.BkRetention=&Keep for (days):
french.BkNote=Tous ces réglages se modifient ensuite dans MarocLIS, Paramètres › Sauvegardes (plusieurs heures par jour, jours de la semaine). Les sauvegardes sont vérifiées après écriture ; la plus récente vérifiée n'est jamais supprimée.
english.BkNote=All these settings can be changed later in MarocLIS, Settings › Backups (several times a day, days of the week). Backups are verified once written; the most recent verified one is never deleted.
french.BkUpgradeNote=Mise à jour : si les sauvegardes ont déjà été réglées dans MarocLIS, ces réglages restent en vigueur et les valeurs ci-dessus ne servent pas.
english.BkUpgradeNote=Upgrade: if backups were already set up in MarocLIS, those settings stay in force and the values above are not used.
french.BkFolderRequired=Indiquez le dossier des sauvegardes (chemin complet, par exemple C:\MarocLIS\Sauvegardes ou \\serveur\partage).
english.BkFolderRequired=Enter the backup folder (full path, for example C:\MarocLIS\Sauvegardes or \\server\share).
french.BkTimeInvalid=L'heure doit s'écrire HH:MM, de 00:00 à 23:59.
english.BkTimeInvalid=The time must be written HH:MM, from 00:00 to 23:59.
french.BkRetentionInvalid=La conservation est un nombre de jours, de 1 à 365.
english.BkRetentionInvalid=Keep for is a number of days, from 1 to 365.
french.ReadyBackup=Sauvegarde automatique :%n%1%2 à %3, conservation %4 jours, dans %5%n%1tâche planifiée « {#BackupTaskName} » (compte SYSTEM)
english.ReadyBackup=Automatic backup:%n%1%2 at %3, kept %4 days, in %5%n%1scheduled task "{#BackupTaskName}" (SYSTEM account)
french.ReadyBackupOff=désactivée
english.ReadyBackupOff=disabled
french.ReadyBackupOn=tous les jours
english.ReadyBackupOn=every day
french.PostBackup=Sauvegarde automatique (dossier et tâche planifiée)…
english.PostBackup=Automatic backup (folder and scheduled task)…
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

french.FinishedServer=MarocLIS est installé sur ce serveur.%n%nAu premier démarrage, MarocLIS crée les tables de la base (identifiant « admin », mot de passe initial « Maroc-LIS-2026! », à changer aussitôt).%n%nPostes Windows : ajoutez les comptes Windows des utilisateurs au groupe local « {#UsersGroup} » de ce serveur (en groupe de travail, créez sur le serveur un compte de même nom et même mot de passe que sur le poste), puis installez « Poste Windows » sur chaque poste.%n%nValidation depuis le téléphone : installez Tailscale sur ce serveur puis exécutez « tailscale serve --bg 5080 » (manuel, Administration › Service de validation à distance).
english.FinishedServer=MarocLIS is installed on this server.%n%nOn its first start MarocLIS creates the database tables (user "admin", initial password "Maroc-LIS-2026!", to be changed at once).%n%nWorkstations: add the users' Windows accounts to this server's local group "{#UsersGroup}" (in a workgroup, create on the server an account with the same name and password as on the workstation), then install "Windows workstation" on each one.%n%nValidation from the phone: install Tailscale on this server, then run "tailscale serve --bg 5080" (manual, Administration > Remote validation service).
french.FinishedWebServer=Version Web : %1, pour les postes « Poste Web » (Windows 7 à 11) et tout navigateur du réseau local.%nEmpreinte SHA-256 du certificat du serveur, à comparer à celle qu'affiche l'installation de chaque Poste Web (aussi dans %3) :%n%2
english.FinishedWebServer=Web version: %1, for the "Web workstation" computers (Windows 7 to 11) and any browser of the local network.%nSHA-256 fingerprint of the server's certificate, to compare with the one each Web workstation's setup shows (also in %3):%n%2
french.FinishedWebStation=MarocLIS est prêt sur ce poste : le raccourci « MarocLIS » (bureau et menu Démarrer) ouvre %1 dans %2.%n%nIdentifiez-vous avec votre compte MarocLIS, comme sur un poste Windows. Le certificat du serveur est reconnu par Windows%3.
english.FinishedWebStation=MarocLIS is ready on this workstation: the "MarocLIS" shortcut (desktop and Start menu) opens %1 in %2.%n%nSign in with your MarocLIS account, as on a Windows workstation. The server's certificate is trusted by Windows%3.
french.FinishedWebStationFirefox= et par Firefox
english.FinishedWebStationFirefox= and by Firefox
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

  BackupPage: TInputDirWizardPage;
  BackupEnabledCheck: TNewCheckBox;
  BackupTimeEdit: TNewEdit;
  BackupRetentionEdit: TNewEdit;
  BackupUpgradeLabel: TNewStaticText;

  TypeDetailLabel: TNewStaticText;
  TypesComboChange: TNotifyEvent;
  TypePreselected: Boolean;

  { « Poste Web » }
  WebPage: TWizardPage;
  WebServerEdit: TNewEdit;
  WebCertEdit: TNewEdit;
  WebStatusLabel: TNewStaticText;
  WebBrowserLabel: TNewStaticText;
  WebCertTemp: String;               { the server's certificate (DER) in the temporary folder, once obtained }
  WebCertFingerprint: String;        { its SHA-256, as the server's setup shows it }
  WebCertConfirmed: String;          { server|file for which the fingerprint was confirmed }
  BrowserExe: String;                { the browser the shortcut opens; '' until one is found }
  BrowserName: String;
  BrowserMajor: Integer;
  OldBrowsers: String;               { the browsers found too old, for the page and the memo }
  NeedFirefox: Boolean;              { no suitable browser: the bundled Firefox ESR is installed }
  FirefoxInstalledHere: Boolean;

  { Server: MarocLIS.Web }
  WebServiceExisted: Boolean;
  WebSettingsExisted: Boolean;
  ServerFingerprint: String;         { of the certificate install-web-service.ps1 made, for the last page }

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

{ The machine's own registry view: HKLM64 raises an exception on a 32-bit Windows, which a
  « Poste Web » may be. }
function HKLMNative: Integer;
begin
  if IsWin64 then Result := HKLM64 else Result := HKLM;
end;

function IsServer: Boolean;
begin
  Result := WizardSetupType(False) = 'server';
end;

function IsWindowsClient: Boolean;
begin
  Result := WizardSetupType(False) = 'client';
end;

function IsWebStation: Boolean;
begin
  Result := WizardSetupType(False) = 'web';
end;

{ .NET 10, which the Windows application and the services are built on: Windows 10 or later, x64. }
function IsWindows10OrLater: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Major >= 10;
end;

function CanRunDotNet: Boolean;
begin
  Result := IsWindows10OrLater and IsX64Compatible;
end;

function WindowsName: String;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  if (Version.Major = 6) and (Version.Minor = 1) then Result := 'Windows 7'
  else if (Version.Major = 6) and (Version.Minor = 2) then Result := 'Windows 8'
  else if (Version.Major = 6) and (Version.Minor = 3) then Result := 'Windows 8.1'
  else Result := Format('Windows %d.%d', [Version.Major, Version.Minor]);
  if not IsX64Compatible then Result := Result + ' (32 bits)';
end;

{ The reason a type built on .NET 10 cannot be installed here, or ''. }
function TypeRefusal: String;
var
  Name: String;
begin
  Result := '';
  if IsServer then Name := CustomMessage('TypeServerShort')
  else if IsWindowsClient then Name := CustomMessage('TypeClientShort')
  else Exit;
  if not IsWindows10OrLater then
    Result := FmtMessage(CustomMessage('TypeNeedsWindows10'), [Name, WindowsName])
  else if not IsX64Compatible then
    Result := FmtMessage(CustomMessage('TypeNeedsX64'), [Name]);
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
  if not RegGetValueNames(HKLMNative, SqlInstancesKey, SqlInstances) then
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
  Result := RegKeyExists(HKLMNative, 'SYSTEM\CurrentControlSet\Services\{#ApiServiceName}');
end;

function WebServiceInstalled: Boolean;
begin
  Result := RegKeyExists(HKLMNative, 'SYSTEM\CurrentControlSet\Services\{#WebServiceName}');
end;

{ ---------------------------------------------------------------------------------------------- }
{ « Poste Web »: the browser                                                                      }
{ ---------------------------------------------------------------------------------------------- }

{ The program a browser registered under App Paths (machine-wide, then for this user), if it exists. }
function AppPathOf(const Exe: String): String;
var
  Key, Value: String;
begin
  Result := '';
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\' + Exe;
  if RegQueryStringValue(HKLMNative, Key, '', Value) and FileExists(RemoveQuotes(Value)) then
    Result := RemoveQuotes(Value)
  else if RegQueryStringValue(HKLM32, Key, '', Value) and FileExists(RemoveQuotes(Value)) then
    Result := RemoveQuotes(Value)
  else if RegQueryStringValue(HKCU, Key, '', Value) and FileExists(RemoveQuotes(Value)) then
    Result := RemoveQuotes(Value);
end;

{ The browser's program: from App Paths, else from where its installer puts it. }
function BrowserPath(const Exe, Folder: String): String;
begin
  Result := AppPathOf(Exe);
  if Result <> '' then Exit;
  if FileExists(ExpandConstant('{commonpf}\') + Folder + '\' + Exe) then
    Result := ExpandConstant('{commonpf}\') + Folder + '\' + Exe
  else if FileExists(ExpandConstant('{commonpf32}\') + Folder + '\' + Exe) then
    Result := ExpandConstant('{commonpf32}\') + Folder + '\' + Exe;
end;

{ The major version of a program, from its version resource (Chrome 109.0.5414.120 -> 109). }
function MajorVersionOf(const Path: String): Integer;
var
  Version: String;
begin
  Result := 0;
  if (Path <> '') and GetVersionNumbersString(Path, Version) then begin
    if Pos('.', Version) > 0 then Version := Copy(Version, 1, Pos('.', Version) - 1);
    Result := StrToIntDef(Version, 0);
  end;
end;

procedure ConsiderBrowser(const Name, Path: String; Minimum: Integer);
var
  Major: Integer;
begin
  if Path = '' then Exit;
  Major := MajorVersionOf(Path);
  if Major >= Minimum then begin
    if BrowserExe = '' then begin
      BrowserExe := Path;
      BrowserName := Name;
      BrowserMajor := Major;
    end;
  end else begin
    if OldBrowsers <> '' then OldBrowsers := OldBrowsers + ', ';
    OldBrowsers := OldBrowsers + FmtMessage(CustomMessage('WebBrowserTooOld'), [Name, IntToStr(Major)]);
  end;
end;

{ Edge, then Chrome, then Firefox: the first recent enough is the shortcut's. Internet Explorer and
  the former Edge (EdgeHTML) cannot run MarocLIS Web and are not looked at. }
procedure DetectBrowsers;
begin
  BrowserExe := '';
  BrowserName := '';
  BrowserMajor := 0;
  OldBrowsers := '';
  ConsiderBrowser('Microsoft Edge', BrowserPath('msedge.exe', 'Microsoft\Edge\Application'), {#MinChromium});
  ConsiderBrowser('Google Chrome', BrowserPath('chrome.exe', 'Google\Chrome\Application'), {#MinChromium});
  ConsiderBrowser('Mozilla Firefox', BrowserPath('firefox.exe', 'Mozilla Firefox'), {#MinFirefox});
  NeedFirefox := BrowserExe = '';
end;

function BrowserDescription: String;
begin
  if NeedFirefox then begin
    if OldBrowsers <> '' then Result := OldBrowsers else Result := CustomMessage('WebBrowserNone');
    Result := FmtMessage(CustomMessage('WebBrowserInstall'), [Result]);
  end else
    Result := FmtMessage(CustomMessage('WebBrowserFound'), [BrowserName, IntToStr(BrowserMajor)]);
end;

function BrowserLabel: String;
begin
  if FirefoxInstalledHere then Result := 'Firefox ESR {#FirefoxVersion}'
  else if BrowserExe <> '' then Result := BrowserName + ' ' + IntToStr(BrowserMajor)
  else Result := CustomMessage('WebDefaultBrowser');
end;

function WebServerName: String;
begin
  Result := Trim(WebServerEdit.Text);
end;

function WebUrl(Param: String): String;
begin
  Result := 'https://' + WebServerName + ':{#WebPort}/';
end;

{ The shortcut's program: the browser chosen, else Explorer, which opens the address in the
  default browser (only when the bundled Firefox could not be installed). }
function WebBrowserExe(Param: String): String;
begin
  if BrowserExe <> '' then Result := BrowserExe
  else Result := ExpandConstant('{win}\explorer.exe');
end;

function WebBrowserArgs(Param: String): String;
begin
  Result := '"' + WebUrl('') + '"';
end;

function UninstallIcon(Param: String): String;
begin
  if WizardIsComponentSelected('desktop') then Result := ExpandConstant('{app}\Desktop\MarocLIS.exe')
  else Result := ExpandConstant('{app}\maroclis-web.ico');
end;

{ ---------------------------------------------------------------------------------------------- }
{ « Poste Web »: the server's certificate                                                         }
{ ---------------------------------------------------------------------------------------------- }

{ Letters, digits, dots and hyphens: a name (SERVEUR-LABO, serveur.labo.local) or an IPv4 address. }
function ValidHostName(const S: String): Boolean;
var
  I: Integer;
begin
  Result := (Length(S) > 0) and (Length(S) <= 253);
  for I := 1 to Length(S) do
    if not (((S[I] >= 'a') and (S[I] <= 'z')) or ((S[I] >= 'A') and (S[I] <= 'Z')) or
            ((S[I] >= '0') and (S[I] <= '9')) or (S[I] = '.') or (S[I] = '-')) then
      Result := False;
end;

{ AB:CD:..., as install-web-service.ps1 writes it on the server. }
function Fingerprint(const Path: String): String;
var
  Hex: String;
  I: Integer;
begin
  Hex := Uppercase(GetSHA256OfFile(Path));
  Result := '';
  for I := 0 to (Length(Hex) div 2) - 1 do begin
    if I > 0 then Result := Result + ':';
    Result := Result + Copy(Hex, I * 2 + 1, 2);
  end;
end;

function CertUtilExe: String;
begin
  Result := ExpandConstant('{sys}\certutil.exe');
end;

{ Copies a certificate, binary (DER) or text (PEM), as DER into Target. }
function NormalizeCertificate(const Source, Target: String; var Error: String): Boolean;
var
  Content: AnsiString;
  Size, ResultCode: Integer;
begin
  Result := False;
  Error := CustomMessage('WebCertFileInvalid');
  if not FileSize(Source, Size) or (Size < 64) or (Size > 65536) then Exit;
  if not LoadStringFromFile(Source, Content) then Exit;
  DeleteFile(Target);
  if Pos('-----BEGIN CERTIFICATE-----', Content) > 0 then begin
    if not Exec(CertUtilExe, '-f -decode ' + Quote(Source) + ' ' + Quote(Target), '', SW_HIDE,
                ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then Exit;
  end
  else if Ord(Content[1]) = $30 then begin
    if not CopyFile(Source, Target, False) then Exit;
  end
  else
    Exit;
  Result := FileExists(Target);
  if Result then Error := '';
end;

{ GET url, the server's certificate not checked: MSXML's ServerXMLHTTP (its setOption), else
  WinHTTP's own WinHttpRequest (its Option property). Both are WinHTTP, on every Windows since XP.
  Returns the body, or '' with Error set to the step that failed and Windows' own words. }
function GetIgnoringCertificate(const Url: String; var Error: String): String;
var
  Http: Variant;
  Step: String;
begin
  Result := '';
  Step := 'ServerXMLHTTP';
  try
    Http := CreateOleObject('MSXML2.ServerXMLHTTP.6.0');
    Http.open('GET', Url, False);
    Http.setTimeouts(5000, 5000, 10000, 15000);
    { SXH_OPTION_IGNORE_SERVER_SSL_CERT_ERROR_FLAGS (2) = SXH_SERVER_CERT_IGNORE_ALL_SERVER_ERRORS
      (13056): unknown authority, wrong name, expired, wrong usage. }
    Step := 'ServerXMLHTTP.setOption';
    Http.setOption(2, 13056);
  except
    Log('Certificate request: ' + Step + ' unavailable (' + Trim(GetExceptionMessage) + '), WinHttpRequest instead.');
    Step := 'WinHttpRequest';
    try
      Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
      Http.Open('GET', Url, False);
      Http.SetTimeouts(5000, 5000, 10000, 15000);
      { WinHttpRequestOption_SslErrorIgnoreFlags (4): the same four errors. }
      Step := 'WinHttpRequest.Option';
      Http.Option[4] := 13056;
    except
      Error := Step + ' : ' + Trim(GetExceptionMessage);
      Exit;
    end;
  end;
  try
    Step := Step + '.send';
    Http.send('');
    if Http.status <> 200 then begin
      Error := 'HTTP ' + IntToStr(Http.status) + ' ' + Http.statusText;
      Exit;
    end;
    Result := Http.responseText;
  except
    Error := Step + ' : ' + Trim(GetExceptionMessage);
  end;
end;

{ Asks the server for its certificate, at /certificat-serveur.pem. Its HTTPS is not trusted yet —
  that is what this is for — so the certificate errors are ignored for this one request, and the
  person installing confirms the fingerprint before anything is trusted. A Windows 7 without its
  TLS 1.2 updates may fail here, and the .cer file then serves. }
function FetchServerCertificate(const Server, Target: String; var Error: String): Boolean;
var
  Pem, PemFile: String;
begin
  Result := False;
  Pem := GetIgnoringCertificate('https://' + Server + ':{#WebPort}/certificat-serveur.pem', Error);
  if Pem = '' then begin
    Log('Certificate request to ' + Server + ' failed: ' + Error);
    Exit;
  end;
  if Pos('-----BEGIN CERTIFICATE-----', Pem) = 0 then begin
    Error := CustomMessage('WebCertFileInvalid');
    Exit;
  end;
  PemFile := ExpandConstant('{tmp}\certificat-serveur.pem');
  if not SaveStringToFile(PemFile, Pem, False) then begin
    Error := PemFile;
    Exit;
  end;
  Result := NormalizeCertificate(PemFile, Target, Error);
end;

{ The certificate from the file given, else from the server; WebCertFingerprint set. }
function ObtainCertificate(var Error: String): Boolean;
begin
  WebCertTemp := ExpandConstant('{tmp}\certificat-serveur.cer');
  if Trim(WebCertEdit.Text) <> '' then
    Result := NormalizeCertificate(Trim(WebCertEdit.Text), WebCertTemp, Error)
  else begin
    Result := FetchServerCertificate(WebServerName, WebCertTemp, Error);
    if not Result then Error := FmtMessage(CustomMessage('WebFetchFailed'), [WebServerName, Error]);
  end;
  if Result then WebCertFingerprint := Fingerprint(WebCertTemp) else WebCertFingerprint := '';
end;

function WebCertSource: String;
begin
  if Trim(WebCertEdit.Text) <> '' then Result := Trim(WebCertEdit.Text) else Result := WebServerName;
end;

procedure WebCheckClick(Sender: TObject);
var
  Error: String;
begin
  if not ValidHostName(WebServerName) then begin
    MsgBox(CustomMessage('WebNameInvalid'), mbError, MB_OK);
    Exit;
  end;
  WebStatusLabel.Font.Color := clWindowText;
  if ObtainCertificate(Error) then
    WebStatusLabel.Caption := FmtMessage(CustomMessage('WebCheckOk'), [WebCertFingerprint])
  else begin
    WebStatusLabel.Caption := '';
    MsgBox(Error, mbError, MB_OK);
  end;
end;

procedure WebBrowseClick(Sender: TObject);
var
  FileName: String;
begin
  FileName := Trim(WebCertEdit.Text);
  if GetOpenFileName('', FileName, '', CustomMessage('WebCertFilter'), 'cer') then
    WebCertEdit.Text := FileName;
end;

procedure CreateWebPage;
var
  Intro, NameLabel, FileLabel: TNewStaticText;
  CheckButton, BrowseButton: TNewButton;
begin
  WebPage := CreateCustomPage(wpSelectComponents, CustomMessage('WebPageCaption'), CustomMessage('WebPageDescription'));

  Intro := TNewStaticText.Create(WebPage);
  Intro.Parent := WebPage.Surface;
  Intro.AutoSize := False;
  Intro.WordWrap := True;
  Intro.Width := WebPage.SurfaceWidth;
  Intro.Height := ScaleY(42);
  Intro.Caption := CustomMessage('WebPageSubCaption');

  NameLabel := TNewStaticText.Create(WebPage);
  NameLabel.Parent := WebPage.Surface;
  NameLabel.Top := Intro.Top + Intro.Height + ScaleY(6);
  NameLabel.Caption := CustomMessage('WebServerName');

  { The box first, then its button: the tab order, and the box has the focus when the page opens. }
  WebServerEdit := TNewEdit.Create(WebPage);
  WebServerEdit.Parent := WebPage.Surface;
  WebServerEdit.Top := NameLabel.Top + NameLabel.Height + ScaleY(3);
  WebServerEdit.Width := WebPage.SurfaceWidth - ScaleX(148);
  WebServerEdit.Text := GetPreviousData('WebServer', '');
  NameLabel.FocusControl := WebServerEdit;

  CheckButton := TNewButton.Create(WebPage);
  CheckButton.Parent := WebPage.Surface;
  CheckButton.Caption := CustomMessage('WebCheck');
  CheckButton.Width := ScaleX(140);
  CheckButton.Height := WizardForm.NextButton.Height;
  CheckButton.Left := WebPage.SurfaceWidth - CheckButton.Width;
  CheckButton.Top := WebServerEdit.Top - ScaleY(1);
  CheckButton.OnClick := @WebCheckClick;

  FileLabel := TNewStaticText.Create(WebPage);
  FileLabel.Parent := WebPage.Surface;
  FileLabel.Top := WebServerEdit.Top + WebServerEdit.Height + ScaleY(10);
  FileLabel.Caption := CustomMessage('WebCertFile');

  WebCertEdit := TNewEdit.Create(WebPage);
  WebCertEdit.Parent := WebPage.Surface;
  WebCertEdit.Top := FileLabel.Top + FileLabel.Height + ScaleY(3);
  WebCertEdit.Width := WebPage.SurfaceWidth - ScaleX(148);
  FileLabel.FocusControl := WebCertEdit;

  BrowseButton := TNewButton.Create(WebPage);
  BrowseButton.Parent := WebPage.Surface;
  BrowseButton.Caption := SetupMessage(msgButtonWizardBrowse);
  BrowseButton.Width := ScaleX(140);
  BrowseButton.Height := WizardForm.NextButton.Height;
  BrowseButton.Left := WebPage.SurfaceWidth - BrowseButton.Width;
  BrowseButton.Top := WebCertEdit.Top - ScaleY(1);
  BrowseButton.OnClick := @WebBrowseClick;

  WebStatusLabel := TNewStaticText.Create(WebPage);
  WebStatusLabel.Parent := WebPage.Surface;
  WebStatusLabel.AutoSize := False;
  WebStatusLabel.WordWrap := True;
  WebStatusLabel.Top := WebCertEdit.Top + WebCertEdit.Height + ScaleY(8);
  WebStatusLabel.Width := WebPage.SurfaceWidth;
  WebStatusLabel.Height := ScaleY(30);
  WebStatusLabel.Caption := '';

  WebBrowserLabel := TNewStaticText.Create(WebPage);
  WebBrowserLabel.Parent := WebPage.Surface;
  WebBrowserLabel.AutoSize := False;
  WebBrowserLabel.WordWrap := True;
  WebBrowserLabel.Top := WebStatusLabel.Top + WebStatusLabel.Height + ScaleY(4);
  WebBrowserLabel.Width := WebPage.SurfaceWidth;
  WebBrowserLabel.Height := WebPage.SurfaceHeight - WebBrowserLabel.Top;
  WebBrowserLabel.Font.Style := [fsBold];
  WebBrowserLabel.Caption := BrowserDescription;
end;

{ Next on the « Poste Web » page: the name checked, the certificate obtained and its fingerprint
  confirmed — once for a given server and file. }
function WebPageNext: Boolean;
var
  Error, Key: String;
begin
  Result := False;
  if WebServerName = '' then begin
    MsgBox(CustomMessage('WebNameRequired'), mbError, MB_OK);
    Exit;
  end;
  if not ValidHostName(WebServerName) then begin
    MsgBox(CustomMessage('WebNameInvalid'), mbError, MB_OK);
    Exit;
  end;
  Key := Lowercase(WebServerName) + '|' + Trim(WebCertEdit.Text);
  if (Key = WebCertConfirmed) and FileExists(WebCertTemp) then begin
    Result := True;
    Exit;
  end;
  WebCertConfirmed := '';
  if not ObtainCertificate(Error) then begin
    MsgBox(Error, mbError, MB_OK);
    Exit;
  end;
  WebStatusLabel.Caption := FmtMessage(CustomMessage('WebCheckOk'), [WebCertFingerprint]);
  if MsgBox(FmtMessage(CustomMessage('WebConfirmFingerprint'), [WebCertSource, WebCertFingerprint]),
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then begin
    MsgBox(CustomMessage('WebFingerprintRefused'), mbError, MB_OK);
    Exit;
  end;
  WebCertConfirmed := Key;
  Result := True;
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

{ The server's backup: the folder, whether the task backs up, one daily time and the retention.
  The task reads its schedule from the database; these are its first values (scripts/maroclis-backup.ps1). }
procedure CreateBackupPage;
var
  TimeLabel, RetentionLabel, NoteLabel: TNewStaticText;
  Top: Integer;
begin
  BackupPage := CreateInputDirPage(LicensePage.ID, CustomMessage('BkPageCaption'), CustomMessage('BkPageDescription'),
    CustomMessage('BkPageSubCaption'), False, '');
  BackupPage.Add('');
  BackupPage.Values[0] := GetPreviousData('BackupFolder', '{#DefaultBackupFolder}');

  Top := BackupPage.Edits[0].Top + BackupPage.Edits[0].Height + ScaleY(14);

  BackupEnabledCheck := TNewCheckBox.Create(BackupPage);
  BackupEnabledCheck.Parent := BackupPage.Surface;
  BackupEnabledCheck.Top := Top;
  BackupEnabledCheck.Width := BackupPage.SurfaceWidth;
  BackupEnabledCheck.Height := ScaleY(17);
  BackupEnabledCheck.Caption := CustomMessage('BkEnabled');
  BackupEnabledCheck.Checked := GetPreviousData('BackupEnabled', 'true') = 'true';

  Top := Top + ScaleY(28);
  TimeLabel := TNewStaticText.Create(BackupPage);
  TimeLabel.Parent := BackupPage.Surface;
  TimeLabel.Top := Top + ScaleY(3);
  TimeLabel.Caption := CustomMessage('BkTime');

  BackupTimeEdit := TNewEdit.Create(BackupPage);
  BackupTimeEdit.Parent := BackupPage.Surface;
  BackupTimeEdit.Top := Top;
  BackupTimeEdit.Left := ScaleX(290);
  BackupTimeEdit.Width := ScaleX(60);
  BackupTimeEdit.MaxLength := 5;
  BackupTimeEdit.Text := GetPreviousData('BackupTime', '22:00');
  TimeLabel.FocusControl := BackupTimeEdit;

  Top := Top + BackupTimeEdit.Height + ScaleY(8);
  RetentionLabel := TNewStaticText.Create(BackupPage);
  RetentionLabel.Parent := BackupPage.Surface;
  RetentionLabel.Top := Top + ScaleY(3);
  RetentionLabel.Caption := CustomMessage('BkRetention');

  BackupRetentionEdit := TNewEdit.Create(BackupPage);
  BackupRetentionEdit.Parent := BackupPage.Surface;
  BackupRetentionEdit.Top := Top;
  BackupRetentionEdit.Left := ScaleX(290);
  BackupRetentionEdit.Width := ScaleX(60);
  BackupRetentionEdit.MaxLength := 3;
  BackupRetentionEdit.Text := GetPreviousData('BackupRetention', '14');
  RetentionLabel.FocusControl := BackupRetentionEdit;

  Top := Top + BackupRetentionEdit.Height + ScaleY(14);
  NoteLabel := TNewStaticText.Create(BackupPage);
  NoteLabel.Parent := BackupPage.Surface;
  NoteLabel.AutoSize := False;
  NoteLabel.WordWrap := True;
  NoteLabel.Top := Top;
  NoteLabel.Width := BackupPage.SurfaceWidth;
  NoteLabel.Height := ScaleY(44);
  NoteLabel.Caption := CustomMessage('BkNote');

  BackupUpgradeLabel := TNewStaticText.Create(BackupPage);
  BackupUpgradeLabel.Parent := BackupPage.Surface;
  BackupUpgradeLabel.AutoSize := False;
  BackupUpgradeLabel.WordWrap := True;
  BackupUpgradeLabel.Top := NoteLabel.Top + NoteLabel.Height + ScaleY(6);
  BackupUpgradeLabel.Width := BackupPage.SurfaceWidth;
  BackupUpgradeLabel.Height := ScaleY(32);
  BackupUpgradeLabel.Font.Style := [fsBold];
  BackupUpgradeLabel.Caption := CustomMessage('BkUpgradeNote');
  BackupUpgradeLabel.Visible := False;
end;

{ HH:MM, 00:00 to 23:59. }
function ValidBackupTime(const S: String): Boolean;
var
  Hours, Minutes: Integer;
begin
  Result := (Length(S) = 5) and (S[3] = ':');
  if Result then begin
    Hours := StrToIntDef(Copy(S, 1, 2), -1);
    Minutes := StrToIntDef(Copy(S, 4, 2), -1);
    Result := (Hours >= 0) and (Hours <= 23) and (Minutes >= 0) and (Minutes <= 59);
  end;
end;

function BackupRetentionDays: Integer;
begin
  Result := StrToIntDef(Trim(BackupRetentionEdit.Text), 0);
end;

function BackupFolderValue: String;
begin
  Result := Trim(BackupPage.Values[0]);
  { Without its trailing separator: before a closing quote on a command line, "E:\" would escape the
    quote. A drive root goes as "E:", which the backup script turns back into E:\. }
  if (Length(Result) >= 3) and (Result[Length(Result)] = '\') then
    Result := Copy(Result, 1, Length(Result) - 1);
end;

function BackupEnabledValue: String;
begin
  if BackupEnabledCheck.Checked then Result := 'true' else Result := 'false';
end;

function DesktopSettingsPresent: Boolean;
begin
  Result := FileExists(AddBackslash(WizardDirValue) + 'Desktop\appsettings.json');
end;

procedure UpdateTypeDetail;
begin
  if IsServer then
    TypeDetailLabel.Caption := CustomMessage('TypeServerDetail')
  else if IsWindowsClient then
    TypeDetailLabel.Caption := CustomMessage('TypeClientDetail')
  else
    TypeDetailLabel.Caption := CustomMessage('TypeWebDetail');
  { On Windows 7, 8 or 8.1 (or a 32-bit Windows) the reason comes first: refused at Next, said now. }
  if TypeRefusal <> '' then begin
    TypeDetailLabel.Caption := TypeRefusal;
    TypeDetailLabel.Font.Color := clRed;
  end else
    TypeDetailLabel.Font.Color := clWindowText;
end;

procedure TypesComboChanged(Sender: TObject);
begin
  if TypesComboChange <> nil then TypesComboChange(Sender);
  UpdateTypeDetail;
end;

{ Windows 7, 8, 8.1 or a 32-bit Windows: « Poste Web » is the type that can be installed, chosen
  for the user the first time the page shows (they may still look at the others, and are told why
  they cannot have them). }
procedure PreselectWebType;
var
  I: Integer;
begin
  if TypePreselected then Exit;
  TypePreselected := True;
  if CanRunDotNet then Exit;
  for I := 0 to WizardForm.TypesCombo.Items.Count - 1 do
    if WizardForm.TypesCombo.Items[I] = CustomMessage('TypeWeb') then begin
      WizardForm.TypesCombo.ItemIndex := I;
      TypesComboChanged(WizardForm.TypesCombo);
    end;
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
  CreateBackupPage;
  CreateLicenceFilePage;
  DetectBrowsers;
  CreateWebPage;
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
    Result := not IsWindowsClient
  else if PageID = BackupPage.ID then
    Result := not IsServer
  else if PageID = WebPage.ID then
    Result := not IsWebStation
  else if PageID = LicenceFilePage.ID then
    { The licence is the server's: a web workstation has nothing to import it into. }
    Result := IsWebStation;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectComponents then begin
    WizardForm.PageNameLabel.Caption := CustomMessage('TypePageCaption');
    WizardForm.PageDescriptionLabel.Caption := CustomMessage('TypePageDescription');
    PreselectWebType;
    UpdateTypeDetail;
  end
  else if CurPageID = LicensePage.ID then
    WizardForm.NextButton.Enabled := LicenseAccept.Checked
  else if CurPageID = WebPage.ID then
    WizardForm.ActiveControl := WebServerEdit
  else if CurPageID = ServerPage.ID then begin
    { An existing appsettings.json is kept as it is: say so rather than ask for a name that
      would not be written. }
    ServerKeptLabel.Visible := DesktopSettingsPresent;
    ServerPage.Edits[0].Enabled := not DesktopSettingsPresent;
    ServerPage.Edits[1].Enabled := not DesktopSettingsPresent;
  end
  else if CurPageID = BackupPage.ID then
    { The task keeps the schedule already set in MarocLIS: said here rather than discovered later. }
    BackupUpgradeLabel.Visible := ServiceInstalled
  else if CurPageID = wpFinished then begin
    if IsServer then begin
      WizardForm.FinishedLabel.Caption := CustomMessage('FinishedServer');
      if ServerFingerprint <> '' then
        WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
          FmtMessage(CustomMessage('FinishedWebServer'), ['https://' + GetComputerNameString + ':{#WebPort}/',
                     ServerFingerprint, ExpandConstant('{app}\Web\certificat-serveur.txt')]);
    end
    else if IsWebStation then begin
      if FirefoxInstalledHere or (Pos('firefox', Lowercase(BrowserExe)) > 0) then
        WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FinishedWebStation'), [WebUrl(''),
          BrowserLabel, CustomMessage('FinishedWebStationFirefox')])
      else
        WizardForm.FinishedLabel.Caption := FmtMessage(CustomMessage('FinishedWebStation'), [WebUrl(''), BrowserLabel, '']);
    end
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
var
  Folder: String;
begin
  Result := True;
  if (CurPageID = wpSelectComponents) and (TypeRefusal <> '') then begin
    MsgBox(TypeRefusal, mbError, MB_OK);
    Result := False;
  end
  else if CurPageID = WebPage.ID then
    Result := WebPageNext
  else if CurPageID = SqlPage.ID then begin
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
  else if CurPageID = BackupPage.ID then begin
    Folder := Trim(BackupPage.Values[0]);
    if (Folder = '') or (Pos('"', Folder) > 0) or (Pos('''', Folder) > 0) or
       not ((Pos(':\', Folder) = 2) or (Copy(Folder, 1, 2) = '\\')) then begin
      MsgBox(CustomMessage('BkFolderRequired'), mbError, MB_OK);
      Result := False;
    end
    else if not ValidBackupTime(Trim(BackupTimeEdit.Text)) then begin
      MsgBox(CustomMessage('BkTimeInvalid'), mbError, MB_OK);
      Result := False;
    end
    else if (BackupRetentionDays < 1) or (BackupRetentionDays > 365) then begin
      MsgBox(CustomMessage('BkRetentionInvalid'), mbError, MB_OK);
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
  if IsWebStation then SetPreviousData(PreviousDataKey, 'WebServer', WebServerName);
  if IsServer then begin
    SetPreviousData(PreviousDataKey, 'BackupFolder', BackupFolderValue);
    SetPreviousData(PreviousDataKey, 'BackupEnabled', BackupEnabledValue);
    SetPreviousData(PreviousDataKey, 'BackupTime', Trim(BackupTimeEdit.Text));
    SetPreviousData(PreviousDataKey, 'BackupRetention', IntToStr(BackupRetentionDays));
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S, Source, Days, Browser: String;
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
    if BackupEnabledCheck.Checked then Days := CustomMessage('ReadyBackupOn') else Days := CustomMessage('ReadyBackupOff');
    S := S + FmtMessage(CustomMessage('ReadyBackup'), [Space, Days, Trim(BackupTimeEdit.Text),
                                                       IntToStr(BackupRetentionDays), BackupFolderValue]) + NewLine;
    S := S + FmtMessage(CustomMessage('ReadyWebService'), [Space, GetComputerNameString]) + NewLine;
  end else if IsWebStation then begin
    S := S + FmtMessage(CustomMessage('ReadyWebServer'), [Space, WebUrl('')]) + NewLine;
    S := S + FmtMessage(CustomMessage('ReadyWebCert'), [Space, WebCertFingerprint]) + NewLine;
    if NeedFirefox then begin
      if OldBrowsers <> '' then Browser := OldBrowsers else Browser := CustomMessage('WebBrowserNone');
      Browser := FmtMessage(CustomMessage('ReadyWebFirefoxInstall'), [Browser]);
    end else
      Browser := BrowserName + ' ' + IntToStr(BrowserMajor) + ' (' + BrowserExe + ')';
    S := S + FmtMessage(CustomMessage('ReadyWebBrowser'), [Space, Browser]) + NewLine;
    if NeedFirefox or (BrowserPath('firefox.exe', 'Mozilla Firefox') <> '') then
      S := S + Space + CustomMessage('ReadyWebFirefoxPolicies') + NewLine;
  end else if not DesktopSettingsPresent then
    S := S + CustomMessage('ReadyConnection') + NewLine + Space + ConnectionStringFor(ClientDataSource) + NewLine
  else
    S := S + CustomMessage('SrvKept') + NewLine;
  if IsWebStation then
    { no licence on a web workstation }
  else if LicenceFileChosen <> '' then
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

{ The bundled Firefox ESR, silently (Mozilla's installer, /S), when no browser of the workstation
  can run MarocLIS Web. Before the files, so the shortcuts point at it. }
procedure InstallFirefox;
var
  Setup: String;
  ResultCode: Integer;
begin
  WizardForm.PreparingLabel.Caption := CustomMessage('PostFirefox');
  try
    ExtractTemporaryFile('firefox-esr-setup.exe');
  except
    AddStepMessage(FmtMessage(CustomMessage('FirefoxInstallFailed'), [GetExceptionMessage]));
    Exit;
  end;
  Setup := ExpandConstant('{tmp}\firefox-esr-setup.exe');
  Log('Firefox ESR setup: ' + Setup + ' /S');
  if not Exec(Setup, '/S', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then begin
    AddStepMessage(FmtMessage(CustomMessage('FirefoxInstallFailed'), [SysErrorMessage(ResultCode)]));
    Exit;
  end;
  Log(Format('Firefox ESR setup exit code %d', [ResultCode]));
  BrowserExe := BrowserPath('firefox.exe', 'Mozilla Firefox');
  if (ResultCode <> 0) or (BrowserExe = '') then begin
    BrowserExe := '';
    AddStepMessage(FmtMessage(CustomMessage('FirefoxInstallFailed'), [IntToStr(ResultCode)]));
    Exit;
  end;
  BrowserName := 'Firefox ESR';
  BrowserMajor := MajorVersionOf(BrowserExe);
  FirefoxInstalledHere := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  { A silent install (/TYPE=server on Windows 7) is refused here, as the wizard refuses it. }
  Result := TypeRefusal;
  if Result <> '' then Exit;

  LogPath := AddBackslash(WizardDirValue) + 'Installer\install.log';
  DesktopSettingsExisted := FileExists(AddBackslash(WizardDirValue) + 'Desktop\appsettings.json');
  ApiSettingsExisted := FileExists(AddBackslash(WizardDirValue) + 'Api\appsettings.json');
  WebSettingsExisted := FileExists(AddBackslash(WizardDirValue) + 'Web\appsettings.json');
  ServiceExisted := ServiceInstalled;
  WebServiceExisted := WebServiceInstalled;

  if IsWebStation then begin
    if NeedFirefox then InstallFirefox;
    Exit;
  end;

  if MustInstallSql then begin
    Result := InstallSqlExpress;
    if Result <> '' then Exit;
  end;

  { Upgrade: the services hold their files open. }
  if ServiceExisted then begin
    WizardForm.PreparingLabel.Caption := CustomMessage('PostStopService');
    RunHelper('StopService', '', CustomMessage('PostStopService'));
  end;
  if IsServer and WebServiceExisted then begin
    WizardForm.PreparingLabel.Caption := CustomMessage('PostStopWebService');
    RunHelper('StopService', '-ServiceName ' + Quote('{#WebServiceName}'), CustomMessage('PostStopWebService'));
  end;
end;

procedure SetStatus(const Text: String);
begin
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.FilenameLabel.Caption := '';
end;

{ The fingerprint install-web-service.ps1 wrote beside the web, for the server's last page. }
function ReadServerFingerprint: String;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if LoadStringsFromFile(ExpandConstant('{app}\Web\certificat-serveur.txt'), Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      if (Pos('SHA-256', Lines[I]) > 0) and (Pos(': ', Lines[I]) > 0) then
        Result := Trim(Copy(Lines[I], Pos(': ', Lines[I]) + 2, Length(Lines[I])));
end;

{ Where the « Poste Web » keeps the server's certificate: an ASCII path whatever the language of
  Windows, which policies.json can name. }
function StationCertificatePath: String;
begin
  Result := ExpandConstant('{commonappdata}\MarocLIS\certificat-serveur.cer');
end;

function JsonString(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  Result := '"' + Result + '"';
end;

{ Firefox keeps its own certificates: told by its enterprise policies (distribution\policies.json
  beside firefox.exe, read by Firefox 60 and later) to trust Windows' authorities and this server's
  certificate, to open MarocLIS as its home page, and to spare the operator its first-run and
  default-browser questions. Its updates stay on: Firefox ESR 115 still receives its security
  fixes on Windows 7 and 8. A policies.json not written by MarocLIS is the laboratory's: kept. }
procedure ConfigureFirefox(const FirefoxExe: String);
var
  Folder, FileName, Json: String;
  Existing: AnsiString;
begin
  if FirefoxExe = '' then Exit;
  Folder := AddBackslash(ExtractFileDir(FirefoxExe)) + 'distribution';
  FileName := Folder + '\policies.json';
  if FileExists(FileName) and LoadStringFromFile(FileName, Existing) and
     (Pos('certificat-serveur.cer', Existing) = 0) then begin
    AddStepMessage(FmtMessage(CustomMessage('FirefoxPoliciesKept'), [Folder]));
    Exit;
  end;
  Json := '{' + #13#10 +
          '  "policies": {' + #13#10 +
          '    "Certificates": {' + #13#10 +
          '      "ImportEnterpriseRoots": true,' + #13#10 +
          '      "Install": [' + JsonString(StationCertificatePath) + ']' + #13#10 +
          '    },' + #13#10 +
          '    "Homepage": {' + #13#10 +
          '      "URL": ' + JsonString(WebUrl('')) + ',' + #13#10 +
          '      "StartPage": "homepage"' + #13#10 +
          '    },' + #13#10 +
          '    "DontCheckDefaultBrowser": true,' + #13#10 +
          '    "OverrideFirstRunPage": "",' + #13#10 +
          '    "OverridePostUpdatePage": ""' + #13#10 +
          '  }' + #13#10 +
          '}' + #13#10;
  if not ForceDirectories(Folder) or not SaveStringToFile(FileName, Json, False) then
    AddStepMessage(FmtMessage(CustomMessage('StepFailed'), [CustomMessage('PostFirefoxPolicies') + ' ' + FileName, LogPath]))
  else
    Log('Firefox policies written: ' + FileName);
end;

{ « Poste Web », after the files: the server's certificate trusted by Windows (LocalMachine\Root,
  which Chrome, Edge and — through ImportEnterpriseRoots — Firefox read), and Firefox set up. No
  PowerShell: Windows 7 only has version 2.0. }
procedure ConfigureWebStation;
var
  ResultCode: Integer;
begin
  SetStatus(CustomMessage('PostWebCertificate'));
  ForceDirectories(ExtractFileDir(StationCertificatePath));
  if not CopyFile(WebCertTemp, StationCertificatePath, False) then
    AddStepMessage(FmtMessage(CustomMessage('CertImportFailed'), [StationCertificatePath]))
  else if not Exec(CertUtilExe, '-f -addstore Root ' + Quote(StationCertificatePath), '', SW_HIDE,
                   ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    AddStepMessage(FmtMessage(CustomMessage('CertImportFailed'), [IntToStr(ResultCode)]))
  else
    Log('Server certificate ' + WebCertFingerprint + ' added to LocalMachine\Root.');

  SetStatus(CustomMessage('PostFirefoxPolicies'));
  { Every Firefox recent enough, the shortcut's or not: either may be opened on MarocLIS. }
  if MajorVersionOf(BrowserPath('firefox.exe', 'Mozilla Firefox')) >= {#MinFirefox} then
    ConfigureFirefox(BrowserPath('firefox.exe', 'Mozilla Firefox'));
  if (Pos('firefox', Lowercase(BrowserExe)) > 0) and
     (CompareText(BrowserExe, BrowserPath('firefox.exe', 'Mozilla Firefox')) <> 0) then
    ConfigureFirefox(BrowserExe);
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

      SetStatus(CustomMessage('PostBackup'));
      RunHelper('InstallBackupTask', '-Instance ' + Quote(SqlInstance) + ' -Database ' + Quote('{#DatabaseName}') +
                ' -BackupScript ' + Quote(ExpandConstant('{app}\Sauvegarde\maroclis-backup.ps1')) +
                ' -BackupFolder ' + Quote(BackupFolderValue) + ' -BackupTimes ' + Quote(Trim(BackupTimeEdit.Text)) +
                ' -RetentionDays ' + IntToStr(BackupRetentionDays) + ' -BackupEnabled ' + BackupEnabledValue,
                CustomMessage('PostBackup'));

      SetStatus(CustomMessage('PostService'));
      if ServiceExisted then
        RunHelper('StartService', '', CustomMessage('PostService'))
      else
        RunHelper('InstallService', '-ApiDirectory ' + Quote(ExpandConstant('{app}\Api')), CustomMessage('PostService'));

      { MarocLIS Web: the same database; its certificate, firewall rule and service, at every install
        and upgrade (install-web-service.ps1 keeps what is there and starts the service). }
      SetStatus(CustomMessage('PostWeb'));
      if not WebSettingsExisted then
        RunHelper('SetConnectionString', '-File ' + Quote(ExpandConstant('{app}\Web\appsettings.json')) +
                  ' -Server ' + Quote(ServerDataSource), CustomMessage('PostWeb'));
      RunHelper('InstallWebService', '-WebDirectory ' + Quote(ExpandConstant('{app}\Web')), CustomMessage('PostWeb'));
      ServerFingerprint := ReadServerFingerprint;
    end
    else if IsWebStation then
      ConfigureWebStation
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
