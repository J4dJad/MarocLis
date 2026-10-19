# Guide d'installation : licence MarocLIS

Ce guide complète le programme d'installation (`MarocLIS-Setup-<version>.exe`) pour ce qui touche à la licence. La première partie s'adresse à celui qui installe MarocLIS dans un laboratoire ; la seconde à l'éditeur, qui détient la clé de signature des licences.

## Partie 1 — Installer la licence d'un laboratoire

### Ce qu'il faut savoir avant

- La licence est un fichier `.lic` signé par l'éditeur. Elle est enregistrée dans la base de données : une seule licence pour le serveur et tous ses postes.
- Une licence d'exploitation est **liée au serveur** : elle porte l'identifiant du serveur pour lequel elle a été émise. Une licence d'essai ou de formation ne l'est généralement pas, elle peut être installée avant le serveur définitif.
- La licence fixe le nombre de **postes** (ordinateurs connectés en même temps ; les téléphones ne comptent pas) et les **modules** ouverts en plus du socle.

### L'identifiant serveur

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

### Pendant l'installation

Le programme d'installation propose une page **Licence MarocLIS** (facultative). Si vous avez déjà le fichier `.lic` (licence d'essai, de formation, ou réinstallation sur le même serveur), indiquez-le : il est copié à côté de MarocLIS et importé automatiquement au premier démarrage. Sinon laissez vide.

### Au premier démarrage

1. Sur une base vide et sans licence, MarocLIS s'ouvre sur l'écran **Aucune licence installée** au lieu de la connexion.
2. **Exporter la demande** : enregistre `demande-licence-<site>.demande` (identifiant serveur, nom du site, version de MarocLIS). Envoyez ce fichier au fournisseur. À défaut, **Copier** l'identifiant et le lui transmettre.
3. Le fournisseur renvoie un fichier `licence.lic`. **Importer une licence…** : MarocLIS s'ouvre aussitôt sur la connexion, sans redémarrer.
4. Plus tard, une nouvelle licence (renouvellement, postes ou modules ajoutés) s'importe dans **Paramètres › Licence** avec le droit `admin.license` (administrateur). Elle est en vigueur immédiatement sur le poste qui l'importe, et sur les autres au plus tard cinq minutes après.

Une base qui contient déjà des patients n'affiche jamais cet écran bloquant : sans licence valide, elle s'ouvre en **lecture seule** (consultation, réimpression des comptes rendus, encaissements), et l'administrateur importe la licence depuis **Paramètres › Licence**. Le détail des états (préavis, grâce, lecture seule) est au chapitre « Administration » du manuel, section « Licence ».

### Changer de serveur

1. Installer MarocLIS sur le nouveau serveur et y restaurer la base.
2. Au démarrage, MarocLIS signale que la licence a été émise pour un autre serveur et passe en lecture seule.
3. **Paramètres › Licence › Exporter la demande**, envoyer la demande au fournisseur, importer la licence reçue (même numéro, version suivante).

## Partie 2 — Pour l'éditeur : la clé de licence et le Générateur

Cette partie ne concerne pas les laboratoires. Le **Générateur de licences** (`tools/MarocLIS.LicenseGenerator`) est un outil de l'éditeur : il n'est ni dans le programme d'installation, ni livré aux laboratoires.

### Principe

- Chaque licence est signée en ECDSA (courbe P-256, SHA-256) avec la **clé privée** de l'éditeur. Le fichier contient les termes de la licence en JSON lisible et la signature à part ; la signature porte sur une forme canonique des termes, si bien qu'un seul caractère modifié la rend invalide.
- MarocLIS vérifie la signature avec la **clé publique** compilée dans l'application (`src/MarocLIS.Application/Licensing/licence-public-key.pem`). La clé privée n'est jamais dans le dépôt de code.
- Le dépôt contient une **clé de DÉVELOPPEMENT** (empreinte `CDC1-D2A2-222A-4A92-B835-95FB-909A-18BB`), dont la clé privée est dans `tests/MarocLIS.Tests/Licensing/` pour les tests. N'importe qui ayant le code peut signer des licences qu'elle accepte : une version compilée avec elle ne doit jamais être installée chez un laboratoire. MarocLIS l'affiche en clair dans **Paramètres › Licence**, et `installer/build-installer.ps1` refuse de construire le programme d'installation avec elle.

### Cérémonie de la clé (une seule fois)

