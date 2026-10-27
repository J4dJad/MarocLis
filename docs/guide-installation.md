# Guide d'installation et d'exploitation de MarocLIS

Ce guide s'adresse au technicien qui installe MarocLIS dans un laboratoire, et, pour la construction du programme d'installation et la clé de licence, à l'éditeur. Il décrit ce que fait réellement le programme d'installation (`installer/MarocLIS.iss` et ses scripts) : chaque page, chaque valeur par défaut, chaque fichier écrit. Le fonctionnement de chaque écran de MarocLIS est décrit dans le manuel utilisateur (chapitre « Administration », ouvert par **F1** dans l'application).

Conventions : `{app}` désigne le dossier d'installation choisi dans l'assistant, `C:\MarocLIS` par défaut (plus exactement `<lecteur système>\MarocLIS`). Les menus de MarocLIS s'écrivent **Paramètres › Sauvegardes**.

## 1. Vue d'ensemble

Un laboratoire compte **un seul serveur**, des **postes de travail** et, s'il utilise la validation à distance, les **téléphones** des biologistes. Un poste de travail est soit un **Poste Windows** (l'application MarocLIS, Windows 10 ou 11), soit un **Poste Web** (MarocLIS dans le navigateur, servi par le serveur, de Windows 7 à Windows 11). Un seul programme d'installation, `MarocLIS-Setup-<version>.exe`, sert pour le serveur et pour les postes : le type d'installation se choisit dans l'assistant.

