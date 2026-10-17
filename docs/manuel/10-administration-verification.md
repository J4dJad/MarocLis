## Vérification de l'interface — Administration

Chemins relatifs à `src/`. « V/ » = `MarocLIS.Desktop/Views/`. « Présent ? » : **Oui**, **PARTIEL**, **MANQUANT**. Revérifié le 2026-10-17 sur le commit 4e20d0d (plus 9f25a6a et 5ad4d11 fusionnés : bouton « … » du code d'acte, compteurs et sélection de la nomenclature). Les libellés sont ceux de `MarocLIS.Localization/Resources/fr.json`.

### Rôles livrés

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Droits livrés par rôle | Utilisateurs › Rôles et droits | arbre des droits | Oui | MarocLIS.Infrastructure/Persistence/ReferenceDataSeeder.cs:493-760 (`RolePermissionSets`) |
| Réimporter par le Biologiste et le Technicien | Automates | **Réimporter** | Oui | **Corrigé.** `instruments.replay` donné au Biologiste (ReferenceDataSeeder.cs:580) et au Technicien (l.611), en plus de l'Administrateur et du Biologiste responsable (l.529) |
| Retrait de validation et réédition par le Biologiste ; réédition par le Secrétariat | — | — | Oui | **Nouveau.** ReferenceDataSeeder.cs:570, 574 (`Results.Unvalidate`, `Reports.ReprintValidated`) ; Secrétariat l.642 |
| Libellés des modules dans l'arbre | Rôles et droits | en-têtes de l'arbre | PARTIEL | `PermissionModule.accounting` et `PermissionModule.assets` toujours absents de fr.json (seuls steering, messaging, relations, treasury, hr… existent) : en-têtes bruts pour l'export comptable et les immobilisations |

### Utilisateurs (service de gestion des utilisateurs)

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister / chercher les comptes | Utilisateurs › Utilisateurs | **Rechercher…** (Entrée), **Inclure les comptes désactivés** | Oui | V/UserAdministrationView.cs:135-137, 423-428 ; compteur `Admin.UserCount` l.686 |
| Panneau du compte sélectionné | Utilisateurs | panneau de droite | Oui | **Nouveau.** V/UserAdministrationView.cs:154-290 : nom, identifiant, pastille, Qualification, N° Ordre, Rôles et droits, Secteurs autorisés, Dernière connexion (l.534-536, 922-941), message de défaut (l.895-908) |
| Créer un compte | Utilisateurs | **Nouvel utilisateur** | Oui | :434 → CreateUserAsync :972. Le compte est créé sans rôle ni choix de site (site de l'administrateur) : 2 étapes de plus |
| Modifier une fiche | Utilisateurs | **Modifier** | Oui | :435 → :994. Secteurs saisis en texte libre (codes) : pas de liste de choix |
| Attribuer des rôles | Utilisateurs | **Rôles et droits** (panneau) | Oui | :436 → :1020. Même libellé que l'onglet « Rôles et droits » : prête à confusion |
| Sites accessibles + site par défaut | Utilisateurs | **Sites accessibles**, **Site par défaut** | Oui | :437 → :1040. **Corrigé** : un laboratoire partenaire est refusé comme site de travail (Application/Administration/UserAdministrationService.cs:292, 481, `User_PartnerSiteNotAccessible`) |
| Réinitialiser le mot de passe | Utilisateurs | **Réinitialiser le mot de passe** | Oui | :438 → :1060 |
| Déverrouiller | Utilisateurs | **Déverrouiller** | Oui | :439 → :1085 ; actif seulement si verrouillé ou avec échecs (l.871) |
| Désactiver / réactiver avec motif | Utilisateurs | **Désactiver** / **Réactiver** | PARTIEL | :440 → :1100 ; grisé sur son propre compte (l.876). La réactivation n'est toujours pas inscrite au journal de sécurité : le service n'écrit que UserCreated, RoleAssigned, RoleRemoved, UserDeactivated, AccountUnlocked, PermissionsChanged (UserAdministrationService.cs:330-697) |
| Double-clic sur un compte | Utilisateurs | — | PARTIEL | Aucun gestionnaire de double-clic sur la liste des comptes ; les autres écrans du chapitre ouvrent la fiche au double-clic |
| Créer un rôle | Rôles et droits | **Nouveau rôle** | PARTIEL | :449 → :1125. Code puis désignation française seulement (l.1129-1132) ; ni description ni désignation arabe/anglaise ; aucun moyen de renommer un rôle |
| Modifier les droits d'un rôle | Rôles et droits | arbre + **Enregistrer les droits** | Oui | :451 → :1189 ; confirmation `Admin.ConfirmPermissionChange` l.1209 |
| Activer / désactiver un rôle créé | Rôles et droits | **Désactiver le rôle** / **Activer le rôle** | Oui | :450 → :1164 ; grisé pour un rôle livré (l.799) |
| Droits des boutons | Utilisateurs | — | Oui | :454-455 (`admin.users.manage`, `admin.roles.manage`) ; arbre en lecture seule sans le droit (l.460) |
| Journal de sécurité | Journal de sécurité | **N'afficher que l'utilisateur sélectionné**, **Actualiser** | Oui | :463-464 → :806 ; onglet retiré sans `admin.audit.view` (l.117) ; 300 derniers événements |
| Compte de service `interface` des automates | Utilisateurs | **Nouvel utilisateur** | PARTIEL | Demandé par l'écran Automates (V/InstrumentInterfaceView.cs:337) ; aucune aide sur les droits à lui donner |

### Journal d'audit

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Filtres et relecture | Journal d'audit | **Du**, **Au**, **Identifiant**, **Rechercher…**, **N° de dossier**, **Appliquer** (ou Entrée) | Oui | V/AuditTrailView.cs:111-113, 223-227 ; libellés l.321-326 |
| Journal des modifications / consultations / événements de sécurité | onglets | **Modifications**, **Consultations**, **Événements de sécurité** | Oui | :133, 317-319 |
| Détail de la ligne | cadre du bas | **Détail de la ligne sélectionnée** | Oui | :125-127, 315 |
| Liste tronquée signalée | ligne d'état | — | Oui | :437 (`Audit.Truncated`) ; plafond 500 (Application/Administration/AuditTrailService.cs:43) |
| Demande d'accès d'un patient (CNDP) | › Consultations | **N° de dossier** | PARTIEL | Le filtre courant (l.409-415) ne porte pas le n° de dossier aux modifications : seules les consultations sont filtrées. Aucun export dédié : impression générique de la grille (Ctrl+P) |
| Historique d'un enregistrement précis / d'un patient par identifiant interne | — | — | MANQUANT | Filtres `PatientId` et enregistrement prévus par le service (AuditTrailService.cs:30-37), non proposés à l'écran |

### Catalogue des analyses

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Sept onglets | Catalogue | Analyses, Tarifs, Profils, Analyses en cascade, Listes de référence, Nomenclature des actes, Écarts avec la nomenclature | Oui | V/CatalogView.cs:138-144 ; libellés l.461-465 et V/CatalogViewNationalActs.cs:143-144 |
| Filtrer les analyses à compléter | Catalogue › Analyses | pastilles **Toutes · N**, **À compléter · N** | Oui | **Nouveau.** V/CatalogView.cs:160-168, 663-681 : non prescriptible ou unité d'âge à confirmer |
| Créer une analyse (code interne) | Catalogue › Analyses | **Nouvelle analyse** | Oui | V/CatalogView.cs:431 → :1254 ; code saisi une fois et mis en majuscules (V/CatalogEditors.cs:86, l.454) |
| Modifier une analyse | Catalogue › Analyses | **Modifier l'analyse** / double-clic | Oui | **Corrigé** (commit 89ddaa8). :432, 424. La fenêtre s'ouvre sur toute la fiche enregistrée (V/CatalogEditors.cs:93, 243-246) et la requête part de la fiche stockée, jours d'exécution compris (l.442-452) |
| Code de l'acte : saisie, choix dans la nomenclature | fiche analyse | **Code de l'acte**, bouton **…**, F4 | Oui | **Nouveau.** V/CatalogEditors.cs:120-135 (case mise en majuscules, bouton de 36 px depuis 9f25a6a) ; info en direct l.262-283 (`Nabm.ActInfo`, `Nabm.NoActHint`, `Nabm.CodeNotInList`) ; choix l.291-300 |
| Fenêtre de choix de l'acte | Nomenclature des actes de biologie médicale | **Rechercher**, flèches, **Sélectionner** / double-clic | Oui | **Nouveau.** V/CatalogViewNationalActs.cs:395-480 ; ouverte sur l'acte déjà saisi (l.461-466) ; `Nabm_PickAnAct` l.495 |
| Coefficient officiel proposé au choix de l'acte | fiche analyse | bloc **Prix du laboratoire** | Oui | V/CatalogEditors.cs:300 et suivantes, seulement avec le droit des tarifs |
| Code tapé hors nomenclature | fiche analyse | **Enregistrer** | Oui | V/CatalogEditors.cs:428-436 (`Nabm.CodeNotInListConfirm`) |
| Liste de la nomenclature | Catalogue › Nomenclature des actes | recherche, pastilles **Tous**, **2005**, **2012**, **2016** | Oui | **Nouveau.** V/CatalogViewNationalActs.cs:57-87, 196-230 ; 492 actes B100-B591 (MarocLIS.Infrastructure/Persistence/Seed/nomenclature-biologie.csv) ; lecture seule. **Utilisé par** relu après l'enregistrement d'une analyse (V/CatalogView.cs:1331, depuis 9f25a6a) |
| Contrôle des écarts | Catalogue › Écarts avec la nomenclature | pastilles **Tous**, **Code manquant**, **Code inconnu**, **Coefficient différent** | Oui | **Nouveau.** V/CatalogViewNationalActs.cs:89-136, 237-300 |
| Corriger le code d'une analyse en écart | Écarts | **Modifier l'analyse** / double-clic | Oui | V/CatalogViewNationalActs.cs:129-134, 360-387 ; `catalog.manage` (l.303) ; la ligne voisine devient courante (l.338-357, depuis 9f25a6a) |
| Appliquer le coefficient officiel | Écarts | **Appliquer le coefficient officiel** | Oui | **Nouveau.** V/CatalogViewNationalActs.cs:311-336 ; confirmation `Nabm.ConfirmApply` ; `billing.tariffs.manage` (l.301). Service Application/Catalog/NationalActService.cs:212-279 : refus sans code, code inconnu, déjà officiel, révision programmée ; tracé au journal d'audit (l.279) ; révisions programmées laissées (`Nabm.SkippedScheduled`) |
| Analyse sous-traitée | fiche analyse | **Sous-traitée** + **Laboratoire partenaire** | Oui | **Corrigé** : un partenaire se crée maintenant par **Paramètres** › **Nouveau site** (type « Laboratoire correspondant »). Refus sans partenaire V/CatalogEditors.cs:421-425 |
| Créer / modifier un paramètre | Catalogue › Analyses | **Nouveau paramètre**, **Modifier le paramètre** / double-clic | Oui | **Corrigé.** V/CatalogView.cs:433-434, 425 ; la fenêtre s'ouvre sur le paramètre enregistré : libellé court, LOINC (V/CatalogEditors.cs:575-576), réponses autorisées (l.646), requête bâtie sur la fiche stockée (l.831-845) |
| Paramètre calculé | fiche paramètre | **Formule de calcul** | Oui | contrôle des codes avant enregistrement (V/CatalogEditors.cs:822) |
| Valeurs usuelles | Catalogue › Analyses | **Nouvel intervalle**, **Modifier l'intervalle** / double-clic | Oui | V/CatalogView.cs:435-436, 426 |
| Revue périodique | Catalogue › Analyses | **Enregistrer la revue** | Oui | :437 → :1399 |
| Nouveau tarif / révision du B | Catalogue › Tarifs | **Nouveau tarif** | PARTIEL | :438 → :1418. Une seule analyse à la fois : pas de révision globale de la valeur de la lettre B. Le coefficient, lui, s'aligne en série depuis l'onglet Écarts |
| Corriger un tarif | Catalogue › Tarifs | **Modifier le tarif** / double-clic | Oui | :439, 441 |
| Tarifs par le service comptable | — | — | PARTIEL | Le rôle Comptabilité a le droit des tarifs mais pas `catalog.view` (ReferenceDataSeeder.cs, rôle Accountant) : pas de Catalogue, donc ni tarifs ni **Appliquer le coefficient officiel**. Seul l'administrateur le peut avec les rôles livrés |
| Profils | Catalogue › Profils | **Nouveau profil**, **Modifier le profil** / double-clic | PARTIEL | :445-446, 427 ; en modification les **Synonymes de recherche** s'ouvrent toujours vides (V/CatalogEditors.cs:1572) et sont effacés à l'enregistrement (Application/Catalog/CatalogMaintenanceService.cs:1359) |
| Analyses en cascade | Catalogue › Analyses en cascade | **Nouvelle règle en cascade**, **Modifier la règle** / double-clic | Oui | :447-448, 428 |
| Listes de référence | Catalogue › Listes de référence | **Ajouter**, **Modifier** / double-clic | Oui | :449-450, 429 |
| Supprimer une entrée | — | — | (voulu) | Pas de suppression : désactivation par **Actif** |

### Automates

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Créer une liaison | Automates | **Nouvelle interface** | Oui | V/InstrumentInterfaceView.cs:231 → :675 ; `instruments.configure` (l.649-651) |
| Modifier une liaison | Automates | **Modifier l'interface** / double-clic | Oui | :232, 200 |
| Activer / désactiver | Automates | **Activer la liaison** / **Désactiver la liaison** | Oui | :233 → :829 ; `instruments.control` (l.657-658) |
| Table de correspondance | Automates | **Table de correspondance**, **Ajouter des paramètres…**, **Retirer la ligne** | Oui | :234 → :727 |
| Tester une trame | Automates | **Tester une trame…**, **Analyser la trame**, **Charger la dernière trame reçue** | Oui | :235-239 → :756 |
| Exemple de trame COBAS INTEGRA | Tester une trame | **Exemple de trame** | Oui | **Nouveau.** V/InstrumentFrameTestDialog.cs:100-109 ; proposé seulement pour une liaison Roche COBAS INTEGRA (V/InstrumentInterfaceView.cs:791-816) |
| Protocole et profil COBAS INTEGRA | fiche interface | **Protocole** « Roche COBAS INTEGRA », **Profil fabricant** « Roche cobas INTEGRA 400 plus » | Oui | **Nouveau.** V/InstrumentInterfaceDialogs.cs:73, 278-289 ; préréglage l.393-411 (Série, Envoi de la liste de travail, 9600 8N1, XOnXOff, ASCII, 180 s, 3 tentatives) ; note `Interface.IntegraNotice` l.425-426 |
| Voir la trame brute | Automates | **Voir la trame brute** / double-clic | Oui | :240, 229 → :859 ; droit de voir les résultats (l.243) |
| Réimporter / accepter une transmission retenue | Automates | **Réimporter** | Oui | **Corrigé.** :244 → :881 ; `instruments.replay` (l.667-668) maintenant livré au Technicien et au Biologiste : les transmissions retenues sans acceptation automatique peuvent être libérées à la paillasse |
| Purger le journal | Automates | **Purger le journal** | Oui | :245 → :920 ; `admin.maintenance.run` (l.672) |

### Données de référence

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Équipement : créer | Données de référence › Automates | **Nouvel équipement** | Oui | V/MasterDataView.cs:323 → :943 |
| Équipement : modifier | idem | **Modifier** | Oui | **Corrigé.** :324 ; la fenêtre s'ouvre sur la fiche enregistrée (V/MasterDataEditors.cs:435-522) et la requête part de la fiche stockée (l.564-573) |
| Matériau / cible de contrôle | › Contrôle interne | **Nouveau matériau de contrôle**, **Nouvelle cible de contrôle**, **Modifier** | Oui | :327-333 → :968, :981 ; prérequis V/MasterDataView.cs:985-997 |
| Articles | › Articles | **Nouvel article**, **Modifier** | Oui | :335-337 → :1013 |
| Fournisseurs | › Fournisseurs | **Nouveau fournisseur**, **Modifier** | Oui | :339-341 → :1032 |
| Prescripteur : créer | › Prescripteurs | **Nouveau prescripteur** | Oui | :343 → :1045 |
| Prescripteur : modifier | › Prescripteurs | **Modifier** | Oui | **Corrigé.** :344 ; ICE, Téléphone, Fax, Adresse, Téléphone résultats critiques, Observations repris de la fiche enregistrée (V/MasterDataEditors.cs:1261-1335, requête l.1385-1394) |
| Libellé du type de prescripteur / d'article | fiches | liste du type | PARTIEL | Toujours intitulée « Désignation » (`Field.Name`, V/MasterDataEditors.cs:895, 1291) |
| Organismes payeurs | › Organismes payeurs | **Nouvel organisme payeur**, **Modifier** / double-clic | Oui | :347-349, 368-371 → :1070 |
| Droits des onglets | Données de référence | — | Oui | V/MasterDataView.cs:375-379 |
| Ouverture par la Comptabilité | Données de référence | — | PARTIEL | Le rôle Comptabilité n'a pas `equipment.view` : la lecture des équipements est refusée et affichée en message à chaque ouverture (V/MasterDataView.cs:617-619 ; Application/Administration/MasterDataService.cs:383) |
| Libellé de la case | Données de référence | **Inclure les comptes désactivés** | PARTIEL | V/MasterDataView.cs:528 (`Admin.IncludeInactive`) — libellé repris des comptes utilisateurs ; il s'agit des entrées désactivées |

### Paramètres

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Six onglets | Paramètres | Site, Numérotations, Paramètres, Factures, Lieux de stockage, Imprimantes | Oui | V/SettingsView.cs:172 ; **Lieux de stockage** retiré sans `admin.settings.manage` (l.175-176) |
| Compléter l'identité du site | Paramètres › Site | **Enregistrer** | Oui | :719 → SaveSiteAsync :1124 ; `Site.ConfigurationComplete` l.1182 |
| Logo | Paramètres › Site | **Choisir le logo…**, **Retirer le logo** | Oui | :721-726 ; `Site.LogoInvalid` l.1056 |
| **Créer un site** (centre de prélèvement, laboratoire partenaire) | Paramètres › Site | **Nouveau site** | Oui | **Corrigé (était MANQUANT).** :720 → CreateSiteAsync :1195-1241 : fenêtre Code / Désignation (français) / Désignation (arabe) / **Type de site** (« Laboratoire correspondant » par défaut), rouverte avec les saisies après un refus, `Site.Created` ; `admin.sites.manage` (l.747) |
| Passer un site utilisé en partenaire | Paramètres › Site | **Type de site** | Oui | Refusé (`Site_CannotBecomePartner`) : le partenaire se crée par **Nouveau site** |
| Numéro de dossier et bordereau de sous-traitance | Numérotations | — | Oui (info) | Compteur unique partagé (MarocLIS.Infrastructure/Sequencing/SequenceAllocator.cs) ; colonne **Site** = « — » |
| Modifier un format | Numérotations | **Modifier le format** | Oui | :731 → :1297 |
| Clôturer / réouvrir une série | Numérotations | **Clôturer la série** / **Réouvrir la série** | Oui | :732 → :1318 |
| Préparer la série de l'année suivante | Numérotations | — | MANQUANT | La série d'une nouvelle année est créée au premier document avec le format par défaut (SequenceAllocator.cs:43) ; colonne année intitulée « Date » |
| Modifier / créer un paramètre | Paramètres › Paramètres | **Modifier**, **Nouveau** | PARTIEL | :744-745 → :1388, :1409. **Nouveau** demande seulement la clé et la valeur (l.1411-1416) : pas de choix du site, de la description ni du caractère sensible |
| Choisir le format des factures patients | Paramètres › Factures | **Format par défaut des factures patients** : « Format existant (montant, part organisme, part patient) » / « Format cotation B (code de l'acte, cotation en B, montant en DH) » | Oui | **Nouveau.** V/SettingsView.InvoicePrint.cs:33-95, 116-118 ; `admin.settings.manage` (l.93) |
| Options des deux formats | Paramètres › Factures | **Afficher la colonne code national de l'acte**, **Afficher la colonne cotation (coefficient)**, **Afficher le montant en lettres** ; **Afficher le prix en DH de chaque acte**, **Afficher la ligne « Prise de sang (PC) » même à 0,00 DH**, **Afficher le montant en lettres**, **Afficher la valeur de la lettre B utilisée** | Oui | **Nouveau.** l.104-111 ; valeurs par défaut Application/Billing/InvoicePrintFormat.cs:46-57 ; avoirs au format existant (l.60) |
| Enregistrer le format | Paramètres › Factures | **Enregistrer**, **Actualiser** | Oui | **Nouveau.** l.88-89 → :148-178 ; `InvoicePrint.Saved` ; service InvoicePrintFormat.cs:126-159 (`InvoicePrint_UnknownFormat`, tracé au journal d'audit l.157) |
| Ajouter un lieu de stockage | Paramètres › Lieux de stockage | **Ajouter** | Oui | **Nouveau.** V/SettingsView.cs:739 → :1475 (`StorageLocations.NewPrompt`) |
| Renommer un lieu | Lieux de stockage | **Renommer** / double-clic | Oui | **Nouveau.** :735-740 → :1499 (`StorageLocations.RenamePrompt`) |
| Désactiver / réactiver un lieu | Lieux de stockage | **Désactiver** / **Réactiver** | Oui | **Nouveau.** :741 → :1525, libellé l.1472 ; confirmation `StorageLocations.DeactivateConfirm` l.1532 ; colonnes **Lieu de stockage**, **Statut**, **Tubes rangés** (l.529-536, 889-891) |
| Imprimantes du poste | Paramètres › Imprimantes | **Enregistrer**, **Imprimer une étiquette de test**, **Actualiser** | Oui | :596-598 |

### Boutons présents mais sans effet utile

- Utilisateurs : double-clic sur un compte sans effet (les autres écrans du chapitre ouvrent la fiche au double-clic).
- Données de référence : **Modifier** est actif dès que la liste n'est pas vide ; sans ligne courante, il ouvre une fiche de création (à vérifier).

### Manques à corriger (par impact sur le travail quotidien)

1. **Tarifs** inaccessibles au rôle Comptabilité (pas de lecture du catalogue), donc ni tarifs ni **Appliquer le coefficient officiel** ; pas de révision globale de la valeur du B.
2. **Synonymes des profils** effacés en modification.
3. Numérotation de l'année suivante créée au format par défaut, sans possibilité de la préparer.
4. Journal d'audit : n° de dossier sans effet sur l'onglet Modifications, pas d'export dédié à la demande d'accès CNDP, pas de filtre par enregistrement.
5. Réactivation d'un compte non tracée au journal de sécurité.
6. Mineurs : message d'erreur à l'ouverture de Données de référence pour la Comptabilité ; libellés « Désignation » sur le type de prescripteur/article, « Inclure les comptes désactivés » sur les données de référence, bouton « Rôles et droits » ambigu ; **Nouveau** paramètre sans site ni description ; **Nouveau rôle** sans description ni traductions ; modules `accounting`/`assets` sans libellé dans l'arbre des droits.

Corrigés depuis la vérification précédente : modification d'une analyse, d'un paramètre, d'un équipement et d'un prescripteur (les fenêtres s'ouvrent sur la fiche complète) ; **Nouveau site** livré (deuxième site, centre de prélèvement, laboratoire partenaire, donc case **Sous-traitée** utilisable) ; **Réimporter** donné au Technicien et au Biologiste.