1. **Choisir l'ordinateur** qui gardera la clé : un poste de l'éditeur, à jour, avec un compte Windows personnel, chiffrement du disque activé (BitLocker), sans partage réseau du profil.
2. **Choisir la phrase de passe** : 12 caractères au moins, une phrase plutôt qu'un mot. Elle n'est enregistrée nulle part ; l'écrire sur papier et la ranger au coffre, à part de toute sauvegarde.
3. Lancer le Générateur (`MarocLIS.GenerateurLicences.exe`) : il s'ouvre sur l'onglet **Clé de signature**. **Créer la clé…**, saisir deux fois la phrase de passe. La clé privée est écrite chiffrée (PKCS#8, AES-256, phrase de passe étirée par PBKDF2-SHA-256 sur 600 000 itérations) dans `%APPDATA%\MarocLIS\GenerateurLicences\cle-privee.pem`, la clé publique à côté. Noter l'**empreinte** affichée.
4. **Exporter une sauvegarde chiffrée…** : choisir la phrase de passe de la sauvegarde (la même ou une autre, elle aussi notée au coffre), enregistrer le fichier `.cle-sauvegarde` sur **deux supports** gardés en deux lieux distincts (clé USB au coffre, second exemplaire hors des locaux). Sans cette sauvegarde, une panne de l'ordinateur rend impossible toute nouvelle licence pour les installations existantes : il faudrait livrer à chacune une version de MarocLIS avec une nouvelle clé.
5. **Exporter la clé publique…** : enregistrer `licence-public-key.pem`, le copier sur `src/MarocLIS.Application/Licensing/licence-public-key.pem` dans le code source, et valider ce changement dans le dépôt (la clé publique n'est pas secrète).
6. Construire le programme d'installation : `installer\build-installer.ps1` affiche l'empreinte de la clé compilée ; elle doit être celle notée à l'étape 3. Avec la clé de développement, le script s'arrête et explique la marche à suivre (`-AllowDevelopmentKey` ne sert qu'aux essais internes et produit `MarocLIS-Setup-<version>-CLE-DEV.exe`).
7. Après installation chez un premier laboratoire, vérifier dans **Paramètres › Licence › Clé de vérification** que l'empreinte est la bonne.

Restaurer la clé sur un autre ordinateur : **Restaurer une sauvegarde…** (phrase de passe de la sauvegarde, puis nouvelle phrase de passe locale). Le Générateur refuse d'écraser une autre clé déjà présente.

### Émettre une licence

- **Nouvelle licence…** ou **Depuis une demande…** (le fichier `.demande` du laboratoire remplit l'identifiant serveur, le site et le laboratoire). Le numéro suit la série de l'année (`LIC-2026-0001`, …).
- Le **pack** (Essentiel 2 postes, Professionnel 5 postes, Complet 10 postes) ne fait que préremplir les postes et les modules ; les modules restent cochables un à un, et le fichier les liste toujours un par un. Les packs se modifient dans l'onglet **Packs**.
- Valeurs proposées, modifiables pour chaque licence : essai de 30 jours ; préavis 30 jours avant la fin ; grâce 15 jours après ; 7 jours de paillasse en lecture seule ; liaison au serveur cochée pour une licence annuelle ou perpétuelle, décochée pour l'essai et la formation ; licence perpétuelle avec une date « mises à jour jusqu'au » (un an par défaut) ; licence de formation à un poste, sans fin.
- **Signer et enregistrer…** : la phrase de passe est demandée une fois par session ; la licence est relue et vérifiée avec la clé publique avant d'être inscrite au registre, puis enregistrée sous `LIC-…-vN.lic`.

### Remplacer, renouveler, révoquer

- **Remplacer…** : même numéro, version suivante (postes ou modules ajoutés, nouveau serveur). MarocLIS remplace une licence par une version supérieure du même numéro et refuse ensuite les versions antérieures.
- Onglet **Renouvellements** : les licences en vigueur dont la fin (ou la fin des mises à jour d'une perpétuelle) tombe dans les 45 jours, et celles déjà échues non renouvelées, avec le contact noté sur la licence. **Renouveler…** prépare la version suivante sur un an de plus ; **Exporter en CSV…** donne la liste de relance.
- **Révoquer…** (impayé, fichier perdu ou volé) : la ligne de révocation est copiée ; l'ajouter à `src/MarocLIS.Application/Licensing/licence-revoked.txt` et publier une nouvelle version de MarocLIS. Les versions publiées ensuite refusent cette licence (une version précise `LIC-2026-0042/1`, ou toutes `LIC-2026-0042`).

### Fichiers du Générateur

Dans `%APPDATA%\MarocLIS\GenerateurLicences\` (ou le dossier donné par `--dossier` ou la variable `MAROCLIS_GENERATEUR_DOSSIER`) : `cle-privee.pem` (chiffrée), `cle-publique.pem`, `registre-licences.json` (licences émises, avec leur fichier signé) et `packs.json`. Sauvegarder le registre avec la sauvegarde habituelle de l'éditeur ; il ne contient aucun secret.