| Ordinateur | Ce qui y est installé | Rôle |
|---|---|---|
| **Serveur** (type « Serveur », Windows 10, 11 ou Windows Server, 64 bits) | SQL Server Express 2025, instance `SQLEXPRESS` (seulement si aucun SQL Server n'est déjà installé) ; la base `MarocLIS` ; l'application MarocLIS ; le service Windows `MarocLIS.Api` ; le service Windows `MarocLIS.Web` (version Web, `https://<serveur>:5443`) et son certificat HTTPS ; la tâche planifiée « MarocLIS - Sauvegarde » | Garde la base de données, la sauvegarde, sert la version Web aux postes du réseau local et l'application du téléphone. On peut aussi y travailler avec MarocLIS. |
| **Poste Windows** (type « Poste Windows », Windows 10 ou 11, 64 bits) | L'application MarocLIS seule | Se connecte à la base du serveur par le réseau local, avec le compte Windows de l'utilisateur. |
| **Poste Web** (type « Poste Web », Windows 7 SP1, 8, 8.1, 10 ou 11, 32 ou 64 bits) | Rien de MarocLIS ne s'y exécute : le certificat du serveur, un navigateur récent si le poste n'en a pas (Firefox ESR 115), le raccourci « MarocLIS » | Ouvre la version Web du serveur dans le navigateur, avec les mêmes écrans et le compte MarocLIS de l'utilisateur. Section 5.5. |
| **Téléphone** du biologiste | Rien depuis le programme d'installation : l'application Tailscale, puis l'application web « MarocLIS Validation » servie par le serveur | Validation biologique à distance, par le réseau privé Tailscale. |

Points communs :

- L'application et les services sont publiés « autonomes » (self-contained) pour Windows 64 bits : **aucun runtime .NET n'est à installer**, ni sur le serveur ni sur les postes. Le Poste Web n'en a pas besoin non plus : il n'exécute que le navigateur.
- Les Postes Windows et les services se connectent à SQL Server en **authentification Windows** (`Integrated Security=true`) : il n'y a pas de mot de passe SQL dans les fichiers de configuration. Les comptes Windows des utilisateurs doivent appartenir au groupe local **`MarocLIS-Utilisateurs`** du serveur, créé par l'installation.
- Le service `MarocLIS.Api` n'écoute que sur la machine elle-même (`http://127.0.0.1:5080`). Les téléphones l'atteignent uniquement par `tailscale serve`, qui ajoute le HTTPS ; aucun port n'est ouvert pour lui, ni sur Internet ni sur le réseau du laboratoire.
- Le service `MarocLIS.Web` écoute en HTTPS sur le port **5443** de toutes les adresses du serveur ; le pare-feu ne l'ouvre qu'au **sous-réseau local**, et le service lui-même répond « 403 » à toute adresse hors des plages privées. Son certificat est créé par l'installation, propre à ce serveur (section 4.8).
- Les téléphones ne comptent pas dans le nombre de postes de la licence.

Ordre d'installation : **d'abord le serveur**, puis chaque Poste Windows ou Poste Web.

## 2. Configuration requise

### Système d'exploitation

- Le programme d'installation **démarre** sur Windows **7 SP1** et toute version ultérieure, 32 ou 64 bits (`MinVersion=6.1sp1`, `ArchitecturesAllowed=x86compatible x64compatible`). Sur Windows 7 sans le Service Pack 1, Windows refuse de le lancer.
- Les types **Serveur** et **Poste Windows** demandent **Windows 10 ou ultérieur, 64 bits** (x64) : l'application et les services sont construits sur .NET 10, que Microsoft ne fournit pas pour Windows 7, 8 ni 8.1. Cela couvre Windows 10, Windows 11 et les Windows Server du même noyau (2016 et ultérieurs).
- Le type **Poste Web** fonctionne de Windows 7 SP1 à Windows 11, 32 ou 64 bits. Sur un Windows antérieur à Windows 10 ou en 32 bits, l'assistant le présélectionne et refuse les deux autres types avec un message qui en donne la raison. Le tableau de la section 5.5 dit ce qui est installé selon la version de Windows.
- Les versions de Windows prises en charge par **SQL Server 2025 Express** sur le serveur sont fixées par Microsoft ; le code de MarocLIS ne les vérifie pas. Consultez la documentation de Microsoft avant de choisir le serveur.

### Droits

- Le programme d'installation exige un **compte administrateur** de l'ordinateur (`PrivilegesRequired=admin`) : Windows demande l'élévation au lancement.
- Sur un serveur où SQL Server est **déjà installé**, le compte qui installe doit être **administrateur (sysadmin) de l'instance** choisie : l'installation y crée la base et les connexions.
- Quand l'installation installe elle-même SQL Server Express, elle fait des administrateurs Windows de la machine (`BUILTIN\Administrators`) les administrateurs de SQL Server.

### SQL Server Express

L'installation serveur installe **Microsoft SQL Server 2025 Express** quand aucun SQL Server n'est présent (voir 4.3). Limites de cette édition, publiées par Microsoft (le code de MarocLIS ne les contrôle pas) :

| Limite | Valeur |
|---|---|
| Taille maximale d'une base | 50 Go |
| Mémoire utilisée pour le cache des données | environ 1,4 Go (1 410 Mo) |
| Processeurs | le plus petit de : 1 socket ou 4 cœurs |
| Agent SQL Server (planificateur de travaux) | absent : c'est pourquoi MarocLIS sauvegarde par une tâche planifiée Windows (section 7) |
| Compression des sauvegardes | non proposée par Express : les fichiers `.bak` ont à peu près la taille de la base |

### Espace disque

Le code ne fixe aucun minimum d'espace disque. À prévoir :

- le téléchargement de SQL Server Express (environ 300 Mo selon le programme d'installation) et son installation ;
- la base de données, qui grandit avec l'activité ;
- les sauvegardes : non compressées avec Express, une par horaire prévu, conservées 14 jours par défaut, plus la dernière sauvegarde vérifiée. Avec une sauvegarde par jour, comptez au moins quinze fois la taille de la base dans le dossier des sauvegardes.

### Réseau

| Flux | Sens | Ouvert par l'installation |
|---|---|---|
| Moteur SQL Server (programme `sqlservr.exe` de l'instance, quel que soit son port TCP) | Postes → serveur | Oui : règle de pare-feu entrante « MarocLIS - SQL Server (`<instance>`) », sous-réseau local seulement |
| SQL Server Browser, UDP 1434 (instance nommée, par exemple `SQLEXPRESS`) | Postes → serveur | Oui : règle « MarocLIS - SQL Server Browser », sous-réseau local seulement |
| Service `MarocLIS.Web`, `https://<serveur>:5443` (TCP 5443) | Postes Web et navigateurs du réseau local → serveur | Oui : règle « MarocLIS - Web (HTTPS 5443) », sous-réseau local seulement |
| Service `MarocLIS.Api`, `http://127.0.0.1:5080` | Machine elle-même | Aucun port à ouvrir : il n'écoute que sur la boucle locale |
| Tailscale (`tailscale serve --bg 5080`, publié en HTTPS sur le réseau Tailscale) | Téléphones → serveur | Non : Tailscale s'installe et se configure à part (section 4.10) |
| Téléchargement de SQL Server Express depuis Microsoft | Serveur → Internet | Seulement si aucun support hors ligne n'est posé à côté du programme d'installation |
| Mises à jour de Firefox ESR (Poste Web où l'installation l'a installé) | Poste → Internet | Non : Firefox les télécharge lui-même quand le poste a Internet ; sans Internet, il reste dans sa version |

Les règles de pare-feu créées s'appliquent à **tous les profils réseau** (domaine, privé, public), parce que le réseau d'un petit laboratoire est souvent classé « public », et n'admettent que le **sous-réseau local** (`LocalSubnet`). Elles sont rangées dans le groupe de règles « MarocLIS ».

TCP/IP doit être activé sur l'instance SQL Server. Il l'est quand l'installation installe SQL Server Express ; sur une instance existante, l'installation se contente de le vérifier (voir 11.3).

### Domaine ou groupe de travail

Les Postes Windows s'authentifient auprès de SQL Server avec leur compte Windows (un Poste Web n'est concerné par rien de ceci : il se connecte avec un compte MarocLIS, et c'est le service `MarocLIS.Web` qui accède à la base) :

- **en domaine**, ajoutez au groupe local `MarocLIS-Utilisateurs` du serveur les comptes ou groupes du domaine ;
- **en groupe de travail**, créez sur le serveur, pour chaque utilisateur, un compte local **de même nom et de même mot de passe** que sur son poste, et ajoutez-le au groupe.

## 3. Construire le programme d'installation (éditeur)

Cette section concerne l'éditeur, pas le laboratoire, qui reçoit le fichier `MarocLIS-Setup-<version>.exe` déjà construit.

### 3.1 Prérequis

- **Windows PowerShell 5.1** ou PowerShell 7. Les scripts sont écrits en ASCII, encodés en UTF-8 avec BOM, pour que Windows PowerShell 5.1 les lise correctement.
- **SDK .NET 10** : le fichier `global.json` du dépôt demande la version `10.0.100` (ou une version plus récente de la même bande, `rollForward: latestFeature`).
- **Inno Setup 6.3 ou ultérieur, de la série 6** (https://jrsoftware.org/isdl.php ; 6.7.3 essayé). Son programme d'installation produit démarre sur Windows 7 SP1, ce que demande le type Poste Web ; le script refuse une version antérieure à 6.3 ou une version 7 et suivantes, dont le Windows minimal n'a pas été vérifié. Le compilateur `ISCC.exe` est cherché dans cet ordre : le paramètre `-Iscc`, la variable d'environnement `INNO_SETUP_ISCC`, le `PATH`, puis `%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe`, `%ProgramFiles%\Inno Setup 6\ISCC.exe` et `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.
- La **clé publique de licence de production** à la place de la clé de développement (voir 3.4 et la section 8, partie 2).
- **Internet** à la première construction, pour télécharger depuis Mozilla le programme d'installation de **Firefox ESR 115** inclus dans le programme MarocLIS (3.3, étape 5) ; ou ce fichier posé à la main dans `installer\prereqs`.

### 3.2 Commande

Depuis la racine du dépôt :

```
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

`-ExecutionPolicy Bypass` évite le refus de Windows quand l'exécution de scripts est désactivée sur le poste.

| Paramètre | Effet |
|---|---|
| `-Configuration <nom>` | Configuration de compilation, `Release` par défaut. |
| `-SkipPublish` | Ne publie pas : compile le dossier de préparation tel qu'il est. |
| `-Iscc <chemin>` | Chemin de `ISCC.exe`, par exemple `-Iscc 'D:\Tools\Inno Setup 6\ISCC.exe'`. |
| `-AllowDevelopmentKey` | Construit malgré la clé de licence de développement (essais internes seulement, voir 3.4). |
| `-FirefoxVersion <115.x.y>` | Version de Firefox ESR 115 à télécharger, par exemple `115.30.0` ; par défaut la plus récente des versions 115.x ESR publiées par Mozilla. Sans effet si le fichier est déjà dans `installer\prereqs`. |
| `-FirefoxSha256 <empreinte>` | Empreinte SHA-256 attendue du programme d'installation de Firefox, comparée à chaque construction. Obligatoire là où la signature Authenticode ne peut pas être vérifiée (hors Windows). |

### 3.3 Ce que fait le script

1. Lit la version du produit dans `Directory.Build.props` (élément `<Version>`, actuellement `1.0.0`) ; s'arrête si elle manque (« No <Version> in Directory.Build.props. »).
2. Calcule et affiche l'**empreinte de la clé de licence** compilée (`src/MarocLIS.Application/Licensing/licence-public-key.pem`) : « Licence key fingerprint: … ».
3. Publie, sauf avec `-SkipPublish`, l'application (`src/MarocLIS.Desktop`), le service (`src/MarocLIS.Api`) et la version Web (`src/MarocLIS.Web`) en mode autonome pour `win-x64` dans `installer\build\staging\Desktop`, `installer\build\staging\Api` et `installer\build\staging\Web`. Le fichier `appsettings.Development.json` est retiré : il ne doit jamais être livré. La publication du service y place aussi l'application du téléphone (`MarocLIS.Mobile`, Blazor WebAssembly) dans `wwwroot`.
4. Vérifie la présence des fichiers indispensables, et s'arrête sur « Missing from the published files: … » s'il en manque un :
    - `Desktop` : `MarocLIS.exe`, `hostfxr.dll`, `appsettings.json`, `Resources/fr.json`, `Help`, `Assets/Fonts/Amiri-Regular.ttf`, `Assets/Fonts/Carlito-Regular.ttf`, `libSkiaSharp.dll`, `QuestPdfSkia.dll`, `Microsoft.Data.SqlClient.SNI.dll` ;
    - `Api` : les mêmes (sauf `MarocLIS.exe` et `Help`), `MarocLIS.Api.exe`, et `wwwroot/index.html`, `wwwroot/_framework/dotnet.js`, `wwwroot/_framework/dotnet.native.wasm`, `wwwroot/_framework/MarocLIS.Mobile.wasm`, `wwwroot/service-worker-assets.js` ;
    - `Web` : `MarocLIS.Web.exe`, `hostfxr.dll`, `appsettings.json`, `Resources/fr.json`, les polices et bibliothèques des comptes rendus, `MarocLIS.Web.staticwebassets.endpoints.json`, `wwwroot/css/app.css`, `wwwroot/js/compat.js`, `wwwroot/js/maroclis.js`, `wwwroot/_framework/blazor.web.js`, `wwwroot/icons/FlaskConical.png`.
5. **Firefox ESR 115** pour le type Poste Web. Si `installer\prereqs\Firefox-Setup-esr115-fr-win32.exe` n'existe pas, le script le télécharge une fois depuis le site de Mozilla (`https://ftp.mozilla.org/pub/firefox/releases/<version>esr/win32/fr/`) : le programme d'installation complet, en français, 32 bits (il s'installe sur un Windows 32 ou 64 bits), de la version la plus récente 115.x ou de `-FirefoxVersion`. La version est notée dans `Firefox-Setup-esr115-fr-win32.version.txt`. À chaque construction, il vérifie le fichier : empreinte SHA-256 égale à `-FirefoxSha256` quand ce paramètre est donné, et signature Authenticode **valide et de Mozilla Corporation**. Il s'arrête si le fichier manque, si le téléchargement échoue ou si une vérification échoue. Le dossier `installer\prereqs` est ignoré par Git.
6. Vérifie la version d'Inno Setup (6.3 ou ultérieure, série 6), puis compile `installer/MarocLIS.iss` et affiche le chemin et la taille du résultat.

Résultat : **`installer\output\MarocLIS-Setup-<version>.exe`**. Les dossiers `installer\build\` et `installer\output\` sont ignorés par Git (`installer/.gitignore`).

Le script rappelle à la fin : pour installer SQL Server Express sans Internet sur le serveur, posez `SQLEXPR_x64_ENU.exe` à côté du programme d'installation (voir 4.3).

### 3.4 Refus de la clé de développement

Le dépôt contient une clé de licence de **développement** (empreinte `CDC1-D2A2-222A-4A92-B835-95FB-909A-18BB`) dont la clé privée est dans `tests/MarocLIS.Tests/Licensing` : n'importe qui ayant le code pourrait signer des licences acceptées par une version compilée avec elle. Si l'empreinte calculée est celle-ci, le script **s'arrête** et explique la marche à suivre (exporter la clé publique depuis le Générateur de licences, la copier sur `src/MarocLIS.Application/Licensing/licence-public-key.pem`, valider ce changement, relancer).

Avec `-AllowDevelopmentKey`, il construit quand même, avec un avertissement, un fichier nommé **`MarocLIS-Setup-<version>-CLE-DEV.exe`**, réservé aux essais internes et à ne jamais installer dans un laboratoire. La procédure de création de la clé de production est décrite en section 8, partie 2.

### 3.5 Contrôles facultatifs du code

Deux scripts du dossier `scripts` vérifient le code source sans SDK ; ils ne jouent aucun rôle dans l'installation. Depuis la racine du dépôt : `powershell -ExecutionPolicy Bypass -File .\scripts\check-reachability.ps1` (chaque fonction a un chemin depuis l'interface, chaque écran est enregistré et dans la navigation, etc.) et `scripts\check-resource-keys.ps1` (clés de traduction).

## 4. Installer le serveur

### 4.1 Avant de commencer

- Ouvrez une session avec un compte **administrateur** de la machine (et sysadmin de SQL Server si SQL Server est déjà installé).
- Choisissez l'ordinateur définitivement : la licence d'exploitation est liée au serveur (nom de la machine, instance SQL Server, base), voir section 8.
- Sans Internet sur le serveur : posez le support de SQL Server Express à côté de `MarocLIS-Setup-<version>.exe`, sous l'une de ces formes (cherchées dans cet ordre) :
  - un dossier `SQLEXPR\` contenant `setup.exe` ;
  - un dossier `SQLEXPR_x64_ENU\` contenant `setup.exe` ;
  - le paquet de Microsoft `SQLEXPR*.exe` lui-même, par exemple `SQLEXPR_x64_ENU.exe`.

  Le programme d'installation obtient ce paquet, quand il est en ligne, en lançant le programme de Microsoft `SQL2025-SSEI-Expr.exe` avec `/Action=Download /MediaType=Core /MediaPath=<dossier> /Quiet` ; la même commande, lancée sur un ordinateur relié à Internet, fournit le paquet à copier.
- Si vous avez déjà un fichier de licence `.lic` (essai, formation, réinstallation sur le même serveur), gardez-le à portée.

### 4.2 Les pages de l'assistant

Lancez `MarocLIS-Setup-<version>.exe`. Les pages se suivent ainsi ; une page sans objet pour le type choisi est sautée.

1. **Langue de l'assistant.** Le français est proposé en premier quelle que soit la langue de Windows ; l'anglais reste disponible.
2. **Dossier de destination.** Par défaut `C:\MarocLIS` (`{sd}\MarocLIS`). Lors d'une mise à jour, le dossier de l'installation précédente est repris.
3. **Type d'installation.** Choisissez **« Serveur : base de données, application, version Web, validation mobile »**. Le texte sous la liste résume ce qui sera installé : SQL Server Express (instance `SQLEXPRESS`) sauf si SQL Server est déjà installé, l'application MarocLIS reliée à la base de ce serveur, le service Windows `MarocLIS.Api`, le service Windows `MarocLIS.Web` avec son certificat HTTPS, et le pare-feu Windows ouvert à SQL Server et à la version Web pour le réseau local. Lors d'une mise à jour, le type précédent est repris. Sur un Windows antérieur à Windows 10 ou en 32 bits, ce type est refusé (section 2).
4. **SQL Server.** Deux cas :
    - **SQL Server déjà installé** : la page affiche « SQL Server est déjà installé sur cet ordinateur. Instance utilisée par MarocLIS : » et une liste des instances trouvées. `SQLEXPRESS` est présélectionnée si elle existe, sinon la première instance. La note rappelle que l'installation crée la base « MarocLIS » si elle n'existe pas, donne l'accès au groupe `MarocLIS-Utilisateurs` et au service, active SQL Server Browser et ouvre le pare-feu au réseau local, et que votre compte doit être sysadmin de l'instance.
    - **Aucun SQL Server** : la page annonce l'installation de Microsoft SQL Server 2025 Express (moteur seul, instance `SQLEXPRESS`, TCP/IP activé, pare-feu ouvert au réseau local, administrateurs de l'ordinateur administrateurs de SQL Server), puis indique la source : le support hors ligne trouvé à côté du programme, ou le téléchargement depuis Microsoft (environ 300 Mo, Internet nécessaire).
5. **Licence de Microsoft SQL Server Express** (seulement si SQL Server doit être installé). SQL Server s'installe sans les écrans de Microsoft : c'est ici que vous acceptez sa licence (lien vers `https://aka.ms/useterms`) et la collecte de données décrite dans la déclaration de confidentialité de Microsoft. « Je n'accepte pas » est coché au départ et le bouton **Suivant** reste grisé tant que « J'accepte la licence Microsoft SQL Server » n'est pas coché ; sans accepter, revenez en arrière (choisir un autre type) ou annulez.
6. **Sauvegarde automatique.** Premières valeurs de la sauvegarde de la base (section 7) :
    - **dossier** où SQL Server écrit les sauvegardes, par défaut `C:\MarocLIS\Sauvegardes` : un chemin complet (`X:\…`) ou un partage réseau (`\\serveur\partage`), jamais une lettre de lecteur réseau (un service n'en a pas) ; les guillemets et apostrophes sont refusés ;
    - **Activer la sauvegarde automatique** : coché par défaut ;
    - **Heure de la sauvegarde (HH:MM)** : `22:00` par défaut, de `00:00` à `23:59` ;
    - **Conservation (jours)** : `14` par défaut, de 1 à 365.

    Tous ces réglages se modifient ensuite dans MarocLIS, **Paramètres › Sauvegardes** (plusieurs heures par jour, jours de la semaine). Lors d'une mise à jour, un avertissement en gras rappelle que si les sauvegardes ont déjà été réglées dans MarocLIS, ces réglages restent en vigueur et les valeurs de la page ne servent pas.

7. **Licence MarocLIS** (facultative). Indiquez le fichier `.lic` reçu de l'éditeur, ou laissez vide. Le fichier doit exister et porter l'extension `.lic` ; il est copié sous le nom `{app}\Desktop\licence.lic` et importé au premier démarrage (section 6).
8. **Tâches supplémentaires** : **Créer une icône sur le Bureau** (facultatif). Un raccourci « MarocLIS » est toujours créé dans le menu Démarrer.
9. **Prêt à installer.** Le récapitulatif indique : le dossier, le type, SQL Server (installation et source, ou instance existante), la chaîne de connexion qui sera écrite (`Server=localhost\SQLEXPRESS;Database=MarocLIS;Integrated Security=true` pour l'instance `SQLEXPRESS` ; `Server=localhost;…` pour l'instance par défaut `MSSQLSERVER`), le service (« compte LocalSystem, http://127.0.0.1:5080, démarrage automatique différé »), le service MarocLIS.Web (« compte LocalSystem, https://<serveur>:5443/, certificat HTTPS propre à ce serveur, pare-feu ouvert au réseau local »), la sauvegarde (« tous les jours à 22:00, conservation 14 jours, dans … », tâche planifiée « MarocLIS - Sauvegarde », compte SYSTEM), et la licence. Lors d'une mise à jour, il ajoute que le service est arrêté pendant la copie puis redémarré, et que les fichiers `appsettings.json` existants sont conservés.

    Si SQL Server doit être téléchargé, un clic sur **Installer** télécharge d'abord le petit programme de Microsoft `SQL2025-SSEI-Expr.exe` ; en cas d'échec : « Le téléchargement de SQL Server Express a échoué : … ».

10. **Installation de SQL Server Express** (si nécessaire), puis copie des fichiers et configuration (4.3 à 4.9). L'étape SQL Server peut durer **de 10 à 30 minutes** : ne fermez pas la fenêtre.
11. **Fin.** Le message rappelle les étapes suivantes :
    - au premier démarrage, MarocLIS crée les tables de la base (identifiant `admin`, mot de passe initial `Maroc-LIS-2026!`, à changer aussitôt) ;
    - Postes Windows : ajouter les comptes Windows des utilisateurs au groupe local `MarocLIS-Utilisateurs` du serveur (en groupe de travail, créer sur le serveur un compte de même nom et même mot de passe que sur le poste), puis installer « Poste Windows » sur chaque poste ;
    - version Web : l'adresse `https://<serveur>:5443/` pour les Postes Web (Windows 7 à 11) et tout navigateur du réseau local, et l'**empreinte SHA-256** du certificat du serveur (32 octets écrits en hexadécimal, `AB:CD:…`), à comparer à celle qu'affiche l'installation de chaque Poste Web ; elle est aussi dans `{app}\Web\certificat-serveur.txt`. **Notez-la** ;
    - validation depuis le téléphone : installer Tailscale sur le serveur puis exécuter `tailscale serve --bg 5080`.

    Si une étape a échoué ou s'est terminée avec un avertissement, la liste **« À vérifier : »** la nomme et renvoie au journal `{app}\Installer\install.log` (section 11.3). La case **Lancer MarocLIS** démarre l'application avec le compte de la session (sans les droits élevés de l'installation). Si l'installation de SQL Server a demandé un redémarrage (code 3010), l'assistant propose de redémarrer l'ordinateur.

### 4.3 Installation de SQL Server Express

Seulement quand aucune instance SQL Server n'est enregistrée sur l'ordinateur (clé de registre `HKLM\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL`).

**Source.** Le support hors ligne trouvé à côté du programme d'installation, sinon le téléchargement par `SQL2025-SSEI-Expr.exe` du paquet « Core » (moteur seul). Un paquet `SQLEXPR*.exe` est d'abord décompressé (`/q /x:`).

**Installation silencieuse**, avec ces options :

```
/QS /ACTION=Install /FEATURES=SQLEngine /INSTANCENAME=SQLEXPRESS
/SQLSVCACCOUNT="NT Service\MSSQL$SQLEXPRESS" /SQLSVCSTARTUPTYPE=Automatic
/BROWSERSVCSTARTUPTYPE=Automatic /TCPENABLED=1
/SQLSYSADMINACCOUNTS="BUILTIN\Administrators" /UpdateEnabled=False
/IACCEPTSQLSERVERLICENSETERMS /SUPPRESSPRIVACYSTATEMENTNOTICE
```

(plus `/ENU` pour un support anglais sur un Windows non anglais).

**Contrôle.** Un code de retour autre que 0 ou 3010 arrête l'installation avec « L'installation de SQL Server Express a échoué (code …) » ; si aucune instance n'est enregistrée après coup : « … aucune instance SQL Server n'est enregistrée sur cet ordinateur ». Dans les deux cas, le détail est dans le journal de SQL Server : `C:\Program Files\Microsoft SQL Server\<version>\Setup Bootstrap\Log\Summary.txt`.

### 4.4 Chaîne de connexion

Pour une **première installation** seulement, l'installation écrit dans `{app}\Desktop\appsettings.json`, `{app}\Api\appsettings.json` et `{app}\Web\appsettings.json` la clé `Database:ConnectionString` :

```
Server=localhost\<instance>;Database=MarocLIS;Integrated Security=true
```

(`Server=localhost;…` pour l'instance par défaut). Seule cette valeur est réécrite : les commentaires et la disposition du fichier livré sont conservés. Un fichier `appsettings.json` déjà présent n'est **jamais** remplacé ni modifié, ni par la copie des fichiers ni par cette étape.

### 4.5 Base de données, accès et pare-feu

Étape « Base de données, accès et pare-feu… » :

1. **TCP/IP** de l'instance : vérifié ; s'il est désactivé, avertissement (les postes ne pourront pas se connecter tant qu'il n'est pas activé dans le Gestionnaire de configuration SQL Server et le service SQL Server redémarré).
2. **SQL Server Browser** (instance nommée seulement) : mis en démarrage automatique et démarré. S'il est introuvable, avertissement : les postes devront indiquer le port de l'instance dans le nom du serveur.
3. **Pare-feu Windows** : règle entrante « MarocLIS - SQL Server (`<instance>`) » pour le programme `sqlservr.exe` de l'instance, et, pour une instance nommée, règle « MarocLIS - SQL Server Browser » (UDP 1434) ; sous-réseau local, tous profils, groupe « MarocLIS ». Une règle du même nom est d'abord supprimée puis recréée.
4. **Groupe local `MarocLIS-Utilisateurs`** : créé s'il n'existe pas (description « Comptes autorises a utiliser MarocLIS (connexion Windows a SQL Server). »).
5. **Base `MarocLIS`** : créée **vide** si elle n'existe pas. Les tables sont créées au premier démarrage de MarocLIS (section 6).
6. **Connexions SQL Server**, chacune membre du rôle `db_owner` de la base (les postes appliquent les mises à jour du schéma au démarrage, d'où ce rôle) :
    - le groupe `<SERVEUR>\MarocLIS-Utilisateurs` ;
    - le compte Windows qui exécute l'installation (un groupe ajouté maintenant ne figurerait dans son jeton qu'à sa prochaine ouverture de session) ;
    - `NT AUTHORITY\SYSTEM`, compte des services `MarocLIS.Api` et `MarocLIS.Web` et de la tâche de sauvegarde.
7. `GRANT CREATE ANY DATABASE TO [NT AUTHORITY\SYSTEM]` : nécessaire à `RESTORE VERIFYONLY`, qui vérifie chaque sauvegarde.

### 4.6 Tâche planifiée de sauvegarde

Étape « Sauvegarde automatique (dossier et tâche planifiée)… » :

- **Dossier local** (`X:\…`) : créé s'il n'existe pas, et le compte du service SQL Server (par exemple `NT Service\MSSQL$SQLEXPRESS`) reçoit le droit **Modifier** dessus (`icacls … /grant <compte>:(OI)(CI)M`) : c'est SQL Server, et non la tâche, qui écrit les fichiers.
- **Partage réseau** (`\\…`) : rien ne peut être réglé d'ici ; avertissement rappelant que les autorisations du partage et NTFS doivent laisser écrire le **compte ordinateur du serveur** (`DOMAINE\SERVEUR$`).
- **Tâche « MarocLIS - Sauvegarde »** enregistrée (remplacée si elle existe) :
  - programme : `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{app}\Sauvegarde\maroclis-backup.ps1" -Server "localhost\<instance>" -Database "MarocLIS" -DefaultFolder "…" -DefaultTimes "22:00" -DefaultRetentionDays 14 -DefaultEnabled true` (valeurs de la page Sauvegarde) ;
  - déclencheurs : **toutes les 15 minutes**, sans fin, à partir de minuit le jour de l'installation, et **au démarrage** de l'ordinateur ;
  - compte **SYSTEM**, privilèges les plus élevés ; une seule instance à la fois ; démarre dès que possible si un passage a été manqué ; fonctionne sur batterie ; arrêtée au-delà de 4 heures.

Les valeurs de l'assistant ne sont que des valeurs initiales : la tâche les écrit dans la base au premier passage où elle n'en trouve pas, jamais par-dessus un réglage existant (section 7).

### 4.7 Service MarocLIS.Api

Étape « Service MarocLIS.Api… », par `{app}\Api\install-api-service.ps1` :

- nom `MarocLIS.Api`, nom affiché « MarocLIS - validation a distance (API) » ;
- compte **LocalSystem** (`NT AUTHORITY\SYSTEM`), programme `{app}\Api\MarocLIS.Api.exe` ;
- démarrage **automatique différé** (SQL Server et Tailscale démarrent d'abord après un redémarrage) ;
- redémarrage après un arrêt anormal : après 1 minute, 1 minute, puis 5 minutes ; compteur remis à zéro après un jour ;
- dossier de journaux `{app}\Api\logs` ;
- démarrage, puis contrôle que `http://127.0.0.1:5080/` répond (sinon avertissement : voir les journaux et le journal d'événements Windows).

Lors d'une mise à jour, le service existant est seulement arrêté avant la copie et redémarré après.

Le script peut aussi servir à la main, dans un PowerShell lancé en administrateur, dans `{app}\Api` :

```
.\install-api-service.ps1                         # créer et démarrer le service (LocalSystem)
.\install-api-service.ps1 -Account 'LAB\svc-maroclis' -Password (Read-Host -AsSecureString)
.\install-api-service.ps1 -Uninstall              # arrêter et supprimer le service
```

Avec `-Account`, le compte reçoit le droit « Ouvrir une session en tant que service » et le droit Modifier sur `logs` ; il doit alors avoir lui-même accès à la base (même rôle que les postes). Le manuel recommande un compte dédié plutôt que LocalSystem ; l'installation, elle, utilise LocalSystem. Si le service existe déjà, le script refuse (« Service MarocLIS.Api already exists… ») : utilisez d'abord `-Uninstall`.

### 4.8 Service MarocLIS.Web (version Web)

La version Web est la seconde interface de MarocLIS : les mêmes écrans, servis par le serveur aux navigateurs du réseau local, à l'adresse **`https://<serveur>:5443/`**. C'est elle qu'utilisent les Postes Web (section 5.5), et notamment les postes sous Windows 7, 8 et 8.1, qui ne peuvent pas faire fonctionner l'application Windows.

Étape « Service MarocLIS.Web (certificat, pare-feu)… », par `{app}\Web\install-web-service.ps1` :

1. **Certificat HTTPS propre au serveur.** Certificat auto-signé, RSA 2048 bits, SHA-256, valable **10 ans**, clé privée non exportable, sujet `CN=MarocLIS Web <NOM-DU-SERVEUR>`, nom convivial « MarocLIS Web ». Ses noms (`subjectAltName`) sont le **nom** du serveur, son **nom DNS complet**, `localhost`, chacune de ses **adresses IPv4** et `127.0.0.1` : `https://SERVEUR:5443`, `https://serveur.domaine.local:5443` et `https://192.168.1.10:5443` sont tous reconnus. Il est rangé dans le magasin de l'ordinateur **`LocalMachine\My`** (Personnel), et ajouté à **`LocalMachine\Root`** (Autorités de certification racines de confiance) pour que le navigateur du serveur lui-même lui fasse confiance. Un certificat existant est **réutilisé** tant qu'il lui reste plus de 30 jours ; si une adresse IP actuelle du serveur n'y figure pas, le journal le signale (voir plus bas « Changement de nom ou d'adresse »).
2. **Kestrel** (le serveur HTTPS de la version Web) est réglé pour l'utiliser : dans `{app}\Web\appsettings.json`, sous `Kestrel:Endpoints:Https`, le script ajoute `"Certificate": { "Subject": "CN=MarocLIS Web <NOM>", "Store": "My", "Location": "LocalMachine", "AllowInvalid": true }` (ou met à jour le sujet d'un bloc qu'il a déjà écrit). Le reste du fichier est conservé tel quel. `AllowInvalid` est nécessaire parce que le certificat est auto-signé.
3. **Partie publique du certificat** exportée dans `{app}\Web\certificat-serveur.cer`, et son **empreinte SHA-256** écrite dans `{app}\Web\certificat-serveur.txt` (ligne « Empreinte SHA-256  : AB:CD:… ») et affichée sur la dernière page de l'assistant. Si le serveur a un partage nommé `MarocLIS`, les deux fichiers y sont aussi copiés. Le service sert ce certificat aux postes du réseau local à `https://<serveur>:5443/certificat-serveur.cer` (et `.pem`) : c'est là que l'installation d'un Poste Web le demande.
4. **Pare-feu Windows** : règle entrante « MarocLIS - Web (HTTPS 5443) », TCP 5443, **sous-réseau local** seulement, tous profils, groupe « MarocLIS » (remplacée si elle existe).
5. **Service Windows** :
    - nom `MarocLIS.Web`, nom affiché « MarocLIS - version Web » ;
    - compte **LocalSystem** (`NT AUTHORITY\SYSTEM`, qui a accès à la base, voir 4.5), programme `{app}\Web\MarocLIS.Web.exe` ;
    - démarrage **automatique différé** ; redémarrage après un arrêt anormal : 1 minute, 1 minute, puis 5 minutes ; compteur remis à zéro après un jour ;
    - journaux `{app}\Web\logs\maroclis-web-AAAAMMJJ.log` ;
    - démarré (ou redémarré s'il existait), puis contrôle que `https://localhost:5443/connexion` répond ; sinon avertissement (voir 11.10).

À son démarrage, le service crée les tables de la base si elles n'existent pas ou applique les mises à jour du schéma, comme le fait l'application Windows (réglage `Web:InitializeDatabase`, `true` par défaut) : la base est donc prête dès la fin de l'installation du serveur.

Le service ne répond qu'aux adresses des réseaux privés (`Web:AllowedNetworks` : boucle locale, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, adresses link-local, `100.64.0.0/10` et leurs équivalents IPv6) ; toute autre adresse reçoit « 403 ». On peut restreindre cette liste aux sous-réseaux du laboratoire dans `{app}\Web\appsettings.json`.

**Changement de nom ou d'adresse du serveur.** Le certificat ne couvre que les noms et adresses du jour de sa création. Après un changement de nom ou une nouvelle adresse IP fixe, dans un PowerShell lancé en administrateur, dans `{app}\Web` :

```
.\install-web-service.ps1 -RenewCertificate      # nouveau certificat, l'ancien est retiré, service redémarré
.\install-web-service.ps1                        # sans rien changer : vérifie et redémarre (sans effet si tout est en place)
.\install-web-service.ps1 -Uninstall             # arrêter et supprimer le service et sa règle de pare-feu
```

Un nouveau certificat a une nouvelle empreinte : **relancez l'installation « Poste Web » sur chaque Poste Web** pour qu'il lui fasse confiance (section 5.5).

### 4.9 Ordre exact des opérations

| Moment | Opération |
|---|---|
| Avant la copie des fichiers | Installation de SQL Server Express (si nécessaire) ; arrêt des services `MarocLIS.Api` et `MarocLIS.Web` s'ils existent (mise à jour) |
| Copie | `{app}\Desktop` (application), `{app}\Api` (service et application du téléphone, `install-api-service.ps1`), `{app}\Web` (version Web, `install-web-service.ps1`), `{app}\Sauvegarde\maroclis-backup.ps1` ; `appsettings.json` seulement s'il n'existe pas encore |
| Après la copie | Copie de la licence en `{app}\Desktop\licence.lic` ; chaînes de connexion ; base, accès et pare-feu ; tâche de sauvegarde ; création du service `MarocLIS.Api` (première installation) ou redémarrage (mise à jour) ; service `MarocLIS.Web` : certificat, pare-feu, création ou redémarrage |

Une étape qui échoue n'interrompt pas l'installation : elle est signalée sur la dernière page (« Étape non terminée : … » ou « Étape terminée avec un avertissement : … ») et détaillée dans `{app}\Installer\install.log`.

### 4.10 Après l'installation du serveur

1. **Démarrer MarocLIS sur le serveur** (section 6) : création des tables, compte `admin`, licence.
2. **Ajouter les utilisateurs** au groupe local `MarocLIS-Utilisateurs` du serveur (Gestion de l'ordinateur › Utilisateurs et groupes locaux). Un utilisateur ajouté doit fermer puis rouvrir sa session Windows pour que l'appartenance soit prise en compte.
3. **Vérifier la sauvegarde** : **Paramètres › Sauvegardes**, **Tester le dossier**, puis **Sauvegarder maintenant** (section 7).
4. **Vérifier la version Web** : sur le serveur, ouvrir `https://localhost:5443/` dans Edge ou Chrome ; la page de connexion de MarocLIS s'affiche sans alerte de sécurité. Garder à portée l'**empreinte SHA-256** affichée en fin d'installation (`{app}\Web\certificat-serveur.txt`) pour l'installation des Postes Web ; copier `{app}\Web\certificat-serveur.cer` sur une clé USB peut servir pour un poste sous Windows 7 qui ne joint pas le serveur en HTTPS (section 5.5).
5. **Validation sur téléphone** (si le module est utilisé ; détail dans le manuel, Administration › « Service de validation à distance ») :
    1. installer **Tailscale** sur le serveur, le relier au réseau Tailscale (tailnet) du laboratoire ; dans la console d'administration Tailscale (DNS), activer **MagicDNS** et **HTTPS Certificates** ;
    2. sur le serveur : `tailscale serve --bg 5080` ; l'adresse publiée est `https://<machine>.<tailnet>.ts.net`, conservée aux redémarrages (`tailscale serve status` l'affiche, `tailscale serve --https=443 off` la retire) ;
    3. **ne jamais** utiliser `tailscale funnel`, qui publierait le service sur Internet ;
    4. sur chaque téléphone : application Tailscale, connexion au tailnet, ouverture de l'adresse publiée, ajout de « MarocLIS Validation » à l'écran d'accueil ;
    5. dans MarocLIS, **Rôles et droits** : droit `results.validate_remote` pour les biologistes concernés (avec `results.validate_biological` et une qualification de biologiste valide).
6. **Une copie hors du serveur** des sauvegardes : un partage sur une autre machine, ou une copie régulière sur un disque externe rangé ailleurs (section 7).

## 5. Installer les postes de travail

### 5.1 Quel type pour quel poste

- **Poste Windows** (Windows 10 ou 11, 64 bits) : l'application MarocLIS, reliée directement à la base du serveur. Toutes les fonctions, y compris la conduite des automates (`Instruments:EnableInstrumentHost`) et l'impression directe des étiquettes. Sections 5.2 à 5.4.
- **Poste Web** (Windows 7 SP1 à 11, 32 ou 64 bits) : MarocLIS dans le navigateur, servi par le service `MarocLIS.Web` du serveur. Rien de MarocLIS ne s'exécute sur le poste. C'est le seul type possible sous Windows 7, 8 et 8.1 ; il convient aussi à un poste sous Windows 10 ou 11 où l'on ne veut rien installer de plus qu'un raccourci. Section 5.5.

Les deux types peuvent coexister dans un laboratoire : ils travaillent sur la même base, avec les mêmes comptes MarocLIS et la même licence.

### 5.2 Poste Windows : avant de commencer

- Le serveur est installé et MarocLIS y a été démarré une fois (la base existe et contient ses tables).
- Le compte Windows de chaque utilisateur du poste est membre du groupe `MarocLIS-Utilisateurs` du serveur (ou, en groupe de travail, a son jumeau de même nom et même mot de passe sur le serveur).
- Notez le **nom de l'ordinateur serveur** et l'**instance** (`SQLEXPRESS` si l'installation l'a installée).

### 5.3 Poste Windows : les pages de l'assistant

1. **Langue**, puis **Dossier de destination** (`C:\MarocLIS` par défaut).
2. **Type d'installation** : **« Poste Windows : application MarocLIS (Windows 10 ou 11) »**. Rien d'autre n'est à installer : l'application contient tout ce qu'il lui faut, .NET compris. Sur un Windows antérieur à Windows 10 ou en 32 bits, ce type est refusé : choisissez « Poste Web » (5.5).
3. **Serveur du laboratoire** :
    - **Nom du serveur** : le nom de l'ordinateur serveur, obligatoire, sans espace, `\`, `;` ni guillemet (l'instance se saisit à part) ;
    - **Instance SQL Server** : `SQLEXPRESS` par défaut ; laissez vide ou saisissez `MSSQLSERVER` pour l'instance par défaut ;
    - **Tester la connexion** : ouvre la base `MarocLIS` du serveur indiqué (délai 10 secondes) avec **le compte Windows qui exécute l'installation**, pas forcément celui de l'utilisateur final. En cas d'échec, le message donne la raison ; vous pouvez continuer, la chaîne sera écrite telle quelle.
    - Si le poste a déjà un `{app}\Desktop\appsettings.json` (réinstallation, mise à jour), les champs sont grisés et la page indique « Ce poste a déjà un fichier appsettings.json : sa chaîne de connexion est conservée. »
4. **Licence MarocLIS** (facultative) : la page est aussi proposée sur un Poste Windows. Un fichier indiqué ici est copié en `{app}\Desktop\licence.lic` et importé au premier démarrage de ce poste si la base n'a pas encore de licence. La licence étant enregistrée dans la base, une seule pour le serveur et tous les postes, laissez la page vide si elle a déjà été installée depuis le serveur (section 8).
5. **Tâches supplémentaires** (icône sur le Bureau), **Prêt à installer** (chaîne de connexion qui sera écrite), **Installation**, **Fin** : « MarocLIS est installé sur ce poste. Il utilise la base du serveur indiqué ; l'utilisateur Windows doit faire partie du groupe « MarocLIS-Utilisateurs » du serveur. »

### 5.4 Comment le Poste Windows trouve le serveur

L'installation écrit dans `{app}\Desktop\appsettings.json` :

```
Server=<nom du serveur>\<instance>;Database=MarocLIS;Integrated Security=true
```

(`Server=<nom du serveur>;…` pour l'instance par défaut). Une instance nommée est localisée par le service **SQL Server Browser** du serveur (UDP 1434), activé et ouvert par l'installation serveur.

Pour changer de serveur plus tard, modifiez la clé `Database:ConnectionString` de ce fichier (l'installation ne la réécrit jamais une fois le fichier présent). Une variable d'environnement de la machine peut aussi la remplacer : MarocLIS lit les variables préfixées `MAROCLIS_`, par exemple `MAROCLIS_Database__ConnectionString`.

Un Poste Windows n'a ni service, ni tâche planifiée, ni règle de pare-feu MarocLIS.

### 5.5 Postes sous Windows 7, 8 et 8.1 (type « Poste Web »)

L'application Windows de MarocLIS et ses services sont construits sur .NET 10, que Microsoft ne fournit que pour Windows 10 et ultérieurs. Un poste sous Windows 7, 8 ou 8.1 travaille donc avec la **version Web** : les mêmes écrans, dans le navigateur, servis par le serveur du laboratoire (section 4.8). Le type **« Poste Web »** du programme d'installation prépare le poste pour cela : il ne copie aucun programme MarocLIS, seulement ce qu'il faut pour que le navigateur ouvre MarocLIS correctement et en confiance.

#### Ce qui est installé selon la version de Windows

| Windows du poste | Types proposés par l'assistant | Ce que le type « Poste Web » installe |
|---|---|---|
| Windows 7 sans Service Pack 1 | Aucun : Windows refuse de lancer le programme (`MinVersion=6.1sp1`) | — Installez d'abord le Service Pack 1 par Windows Update. |
| Windows 7 SP1, 32 ou 64 bits | **Poste Web** seul, présélectionné ; « Serveur » et « Poste Windows » refusés avec la raison | Le certificat du serveur ; **Firefox ESR 115** si aucun navigateur assez récent n'est présent ; la configuration de Firefox ; le raccourci « MarocLIS » |
| Windows 8 et 8.1, 32 ou 64 bits | **Poste Web** seul, présélectionné ; les deux autres types refusés | Comme sous Windows 7 SP1 |
| Windows 10 ou 11, 32 bits | **Poste Web** seul (l'application Windows est construite pour x64) | Le certificat ; le navigateur présent (Edge, toujours assez récent sous Windows 10 et 11) ; le raccourci |
| Windows 10 ou 11, 64 bits | Serveur, Poste Windows et Poste Web | Pour un Poste Web : le certificat ; le navigateur présent (Edge le plus souvent) ; la configuration de Firefox s'il est présent ; le raccourci |
| Windows Server 2016 et ultérieurs, 64 bits | Serveur, Poste Windows et Poste Web | Comme sous Windows 10, 64 bits |
| Windows Server 2008 R2, 2012, 2012 R2 | **Poste Web** seul | Comme sous Windows 7 SP1 |

**Navigateurs qui conviennent** : Google Chrome ou Microsoft Edge (Chromium) **109** ou plus récent, Mozilla Firefox **115** ou plus récent. Ce sont les dernières versions publiées pour Windows 7, 8 et 8.1 ; Internet Explorer et l'ancien Edge (avant 2020) ne conviennent pas. L'assistant cherche Edge, puis Chrome, puis Firefox, et retient le premier assez récent ; s'il n'en trouve aucun, il installe **Firefox ESR 115**, inclus dans le programme d'installation (version vérifiée à la construction, section 3.3). Chrome 109 et Edge 109 ne reçoivent plus de correctifs de sécurité sous ces systèmes ; Firefox ESR 115 en a reçu plus longtemps (consultez le site de Mozilla pour sa fin de prise en charge) et se met à jour seul quand le poste a Internet.

#### Avant de commencer

- Le serveur est installé, avec la version Web (section 4.8), et allumé.
- Vous avez l'**empreinte SHA-256** du certificat du serveur, affichée à la fin de l'installation du serveur et écrite dans `C:\MarocLIS\Web\certificat-serveur.txt` sur le serveur.
- Chaque utilisateur a un **compte MarocLIS** (créé dans MarocLIS, **Utilisateurs**). Le compte Windows du poste ne joue aucun rôle : il n'a pas à faire partie du groupe `MarocLIS-Utilisateurs`.
- Sous Windows 7 : le poste doit pouvoir joindre le serveur en HTTPS (TLS 1.2) pour y demander le certificat. Un Windows 7 qui n'a pas reçu ses mises à jour (notamment KB3140245) peut échouer à cette étape ; copiez alors `C:\MarocLIS\Web\certificat-serveur.cer` du serveur sur une clé USB (ou depuis le partage `\\<serveur>\MarocLIS` s'il existe). Le navigateur, lui, a son propre TLS et n'est pas concerné.

#### Les pages de l'assistant

1. **Langue**, puis **Dossier de destination** (`C:\MarocLIS` par défaut ; seule l'icône du raccourci y est copiée, avec le journal de l'installation).
2. **Type d'installation** : **« Poste Web : MarocLIS dans le navigateur (Windows 7 à 11) »**, présélectionné sous Windows 7, 8 et 8.1.
3. **Serveur MarocLIS Web** :
    - **Nom ou adresse du serveur** : le nom de l'ordinateur serveur (`SERVEUR-LABO`), son nom DNS complet ou son adresse IP (`192.168.1.10`) ; lettres, chiffres, points et tirets seulement, sans `https://` ni numéro de port. C'est l'adresse qu'ouvrira le raccourci : `https://<nom>:5443/`.
    - **Vérifier le serveur** : demande au serveur son certificat (`https://<nom>:5443/certificat-serveur.pem`) et affiche son empreinte SHA-256.
    - **Ou le fichier certificat-serveur.cer copié du serveur** (facultatif) : quand le poste ne joint pas le serveur en HTTPS, indiquez ici le fichier copié depuis le serveur ; il est alors utilisé à la place de la demande au serveur. Fichier `.cer`, `.crt` ou `.pem`, binaire ou texte « BEGIN CERTIFICATE », de moins de 64 Ko.
    - La page indique aussi le **navigateur** que le raccourci utilisera : « Navigateur : Google Chrome 109 convient… », ou « Navigateur : Google Chrome 100, trop ancien ; Firefox ESR 115 sera installé… » (« aucun navigateur récent sur ce poste » quand il n'y a qu'Internet Explorer).
    - **Suivant** affiche l'empreinte SHA-256 du certificat reçu et demande si elle est **identique** à celle du serveur. Comparez-la caractère par caractère : **Non** (le choix par défaut) refuse le certificat et laisse sur la page ; une empreinte différente signifie qu'un autre ordinateur répond à la place du serveur, ou que le nom saisi est le mauvais.
4. **Tâches supplémentaires** : **Créer une icône sur le Bureau**. Le raccourci « MarocLIS » est toujours créé dans le menu Démarrer.
5. **Prêt à installer** : le serveur (`https://<nom>:5443/`), l'empreinte du certificat qui sera ajouté aux autorités de confiance de Windows, le navigateur du raccourci (ou « Firefox ESR 115, installé par ce programme »), et, si Firefox est utilisé, ses réglages.
6. **Installation**, puis **Fin** : « MarocLIS est prêt sur ce poste : le raccourci « MarocLIS » (bureau et menu Démarrer) ouvre https://<nom>:5443/ dans <navigateur>. » La case **Lancer MarocLIS** ouvre aussitôt la page de connexion.

#### Ce que fait l'installation sur le poste

1. **Firefox ESR 115**, s'il le faut, avant la copie des fichiers : installation silencieuse (`/S`) du programme officiel de Mozilla, dans `C:\Program Files\Mozilla Firefox` (ou `Program Files (x86)`). En cas d'échec, le raccourci ouvrira le navigateur par défaut, et la liste « À vérifier » le signale.
2. **Certificat du serveur** : copié dans `C:\ProgramData\MarocLIS\certificat-serveur.cer`, puis ajouté au magasin **Autorités de certification racines de confiance de l'ordinateur** (`certutil -f -addstore Root`). Chrome et Edge le lisent là.
3. **Firefox** (chaque Firefox 115 ou plus récent du poste, installé ou non par MarocLIS) : un fichier de stratégies `distribution\policies.json` à côté de `firefox.exe`, qui lui fait reconnaître le certificat du serveur et les autorités de Windows (`Certificates`, `ImportEnterpriseRoots`), ouvre MarocLIS comme page d'accueil, et lui épargne les questions du premier lancement et du navigateur par défaut. Les mises à jour de Firefox restent actives. Un `policies.json` déjà présent et qui n'a pas été écrit par MarocLIS est **conservé** ; l'installation le signale, et il faut alors, dans Firefox, **Paramètres › Vie privée et sécurité › Certificats**, cocher « Autoriser Firefox à faire confiance aux certificats racines tiers ».
4. **Raccourcis « MarocLIS »** (menu Démarrer, et Bureau si demandé) : le navigateur retenu, avec l'adresse `https://<nom>:5443/`, et l'icône MarocLIS.

Aucun service, aucune tâche, aucune règle de pare-feu, aucun runtime .NET, aucun PowerShell n'est utilisé sur un Poste Web. Le journal est `{app}\Installer\install.log`.

#### Ce que voit l'utilisateur

Le raccourci ouvre la **page de connexion** de MarocLIS ; l'utilisateur s'identifie avec son compte MarocLIS. Les écrans sont ceux de la version Web (en français, anglais ou arabe), sans alerte de certificat. Si le navigateur est trop ancien (par exemple un Chrome resté en version 100), la page de connexion l'annonce et nomme les navigateurs qui conviennent.

Pour changer de serveur, ou après un renouvellement du certificat du serveur (4.8), relancez simplement le programme d'installation avec le type « Poste Web ».

## 6. Premier démarrage

Au lancement, MarocLIS suit cet ordre (`src/MarocLIS.Desktop/Program.cs`) :

1. **Base de données.** Lecture de `appsettings.json`, puis création ou mise à jour du schéma (migrations) et des données de référence. Le premier démarrage crée les tables et un seul compte administrateur. Plusieurs postes démarrés ensemble attendent leur tour. En cas d'échec, un message s'affiche et MarocLIS se ferme (section 11.4).
2. **Licence.** Si aucune licence n'est enregistrée et qu'un fichier `licence.lic` se trouve à côté du programme (`{app}\Desktop\licence.lic`, posé par l'assistant), il est importé. Puis :
    - base sans patient ni dossier et sans licence : l'écran **« Aucune licence installée »** s'ouvre à la place de la connexion (identifiant serveur, **Copier**, **Exporter la demande**, **Importer une licence…**) ; le quitter ferme MarocLIS ;
    - base qui contient déjà des patients ou des dossiers : jamais d'écran bloquant ; sans licence valide, MarocLIS s'ouvre en lecture seule.
3. **Langues** actives du laboratoire.
4. **Polices des comptes rendus.** Si la police latine (`Carlito` par défaut) ou, quand l'arabe est actif, la police arabe (`Amiri`) est introuvable, un avertissement s'affiche une fois avant la connexion :
    - « Police(s) de compte rendu absente(s) : … Les comptes rendus seront édités dans une police de substitution : la mise en page sera dégradée, le contenu restera juste. »
    - « Police(s) de compte rendu absente(s) : … Les comptes rendus en arabe ne pourront pas être édités depuis ce poste tant que la police arabe n'est pas installée. »

    Les polices sont cherchées dans `{app}\Desktop\Assets\Fonts` (livrées par l'installation) et parmi les polices installées dans Windows.

5. **Connexion.** Au premier démarrage : identifiant **`admin`**, mot de passe initial **`Maroc-LIS-2026!`**. Ce compte doit changer son mot de passe avant de pouvoir faire quoi que ce soit. Règles par défaut (`PasswordPolicy`) : 10 caractères au moins, majuscule, minuscule, chiffre et caractère spécial ; expiration 180 jours ; 5 derniers mots de passe interdits ; blocage 15 minutes après 5 échecs ; déconnexion après 15 minutes d'inactivité.

Ensuite, la mise en service se poursuit dans l'application, dans l'ordre que décrit le manuel (Administration › « Vue d'ensemble : mettre en service une installation ») : fiche du site, numérotations, utilisateurs, catalogue, automates, sauvegardes.

## 7. Sauvegardes et restauration

### 7.1 Principe

SQL Server Express n'a pas d'Agent SQL. L'installation serveur enregistre donc la tâche planifiée Windows **« MarocLIS - Sauvegarde »**, qui exécute `{app}\Sauvegarde\maroclis-backup.ps1` sous le compte **SYSTEM**, **toutes les 15 minutes** et **au démarrage** du serveur. À chaque passage, la tâche :

1. ne fait rien si la base n'existe pas, ou si MarocLIS n'a jamais été démarré (schéma absent) ;
2. écrit dans la base les valeurs initiales de l'assistant, seulement pour les réglages absents ;
3. note son passage (affiché dans **Paramètres › Sauvegardes**) ;
4. sauvegarde si un horaire prévu est passé depuis le dernier horaire traité : `BACKUP DATABASE … WITH CHECKSUM` (compression seulement sur les éditions qui la proposent, pas Express), puis `RESTORE VERIFYONLY` ; un horaire manqué (serveur éteint) est rattrapé une seule fois s'il date de moins de 24 heures ; un horaire en échec est signalé, pas retenté toutes les 15 minutes ;
5. vérifie les sauvegardes faites depuis un poste, que le compte des postes peut écrire mais pas vérifier ;
6. supprime les sauvegardes plus anciennes que la conservation, **jamais la dernière sauvegarde vérifiée**, et seulement les fichiers qu'elle reconnaît (`MarocLIS_AAAAMMJJ_HHMMSS.bak`, avec un suffixe `_manuelle` ou `_avant-restauration` pour les autres origines) ;
7. inscrit chaque sauvegarde réussie ou échouée au journal de sécurité de MarocLIS ;
8. tient un journal mensuel `{app}\Sauvegarde\logs\maroclis-backup-AAAAMM.log`.

Les heures sont celles de l'horloge du serveur SQL.

### 7.2 Valeurs par défaut et réglage

| Réglage | Par défaut | Limites |
|---|---|---|
| Sauvegarde automatique activée | oui | — |
| Dossier | `C:\MarocLIS\Sauvegardes` | chemin complet `X:\…` ou partage `\\…`, 200 caractères au plus, jamais une lettre de lecteur réseau |
| Heures | `22:00` | 1 à 6 heures par jour, `HH:mm`, séparées par des virgules |
| Jours | tous | au moins un jour |
| Conservation | 14 jours | 1 à 365 jours |
| Alerte « dernière sauvegarde trop ancienne » | 48 heures | — |

Ces réglages sont gardés dans la base (et non dans la tâche) : ils se changent depuis n'importe quel poste, dans **Paramètres › Sauvegardes**, par un utilisateur qui a le droit **`admin.backup`** (livré au seul rôle Administrateur). Un changement est pris en compte au plus tard 15 minutes après, sans toucher au Planificateur de tâches. **Tester le dossier** fait écrire par SQL Server un petit fichier de test dans le dossier, avec son propre compte de service.

Pour un utilisateur qui a `admin.backup`, la barre d'état affiche en rouge « ⚠ Dernière sauvegarde : … » quand la dernière sauvegarde réussie a plus de **48 heures**, ou « ⚠ Aucune sauvegarde de la base ».

Les sauvegardes restent possibles même quand MarocLIS est en lecture seule faute de licence.

### 7.3 Sauvegarde manuelle

**Dans MarocLIS** : **Paramètres › Sauvegardes › Sauvegarder maintenant**, depuis n'importe quel poste (droit `admin.backup`). Depuis un poste, la vérification est souvent laissée à la tâche du serveur. À faire notamment **avant une mise à jour** ou une reprise de données.

**Sur le serveur, en dehors de MarocLIS** : le script de la tâche accepte `-Now`, qui sauvegarde aussitôt quel que soit l'horaire. Dans un PowerShell lancé en administrateur (adaptez le dossier et l'instance) :

```
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\MarocLIS\Sauvegarde\maroclis-backup.ps1" -Server "localhost\SQLEXPRESS" -Now
```

Le script se connecte alors avec votre compte Windows.

### 7.4 Restauration

La restauration remplace **toute** la base par une sauvegarde : ce qui a été saisi depuis est perdu, et tous les postes sont déconnectés. Elle n'est proposée que si :

1. MarocLIS est ouvert **sur le serveur** (la machine où tourne SQL Server) ;
2. l'utilisateur Windows est **administrateur de ce poste** ;
3. son compte a le droit SQL de remplacer la base (propriétaire, `dbcreator` ou `sysadmin`). Sur un serveur installé par MarocLIS, les administrateurs Windows sont `sysadmin` quand MarocLIS est lancé « en tant qu'administrateur » : si le droit manque, MarocLIS propose de se relancer ainsi (élévation Windows).

Étapes : choisir la sauvegarde dans **Paramètres › Sauvegardes**, **Restaurer…**, **saisir le nom du laboratoire** (celui de la fiche du site principal ; le bouton **Restaurer** reste grisé tant qu'il ne correspond pas, **Entrée** ne restaure jamais). MarocLIS relit la sauvegarde, fait une **sauvegarde de sécurité** de la base actuelle (`…_avant-restauration.bak` ; si elle échoue, rien n'est restauré), ferme la base à tous, la remplace, la rouvre, applique les migrations si la sauvegarde vient d'une version plus ancienne, puis redémarre. Les autres postes doivent redémarrer MarocLIS. Fermez aussi le service de validation à distance si la base ne peut pas être prise en accès exclusif.

Une sauvegarde endommagée ou dont la vérification a échoué ne peut pas être restaurée.

## 8. Licence

### Partie 1 — Installer la licence d'un laboratoire

#### Ce qu'il faut savoir avant

- La licence est un fichier `.lic` signé par l'éditeur. Elle est enregistrée dans la base de données : une seule licence pour le serveur et tous ses postes.
- Une licence d'exploitation est **liée au serveur** : elle porte l'identifiant du serveur pour lequel elle a été émise. Une licence d'essai ou de formation ne l'est généralement pas, elle peut être installée avant le serveur définitif.
- La licence fixe le nombre de **postes** (ordinateurs connectés en même temps ; les téléphones ne comptent pas) et les **modules** ouverts en plus du socle.

#### L'identifiant serveur

Il se lit dans MarocLIS (écran d'accueil sans licence, ou **Paramètres › Licence**), sous la forme `A1B2-C3D4-E5F6-0718-293A`. Il est calculé à partir de trois éléments que SQL Server fournit, et non du matériel de l'ordinateur :

- le nom Windows de la machine qui fait tourner SQL Server, tel que SQL Server le déclare (`SERVERPROPERTY('MachineName')`) ;
- le nom de l'instance SQL Server (`SERVERPROPERTY('InstanceName')`, « MSSQLSERVER » pour l'instance par défaut) ;
- un identifiant tiré au hasard et écrit dans la base MarocLIS la première fois qu'elle est lue.

L'empreinte SHA-256 de ces trois valeurs, tronquée à 10 octets, donne l'identifiant.

Pourquoi ce choix :

- **Tous les postes lisent le même identifiant**, quel que soit le nom ou l'adresse par lesquels ils joignent le serveur (adresse IP, alias, « localhost » sur le serveur lui-même) : c'est SQL Server qui répond, pas le poste.
- **Remplacer un disque, une carte réseau, de la mémoire** ne change rien : aucun de ces éléments n'entre dans le calcul. Restaurer une sauvegarde de la base sur le même serveur ne change rien non plus.
- **Copier la base et l'application sur un autre serveur** change le nom de la machine : l'identifiant change, la licence n'y est plus valide. Installer une seconde base vierge sur le même serveur change l'identifiant de base : une licence ne sert pas deux bases.
- Renommer l'ordinateur serveur change aussi l'identifiant : il faut alors redemander la licence (comme pour un changement de serveur).

#### Pendant l'installation

Le programme d'installation propose une page **Licence MarocLIS** (facultative). Si vous avez déjà le fichier `.lic` (licence d'essai, de formation, ou réinstallation sur le même serveur), indiquez-le : il est copié à côté de MarocLIS et importé automatiquement au premier démarrage. Sinon laissez vide.

#### Au premier démarrage

1. Sur une base vide et sans licence, MarocLIS s'ouvre sur l'écran **Aucune licence installée** au lieu de la connexion.
2. **Exporter la demande** : enregistre `demande-licence-<site>.demande` (identifiant serveur, nom du site, version de MarocLIS). Envoyez ce fichier au fournisseur. À défaut, **Copier** l'identifiant et le lui transmettre.
3. Le fournisseur renvoie un fichier `licence.lic`. **Importer une licence…** : MarocLIS s'ouvre aussitôt sur la connexion, sans redémarrer.
4. Plus tard, une nouvelle licence (renouvellement, postes ou modules ajoutés) s'importe dans **Paramètres › Licence** avec le droit `admin.license` (administrateur). Elle est en vigueur immédiatement sur le poste qui l'importe, et sur les autres au plus tard cinq minutes après.

Une base qui contient déjà des patients n'affiche jamais cet écran bloquant : sans licence valide, elle s'ouvre en **lecture seule** (consultation, réimpression des comptes rendus, encaissements), et l'administrateur importe la licence depuis **Paramètres › Licence**. Le détail des états (préavis, grâce, lecture seule) est au chapitre « Administration » du manuel, section « Licence ».

#### Changer de serveur

1. Installer MarocLIS sur le nouveau serveur et y restaurer la base.
2. Au démarrage, MarocLIS signale que la licence a été émise pour un autre serveur et passe en lecture seule.
3. **Paramètres › Licence › Exporter la demande**, envoyer la demande au fournisseur, importer la licence reçue (même numéro, version suivante).

À noter (constaté dans le code) : sur le nouveau serveur, la base neuve créée par le premier démarrage ne contient ni patient ni licence, et MarocLIS s'ouvre donc d'abord sur l'écran « Aucune licence installée » ; l'onglet **Sauvegardes**, et sa restauration, ne sont accessibles qu'après la connexion. Il faut donc soit importer d'abord une licence que ce serveur accepte (par exemple une licence d'essai non liée au serveur), soit restaurer la base avec les outils de SQL Server, en dehors de MarocLIS. L'identifiant serveur est recalculé après la restauration (il dépend de l'identifiant écrit dans la base restaurée) : c'est la demande exportée **après** la restauration qu'il faut envoyer.

### Partie 2 — Pour l'éditeur : la clé de licence et le Générateur

Cette partie ne concerne pas les laboratoires. Le **Générateur de licences** (`tools/MarocLIS.LicenseGenerator`) est un outil de l'éditeur : il n'est ni dans le programme d'installation, ni livré aux laboratoires.

#### Principe

- Chaque licence est signée en ECDSA (courbe P-256, SHA-256) avec la **clé privée** de l'éditeur. Le fichier contient les termes de la licence en JSON lisible et la signature à part ; la signature porte sur une forme canonique des termes, si bien qu'un seul caractère modifié la rend invalide.
- MarocLIS vérifie la signature avec la **clé publique** compilée dans l'application (`src/MarocLIS.Application/Licensing/licence-public-key.pem`). La clé privée n'est jamais dans le dépôt de code.
- Le dépôt contient une **clé de DÉVELOPPEMENT** (empreinte `CDC1-D2A2-222A-4A92-B835-95FB-909A-18BB`), dont la clé privée est dans `tests/MarocLIS.Tests/Licensing/` pour les tests. N'importe qui ayant le code peut signer des licences qu'elle accepte : une version compilée avec elle ne doit jamais être installée chez un laboratoire. MarocLIS l'affiche en clair dans **Paramètres › Licence**, et `installer/build-installer.ps1` refuse de construire le programme d'installation avec elle.

#### Cérémonie de la clé (une seule fois)

1. **Choisir l'ordinateur** qui gardera la clé : un poste de l'éditeur, à jour, avec un compte Windows personnel, chiffrement du disque activé (BitLocker), sans partage réseau du profil.
2. **Choisir la phrase de passe** : 12 caractères au moins, une phrase plutôt qu'un mot. Elle n'est enregistrée nulle part ; l'écrire sur papier et la ranger au coffre, à part de toute sauvegarde.
3. Lancer le Générateur (`MarocLIS.GenerateurLicences.exe`) : il s'ouvre sur l'onglet **Clé de signature**. **Créer la clé…**, saisir deux fois la phrase de passe. La clé privée est écrite chiffrée (PKCS#8, AES-256, phrase de passe étirée par PBKDF2-SHA-256 sur 600 000 itérations) dans `%APPDATA%\MarocLIS\GenerateurLicences\cle-privee.pem`, la clé publique à côté. Noter l'**empreinte** affichée.
4. **Exporter une sauvegarde chiffrée…** : choisir la phrase de passe de la sauvegarde (la même ou une autre, elle aussi notée au coffre), enregistrer le fichier `.cle-sauvegarde` sur **deux supports** gardés en deux lieux distincts (clé USB au coffre, second exemplaire hors des locaux). Sans cette sauvegarde, une panne de l'ordinateur rend impossible toute nouvelle licence pour les installations existantes : il faudrait livrer à chacune une version de MarocLIS avec une nouvelle clé.
5. **Exporter la clé publique…** : enregistrer `licence-public-key.pem`, le copier sur `src/MarocLIS.Application/Licensing/licence-public-key.pem` dans le code source, et valider ce changement dans le dépôt (la clé publique n'est pas secrète).
6. Construire le programme d'installation : `installer\build-installer.ps1` affiche l'empreinte de la clé compilée ; elle doit être celle notée à l'étape 3. Avec la clé de développement, le script s'arrête et explique la marche à suivre (`-AllowDevelopmentKey` ne sert qu'aux essais internes et produit `MarocLIS-Setup-<version>-CLE-DEV.exe`).
7. Après installation chez un premier laboratoire, vérifier dans **Paramètres › Licence › Clé de vérification** que l'empreinte est la bonne.

Restaurer la clé sur un autre ordinateur : **Restaurer une sauvegarde…** (phrase de passe de la sauvegarde, puis nouvelle phrase de passe locale). Le Générateur refuse d'écraser une autre clé déjà présente.

#### Émettre une licence

- **Nouvelle licence…** ou **Depuis une demande…** (le fichier `.demande` du laboratoire remplit l'identifiant serveur, le site et le laboratoire). Le numéro suit la série de l'année (`LIC-2026-0001`, …).
- Le **pack** (Essentiel 2 postes, Professionnel 5 postes, Complet 10 postes) ne fait que préremplir les postes et les modules ; les modules restent cochables un à un, et le fichier les liste toujours un par un. Les packs se modifient dans l'onglet **Packs**.
- Valeurs proposées, modifiables pour chaque licence : essai de 30 jours ; préavis 30 jours avant la fin ; grâce 15 jours après ; 7 jours de paillasse en lecture seule ; liaison au serveur cochée pour une licence annuelle ou perpétuelle, décochée pour l'essai et la formation ; licence perpétuelle avec une date « mises à jour jusqu'au » (un an par défaut) ; licence de formation à un poste, sans fin.
- **Signer et enregistrer…** : la phrase de passe est demandée une fois par session ; la licence est relue et vérifiée avec la clé publique avant d'être inscrite au registre, puis enregistrée sous `LIC-…-vN.lic`.

#### Remplacer, renouveler, révoquer

- **Remplacer…** : même numéro, version suivante (postes ou modules ajoutés, nouveau serveur). MarocLIS remplace une licence par une version supérieure du même numéro et refuse ensuite les versions antérieures.
- Onglet **Renouvellements** : les licences en vigueur dont la fin (ou la fin des mises à jour d'une perpétuelle) tombe dans les 45 jours, et celles déjà échues non renouvelées, avec le contact noté sur la licence. **Renouveler…** prépare la version suivante sur un an de plus ; **Exporter en CSV…** donne la liste de relance.
- **Révoquer…** (impayé, fichier perdu ou volé) : la ligne de révocation est copiée ; l'ajouter à `src/MarocLIS.Application/Licensing/licence-revoked.txt` et publier une nouvelle version de MarocLIS. Les versions publiées ensuite refusent cette licence (une version précise `LIC-2026-0042/1`, ou toutes `LIC-2026-0042`).

#### Fichiers du Générateur

Dans `%APPDATA%\MarocLIS\GenerateurLicences\` (ou le dossier donné par `--dossier` ou la variable `MAROCLIS_GENERATEUR_DOSSIER`) : `cle-privee.pem` (chiffrée), `cle-publique.pem`, `registre-licences.json` (licences émises, avec leur fichier signé) et `packs.json`. Sauvegarder le registre avec la sauvegarde habituelle de l'éditeur ; il ne contient aucun secret.

## 9. Mise à jour vers une nouvelle version

Une nouvelle version s'installe en lançant simplement le nouveau `MarocLIS-Setup-<version>.exe`, sur le serveur puis sur chaque poste. Le programme garde le même identifiant d'application : il reconnaît l'installation existante et reprend son dossier, son type (Serveur, Poste Windows ou Poste Web) et les valeurs saisies la fois précédente.

Ce qui est conservé :

- les fichiers `appsettings.json` de `{app}\Desktop`, `{app}\Api` et `{app}\Web` (jamais remplacés : les nouveaux réglages éventuels du fichier livré ne sont donc pas ajoutés d'eux-mêmes) ;
- la base de données et ses données ;
- les réglages de sauvegarde faits dans MarocLIS (la page Sauvegarde de l'assistant ne sert alors pas ; la tâche planifiée est réenregistrée avec les mêmes chemins) ;
- la licence, qui est dans la base ;
- le certificat HTTPS du serveur, réutilisé tant qu'il lui reste plus de 30 jours : les Postes Web n'ont rien à refaire.

Déroulement :

1. **Avant** : faites une sauvegarde (**Paramètres › Sauvegardes › Sauvegarder maintenant**) et fermez MarocLIS (l'assistant propose de fermer les applications ouvertes).
2. **Sur le serveur** : lancez le programme. Les services `MarocLIS.Api` et `MarocLIS.Web` sont arrêtés avant la copie, puis redémarrés ; la base, les accès, le pare-feu et la tâche de sauvegarde sont reconfigurés de la même façon qu'à l'installation (opérations sans effet si tout est déjà en place).
3. **Mettre la base à jour** : le schéma n'est pas modifié par le programme d'installation, mais par **le premier MarocLIS de la nouvelle version qui démarre** (migrations appliquées au démarrage, depuis n'importe quel Poste Windows, le serveur compris). Le service `MarocLIS.Web`, redémarré à la fin de l'installation du serveur, applique lui aussi les migrations à son démarrage : c'est en général lui qui met la base à jour. Le service `MarocLIS.Api` ne met pas la base à jour.
4. **Redémarrer le service `MarocLIS.Api`** après ce premier démarrage (Services Windows, ou `sc.exe stop MarocLIS.Api` puis `sc.exe start MarocLIS.Api`). La dernière page de l'assistant le rappelle : « Mise à jour : lancez d'abord MarocLIS sur un poste (il met la base à jour), puis redémarrez le service MarocLIS.Api. » Les téléphones doivent se reconnecter.
5. **Sur chaque Poste Windows** : lancez le programme ; la chaîne de connexion existante est conservée. **Sur un Poste Web**, rien n'est à mettre à jour : il reçoit la nouvelle version du serveur à la connexion suivante (rechargez la page si elle était ouverte pendant la mise à jour).

Une licence perpétuelle ne couvre les mises à jour que jusqu'à sa date « mises à jour jusqu'au » : au-delà, MarocLIS le signale (section 11.7).

## 10. Désinstallation

Par **Paramètres Windows › Applications** (ou Programmes et fonctionnalités), entrée **MarocLIS**.

Ce qui est retiré :

- le service `MarocLIS.Api` (arrêté puis supprimé, sur un serveur) ;
- le service `MarocLIS.Web` et sa règle de pare-feu « MarocLIS - Web (HTTPS 5443) » (sur un serveur) ;
- la tâche planifiée « MarocLIS - Sauvegarde » (sur un serveur) ;
- les fichiers installés par le programme dans `{app}` (`Desktop`, `Api`, `Web`, `Sauvegarde`, l'icône d'un Poste Web), les raccourcis, et le dossier `{app}\Installer` (journal d'installation).

Ce qui est conservé, volontairement :

- la **base de données** `MarocLIS` et **SQL Server** lui-même ;
- les **fichiers de sauvegarde**, où qu'ils soient ;
- ce que le programme d'installation n'a pas copié lui-même, notamment les journaux (`logs`), le fichier `licence.lic` copié par l'assistant et le certificat exporté `{app}\Web\certificat-serveur.cer` : le dossier qui les contient reste en place ;
- sur le serveur, le **certificat HTTPS** dans les magasins `LocalMachine\My` et `Root` : une réinstallation le reprend, et les Postes Web qui lui font confiance continuent de fonctionner. Pour le retirer : `certlm.msc`, certificats « MarocLIS Web » des dossiers Personnel et Autorités de certification racines de confiance ;
- sur un Poste Web, **Firefox ESR** (il se désinstalle à part, comme tout programme), son `distribution\policies.json`, le certificat du serveur dans les autorités de confiance de Windows et sa copie `C:\ProgramData\MarocLIS\certificat-serveur.cer`.

Le code de désinstallation ne retire pas non plus le groupe local `MarocLIS-Utilisateurs`, les connexions SQL Server, ni les règles de pare-feu du groupe « MarocLIS » : retirez-les à la main si le serveur ne sert plus à MarocLIS.

**Attention** : avec les valeurs par défaut, le dossier des sauvegardes `C:\MarocLIS\Sauvegardes` est **dans** le dossier d'installation `C:\MarocLIS`. Ne supprimez pas ce dossier à la main sans avoir mis les sauvegardes à l'abri.

## 11. Dépannage

### 11.1 Construction du programme d'installation

| Symptôme | Cause et solution |
|---|---|
| PowerShell refuse d'exécuter le script (exécution de scripts désactivée) | Lancer `powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1`. |
| « MarocLIS embeds the DEVELOPMENT licence key … » | Clé de développement compilée : remplacer la clé publique (section 8, partie 2), ou `-AllowDevelopmentKey` pour un essai interne. |
| « ISCC.exe (Inno Setup 6) not found. … » | Installer Inno Setup 6, ou passer `-Iscc`, ou définir `INNO_SETUP_ISCC`. |
| « Inno Setup … found (…): use Inno Setup 6.3 or later 6.x … » | Installer une version 6.3 ou ultérieure de la série 6 (6.7.3 essayée). |
| « Firefox ESR … could not be downloaded from … » / « No 115.x ESR release found … » | Pas d'Internet ou site de Mozilla injoignable : télécharger à la main le programme français 32 bits de Firefox ESR 115 sur `https://ftp.mozilla.org/pub/firefox/releases/` et l'enregistrer sous `installer\prereqs\Firefox-Setup-esr115-fr-win32.exe`. |
| « Firefox installer … is not validly signed by Mozilla Corporation … » / « … has SHA-256 …, not the expected … » | Fichier altéré ou d'une autre origine : le supprimer et relancer ; ne jamais livrer le programme avec ce fichier. |
| « Authenticode cannot be checked here: pass -FirefoxSha256 … » | Construction hors Windows : comparer l'empreinte affichée au fichier `SHA256SUMS` publié par Mozilla pour cette version, puis la passer en `-FirefoxSha256`. |
| « dotnet publish … failed (exit code …) » | Vérifier le SDK .NET 10 (`global.json`) et relire la sortie de `dotnet publish`. |
| « Missing from the published files: … » | Un fichier indispensable manque après la publication (police, bibliothèque native, application du téléphone) : corriger la publication ; ne pas livrer le programme. |
| « No <Version> in Directory.Build.props. » | Ajouter l'élément `<Version>`. |

### 11.2 Pendant l'assistant

| Message | Que faire |
|---|---|
| « Le téléchargement de SQL Server Express a échoué : … » | Vérifier la connexion Internet, ou poser `SQLEXPR_x64_ENU.exe` à côté du programme et le relancer. |
| « Le support d'installation de SQL Server Express est introuvable après le téléchargement. » | Même remède : support hors ligne. |
| « L'installation de SQL Server Express a échoué (code …). » / « … aucune instance SQL Server n'est enregistrée … » | Lire `C:\Program Files\Microsoft SQL Server\<version>\Setup Bootstrap\Log\Summary.txt`. |
| « Indiquez le nom du serveur. » / « Le nom du serveur ne doit contenir ni espace, ni « \ », ni « ; ». … » | Poste Windows : nom de l'ordinateur seul ; l'instance va dans le champ suivant. |
| « … a besoin de Windows 10 ou 11 (64 bits) … » / « … a besoin d'un Windows 64 bits (x64) … » | Ce poste ne peut pas recevoir ce type : choisir « Poste Web » (5.5). Le serveur doit être un Windows 10, 11 ou Windows Server 2016 et ultérieur, 64 bits. |
| « Le certificat n'a pas pu être demandé à … » | Poste Web : vérifier le nom, que le serveur est allumé et que le service « MarocLIS - version Web » y fonctionne (11.10). Sous Windows 7 ou 8 sans ses mises à jour, utiliser le fichier `certificat-serveur.cer` copié du serveur (5.5). |
| « Ce fichier n'est pas un certificat lisible … » | Indiquer le fichier `certificat-serveur.cer` du dossier `C:\MarocLIS\Web` du serveur. |
| « Le certificat n'est pas accepté. … » | L'empreinte n'a pas été confirmée. Si elle diffère vraiment de celle du serveur, ne pas continuer : vérifier le nom saisi, ou utiliser le fichier copié du serveur. |
| « Connexion impossible à … » (bouton Tester) | Voir 11.5. On peut continuer : la chaîne est écrite telle quelle. |
| « Indiquez le dossier des sauvegardes (chemin complet …) » | Chemin `X:\…` ou `\\serveur\partage`, sans guillemet ni apostrophe. |
| « L'heure doit s'écrire HH:MM, de 00:00 à 23:59. » / « La conservation est un nombre de jours, de 1 à 365. » | Corriger la valeur. |
| « Ce fichier n'est pas une licence MarocLIS (un fichier .lic de moins de 64 Ko). » | Le fichier n'existe pas ou n'a pas l'extension `.lic` ; laisser vide et importer la licence plus tard. |

### 11.3 « À vérifier » en fin d'installation

Les étapes de configuration écrivent leur détail dans **`{app}\Installer\install.log`**. Le journal général de l'assistant est activé (`SetupLogging=yes`) ; Inno Setup l'écrit dans le dossier temporaire de Windows. Avertissements possibles :

| Dans le journal | Que faire |
|---|---|
| « TCP/IP is disabled on … » | Activer TCP/IP dans le Gestionnaire de configuration SQL Server, puis redémarrer le service SQL Server. |
| « SQL Server Browser service not found … » | Les postes devront indiquer le port de l'instance dans le nom du serveur (modifier leur `appsettings.json`). |
| « Firewall rule '…' could not be created … » / « sqlservr.exe of … not found … » | Créer à la main la règle entrante pour SQL Server (et UDP 1434 pour une instance nommée), limitée au sous-réseau local. |
| « Backup folder … is a network share … » | Donner au compte ordinateur du serveur (`DOMAINE\SERVEUR$`) le droit d'écrire sur le partage et dans le dossier. |
| « Backup folder … could not be created … » / « icacls could not give … » | Créer le dossier, donner le droit Modifier au compte du service SQL Server, ou choisir un autre dossier dans **Paramètres › Sauvegardes**. |
| « Service MarocLIS.Web is installed but https://localhost:5443/ does not answer yet … » | Le service peut mettre un moment à démarrer : réessayer `https://localhost:5443/` sur le serveur ; sinon voir 11.10. |
| « The certificate does not name … (a new address?) … » | Le serveur a une adresse IP absente de son certificat : utiliser son nom dans les Postes Web, ou renouveler le certificat (4.8). |
| « appsettings.json already names another certificate for Kestrel: kept as it is. » | Un certificat choisi à la main est conservé : c'est son empreinte que les Postes Web doivent confirmer. |
| « Firefox ESR n'a pas pu être installé (…) … » (Poste Web) | Installer Firefox ESR 115 à la main, ou mettre à jour Chrome ou Edge en 109, puis relancer l'installation « Poste Web ». |
| « Firefox (…) a déjà ses propres stratégies … conservées … » (Poste Web) | Dans Firefox, **Paramètres › Vie privée et sécurité › Certificats**, cocher « Autoriser Firefox à faire confiance aux certificats racines tiers ». |
| « Le certificat du serveur n'a pas pu être ajouté aux autorités de confiance de Windows … » (Poste Web) | En administrateur : `certutil -addstore Root C:\ProgramData\MarocLIS\certificat-serveur.cer`, ou relancer l'installation. |
| « FAILED: … » | L'étape a échoué : lire le message. Relancer le programme d'installation (comme une mise à jour) une fois la cause corrigée reprend toutes les étapes. |

### 11.4 MarocLIS ne démarre pas

| Message | Que faire |
|---|---|
| « L'application n'a pas pu démarrer. … Consultez logs/maroclis-*.log. » | Lire le journal du jour dans `{app}\Desktop\logs`. |
| « Échec de l'initialisation de la base de données : … » | Serveur injoignable, compte sans accès, ou mise à jour du schéma impossible : voir 11.5 et le journal. |
| « La base de données existe mais ne correspond pas au schéma attendu. » | La chaîne de connexion désigne une autre base que celle de MarocLIS : corriger `Database:ConnectionString`. |
| Écran « Aucune licence installée » | Normal sur une base neuve : section 8, partie 1. |

### 11.5 Un poste n'accède pas à la base

Vérifier, dans l'ordre :

1. le serveur est allumé et SQL Server démarré (Services Windows : `SQL Server (SQLEXPRESS)`, et `SQL Server Browser` pour une instance nommée) ;
2. le nom du serveur et l'instance dans `{app}\Desktop\appsettings.json` ;
3. le compte Windows de l'utilisateur est dans le groupe `MarocLIS-Utilisateurs` du serveur, et l'utilisateur a rouvert sa session depuis ; en groupe de travail, le compte jumeau du serveur a le même nom et le même mot de passe ;
4. les règles de pare-feu « MarocLIS - SQL Server (…) » et « MarocLIS - SQL Server Browser » existent sur le serveur, et le poste est dans le même sous-réseau (les règles n'admettent que le sous-réseau local) ;
5. TCP/IP est activé sur l'instance (11.3).

### 11.6 Le service de validation à distance ne répond pas

- Services Windows : « MarocLIS - validation a distance (API) » doit être démarré (démarrage automatique différé : il démarre quelques minutes après le système).
- Sur le serveur, `http://127.0.0.1:5080/` doit répondre.
- Journaux : `{app}\Api\logs\maroclis-api-*.log`, et le journal d'événements Windows en cas d'échec au démarrage.
- Après une mise à jour : démarrer MarocLIS une fois (mise à jour de la base), puis redémarrer le service.
- Côté téléphone : Tailscale connecté au tailnet du laboratoire, `tailscale serve status` sur le serveur, droit `results.validate_remote` du biologiste. Redémarrer le service déconnecte tous les téléphones.

### 11.7 Licence et lecture seule

Les messages et leur remède sont listés dans le manuel, Administration › « Messages d'erreur et d'avertissement » › « Licence ». Les plus fréquents à l'installation :

| Message | Que faire |
|---|---|
| « Lecture seule — … » | Lire la cause dans **Paramètres › Licence** ; importer une licence valide. |
| « Cette licence a été émise pour le serveur … ; ce serveur est … » | **Exporter la demande** et l'envoyer au fournisseur. |
| « Nombre de postes atteint (n / n) » | Faire déconnecter un poste, ou attendre 15 minutes qu'un poste inactif libère sa place. |
| « Cette licence perpétuelle couvre les mises à jour jusqu'au … ; cette version de MarocLIS date du … » | Réinstaller la version précédente ou renouveler la maintenance. |
| « Cette version vérifie les licences avec la clé de DÉVELOPPEMENT … » | Version de test installée : installer la version livrée par l'éditeur. |

En lecture seule, les sauvegardes et la restauration restent possibles.

### 11.8 Polices manquantes

L'avertissement de la section 6 signifie qu'une police configurée (`Reporting:LatinFontFamily`, `Reporting:ArabicFontFamily`) n'est ni dans `{app}\Desktop\Assets\Fonts` ni installée dans Windows. Vérifier la présence de `Carlito-Regular.ttf` et `Amiri-Regular.ttf` dans ce dossier (les réinstaller par le programme d'installation), ou installer la police dans Windows, ou remettre le nom de police par défaut dans `appsettings.json`. Sans la police arabe, l'édition d'un compte rendu en arabe est refusée sur ce poste.

### 11.9 Sauvegardes

| Message dans Paramètres › Sauvegardes | Que faire |
|---|---|
| « SQL Server ne peut pas écrire dans le dossier « … ». … » | Donner au compte de service SQL Server (par exemple `NT Service\MSSQL$SQLEXPRESS`) le droit Modifier sur le dossier ; pour un partage, au compte ordinateur du serveur. |
| « La tâche planifiée de … ne s'est plus manifestée depuis … » / « … n'a jamais tourné sur … » | Sur le serveur, Planificateur de tâches : vérifier « MarocLIS - Sauvegarde » et son journal `{app}\Sauvegarde\logs` ; si elle manque, relancer le programme d'installation serveur. |
| « Dernière sauvegarde réussie : … Elle date de plus de 48 heures … » / « Aucune sauvegarde réussie de la base n'est connue. » | Vérifier la tâche et le dossier ; **Sauvegarder maintenant**. |
| « La restauration se fait uniquement depuis le serveur, par un administrateur Windows de ce poste. » | Ouvrir MarocLIS sur le serveur. |
| « Le compte « … » n'a pas le droit de restaurer la base. … » | Accepter la relance « en tant qu'administrateur ». |

### 11.10 La version Web ne répond pas

- Sur le serveur, Services Windows : « MarocLIS - version Web » doit être démarré (démarrage automatique différé). `https://localhost:5443/connexion` doit afficher la page de connexion dans le navigateur du serveur.
- Journaux : `{app}\Web\logs\maroclis-web-*.log`, et le journal d'événements Windows en cas d'échec au démarrage. « The database could not be initialised » : voir 11.5 (le service se connecte avec le compte `NT AUTHORITY\SYSTEM`).
- Pare-feu : la règle « MarocLIS - Web (HTTPS 5443) » doit exister et le poste être dans le même sous-réseau que le serveur. Une réponse « 403 » vient du service lui-même : l'adresse du poste n'est pas dans `Web:AllowedNetworks`.
- Certificat : le service doit trouver son certificat (`Kestrel:Endpoints:Https:Certificate` de `{app}\Web\appsettings.json`, sujet `CN=MarocLIS Web <SERVEUR>`, magasin `LocalMachine\My`). En cas de doute, en administrateur dans `{app}\Web` : `.\install-web-service.ps1` (sans effet si tout est en place), ou `.\install-web-service.ps1 -RenewCertificate` puis réinstaller les Postes Web.

### 11.11 Un Poste Web n'ouvre pas MarocLIS

| Symptôme | Que faire |
|---|---|
| Le navigateur affiche une alerte de sécurité (certificat non fiable, `NET::ERR_CERT_AUTHORITY_INVALID`, `SEC_ERROR_UNKNOWN_ISSUER`) | Le certificat du serveur n'est pas dans les autorités de confiance du poste, ou le serveur en a changé : relancer l'installation « Poste Web ». Pour Firefox avec ses propres stratégies : 11.3. |
| Alerte « le nom ne correspond pas » (`ERR_CERT_COMMON_NAME_INVALID`, `SSL_ERROR_BAD_CERT_DOMAIN`) | L'adresse du raccourci n'est pas dans le certificat (serveur renommé, nouvelle adresse IP) : sur le serveur `install-web-service.ps1 -RenewCertificate` (4.8), puis relancer l'installation sur chaque Poste Web. |
| La page de connexion annonce un navigateur trop ancien | Mettre à jour le navigateur (Chrome ou Edge 109, Firefox 115 ou plus récent), ou relancer l'installation « Poste Web », qui installe Firefox ESR 115 quand aucun navigateur ne convient. |
| « Accès refusé » / « 403 » | L'adresse du poste n'est pas dans un réseau privé autorisé (`Web:AllowedNetworks` du serveur). |
| La page ne se charge pas du tout | Voir 11.10 ; vérifier depuis le poste que le serveur répond au nom saisi (`ping <nom>`). |

## 12. Annexe : fichiers et emplacements

### 12.1 Dossiers et fichiers

| Emplacement | Contenu |
|---|---|
| `{app}` = `C:\MarocLIS` par défaut | Dossier d'installation |
| `{app}\Desktop\MarocLIS.exe` | Application MarocLIS |
| `{app}\Desktop\appsettings.json` | Configuration de l'application (chaîne de connexion…), jamais remplacée par une mise à jour |
| `{app}\Desktop\licence.lic` | Licence fournie à l'assistant, importée au premier démarrage |
| `{app}\Desktop\Assets\Fonts` | Polices des comptes rendus (Carlito, Amiri) |
| `{app}\Desktop\Help` | Aide en ligne (touche F1) |
| `{app}\Desktop\logs\maroclis-AAAAMMJJ.log` | Journal de l'application, un fichier par jour, 31 conservés ; dossier modifiable par les utilisateurs |
| `{app}\Api\MarocLIS.Api.exe`, `{app}\Api\wwwroot` | Service de validation à distance et application du téléphone (serveur) |
| `{app}\Api\appsettings.json` | Configuration du service, jamais remplacée par une mise à jour |
| `{app}\Api\install-api-service.ps1` | Création et suppression du service |
| `{app}\Api\logs\maroclis-api-AAAAMMJJ.log` | Journal du service, un fichier par jour, 31 conservés |
| `{app}\Web\MarocLIS.Web.exe`, `{app}\Web\wwwroot` | Version Web (serveur) |
| `{app}\Web\appsettings.json` | Configuration de la version Web, jamais remplacée par une mise à jour (le certificat y est ajouté par `install-web-service.ps1`) |
| `{app}\Web\install-web-service.ps1` | Certificat, pare-feu, création et suppression du service `MarocLIS.Web` |
| `{app}\Web\certificat-serveur.cer`, `{app}\Web\certificat-serveur.txt` | Partie publique du certificat HTTPS du serveur, et son empreinte SHA-256 |
| `{app}\Web\logs\maroclis-web-AAAAMMJJ.log` | Journal de la version Web, un fichier par jour, 31 conservés |
| `{app}\maroclis-web.ico` | Icône du raccourci « MarocLIS » (Poste Web) |
| `C:\ProgramData\MarocLIS\certificat-serveur.cer` | Certificat du serveur reconnu par le Poste Web |
| `<dossier de Firefox>\distribution\policies.json` | Stratégies de Firefox écrites par l'installation d'un Poste Web |
| `{app}\Sauvegarde\maroclis-backup.ps1` | Script de la tâche de sauvegarde (serveur) |
| `{app}\Sauvegarde\logs\maroclis-backup-AAAAMM.log` | Journal mensuel de la tâche |
| `{app}\Installer\install.log` | Journal des étapes de configuration de l'installation |
| `C:\MarocLIS\Sauvegardes` | Dossier des sauvegardes par défaut |
| `C:\Program Files\Microsoft SQL Server\<version>\Setup Bootstrap\Log\Summary.txt` | Journal de l'installation de SQL Server |

### 12.2 Objets Windows et SQL Server créés (serveur)

| Objet | Nom |
|---|---|
| Instance SQL Server (si installée) | `SQLEXPRESS`, service `MSSQL$SQLEXPRESS`, compte `NT Service\MSSQL$SQLEXPRESS` |
| Base de données | `MarocLIS` |
| Groupe local Windows | `MarocLIS-Utilisateurs` |
| Connexions SQL Server (`db_owner` de `MarocLIS`) | `<SERVEUR>\MarocLIS-Utilisateurs`, le compte qui a installé, `NT AUTHORITY\SYSTEM` (avec `CREATE ANY DATABASE`) |
| Service Windows | `MarocLIS.Api` (« MarocLIS - validation a distance (API) »), LocalSystem, automatique différé, `http://127.0.0.1:5080` |
| Service Windows | `MarocLIS.Web` (« MarocLIS - version Web »), LocalSystem, automatique différé, `https://*:5443` |
| Certificat | « MarocLIS Web », sujet `CN=MarocLIS Web <SERVEUR>`, dans `LocalMachine\My` et `LocalMachine\Root`, 10 ans |
| Tâche planifiée | `MarocLIS - Sauvegarde`, SYSTEM, toutes les 15 minutes et au démarrage |
| Règles de pare-feu (groupe « MarocLIS ») | `MarocLIS - SQL Server (<instance>)`, `MarocLIS - SQL Server Browser` (UDP 1434), `MarocLIS - Web (HTTPS 5443)` (TCP 5443) |

Sur un Poste Web : le certificat du serveur dans les autorités de certification racines de confiance de l'ordinateur, et, s'il a fallu l'installer, Firefox ESR 115.

### 12.3 Clés de `appsettings.json` utiles à l'installation

Application (`{app}\Desktop\appsettings.json`) :

| Clé | Valeur livrée | Rôle |
|---|---|---|
| `Database:ConnectionString` | `Server=localhost\SQLEXPRESS;Database=MarocLIS;Integrated Security=true` (réécrite par l'installation) | Base du serveur |
| `Application:DefaultLanguage` | `French` | Langue de l'écran de connexion |
| `Application:SeedDemoDataOnFirstRun` | `false` | Laisser `false` en production : `true` crée des données et des comptes de démonstration aux mots de passe publiés |
| `Instruments:EnableInstrumentHost` | `false` | Mettre `true` sur le seul poste qui pilote les automates |
| `Labels:LabelPrinterName` | vide | Nom Windows de l'imprimante d'étiquettes ; vide = étiquettes en PDF |
| `Reporting:LatinFontFamily` / `Reporting:ArabicFontFamily` | `Carlito` / `Amiri` | Polices des comptes rendus |
| `Messaging:Smtp:*`, `Messaging:Sms:*` | vides | Envoi des comptes rendus par courriel et SMS |
| `PasswordPolicy:*` | voir section 6 | Règles des mots de passe |
| `Serilog` (chemin `logs/maroclis-.log`) | — | Journal |

Toute clé peut être remplacée sur une machine par une variable d'environnement préfixée `MAROCLIS_` (les `:` deviennent `__`), par exemple `MAROCLIS_Instruments__EnableInstrumentHost=true`.

Service (`{app}\Api\appsettings.json`) :

| Clé | Valeur livrée | Rôle |
|---|---|---|
| `Urls` | `http://127.0.0.1:5080` | Ne pas changer : le service ne doit écouter que la machine elle-même |
| `Database:ConnectionString` | réécrite par l'installation | Même base que les postes |
| `MobileApi:MaxSessionLifetime` | `12:00:00` | Durée maximale d'une session de téléphone |
| `PasswordPolicy`, `Reporting` | — | À garder identiques à ceux des postes |

Version Web (`{app}\Web\appsettings.json`) :

| Clé | Valeur livrée | Rôle |
|---|---|---|
| `Kestrel:Endpoints:Https:Url` | `https://*:5443` | Adresse et port de la version Web ; un autre port demande aussi de changer la règle de pare-feu et les raccourcis des Postes Web |
| `Kestrel:Endpoints:Https:Certificate` | ajoutée par l'installation (`Subject`, `Store` `My`, `Location` `LocalMachine`, `AllowInvalid` `true`) | Certificat HTTPS du serveur |
| `Database:ConnectionString` | réécrite par l'installation | Même base que les postes |
| `Web:AllowedNetworks` | plages privées IPv4 et IPv6, boucle locale, `100.64.0.0/10` | Seules adresses servies ; les autres reçoivent « 403 » |
| `Web:InitializeDatabase` | `true` | Crée ou met à jour le schéma au démarrage du service |
| `Web:ServerCertificateFile` | `certificat-serveur.cer` | Certificat servi aux Postes Web à `/certificat-serveur.cer` et `/certificat-serveur.pem` |
| `Application`, `PasswordPolicy`, `Reporting`, `Instruments` | — | À garder identiques à ceux des postes |

### 12.4 Réglages de sauvegarde dans la base

Table `cfg.Settings`, réglages communs à l'installation :

| Clé | Contenu |
|---|---|
| `Backup.Enabled` | `true` ou `false` |
| `Backup.Folder` | Dossier des sauvegardes, vu du serveur |
| `Backup.Times` | Heures, par exemple `22:00` ou `08:00,13:00` |
| `Backup.Days` | Jours en anglais (`Monday,Friday`) ; vide = tous les jours |
| `Backup.RetentionDays` | Conservation en jours |
| `Backup.State.*` | État écrit par la tâche (dernier passage, serveur, dernier horaire traité, dernier résultat, dernière réussite, dossier ouvert au compte SQL Server) |
| `Backup.Check.<fichier>` | Résultat de la vérification de chaque fichier |

Ces valeurs se règlent dans **Paramètres › Sauvegardes** ; ne les modifiez pas directement dans la base.
