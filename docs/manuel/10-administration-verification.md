## Vérification de l'interface — Administration

Chemins relatifs à `src/`. « V/ » = `MarocLIS.Desktop/Views/`. Lignes relevées dans l'état actuel du code (un ajout « Nouveau site » est en cours dans `SettingsView.cs` et peut décaler les numéros de ce fichier).

### Utilisateurs (service de gestion des utilisateurs)

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister / chercher les comptes | Utilisateurs › Utilisateurs | **Rechercher…**, **Inclure les comptes désactivés** | Oui | V/UserAdministrationView.cs:316-321 |
| Créer un compte | Utilisateurs | **Nouvel utilisateur** | Oui | V/UserAdministrationView.cs:327 → CreateUserAsync :774. Le compte est créé sans rôle ni choix de site (site de l'administrateur) : le service accepte rôles et sites à la création mais la fenêtre ne les propose pas (2 étapes de plus). |
| Modifier une fiche (qualification, n° Ordre, INPE, habilitation, secteurs, prescripteur associé) | Utilisateurs | **Modifier** | Oui | :328 → :796. Secteurs saisis en texte libre (codes) : aucune liste de choix, aucun contrôle que le code existe. |
| Attribuer des rôles | Utilisateurs | **Rôles et droits** | Oui | :329 → :822. Le bouton porte le même libellé que l'onglet « Rôles et droits » : prête à confusion (libellé « Attribuer les rôles » conseillé). |
| Sites accessibles + site par défaut | Utilisateurs | **Sites accessibles**, **Site par défaut** | Oui | :330 → :842. La liste montre tous les sites actifs, y compris un éventuel « Laboratoire correspondant » (à vérifier). |
| Réinitialiser le mot de passe | Utilisateurs | **Réinitialiser le mot de passe** | Oui | :331 → :862 |
| Déverrouiller | Utilisateurs | **Déverrouiller** | Oui | :332 → :887 |
| Désactiver / réactiver avec motif | Utilisateurs | **Désactiver** / **Réactiver** | Oui | :333 → :902. La réactivation n'est pas inscrite au journal de sécurité (seule la désactivation l'est) : à vérifier si c'est voulu. |
| Créer un rôle | Utilisateurs › Rôles et droits | **Nouveau rôle** | PARTIEL | :338 → :927. Désignation française seulement ; description et désignation arabe/anglaise (acceptées par le service) non saisissables. Aucun moyen de renommer un rôle. |
| Modifier les droits d'un rôle | Rôles et droits | arbre + **Enregistrer les droits** | Oui | :340 → :981. Les modules `accounting` et `assets` n'ont pas de libellé français (« PermissionModule.accounting/assets » absents de fr.json) : en-têtes d'arbre bruts, à vérifier à l'écran. |
| Activer / désactiver un rôle créé | Rôles et droits | **Désactiver le rôle** / **Activer le rôle** | Oui | :339 → :956 |
| Journal de sécurité | Utilisateurs › Journal de sécurité | **N'afficher que l'utilisateur sélectionné**, **Actualiser** | Oui | :352-353 ; limité aux 300 derniers événements. |
| Compte de service `interface` des automates | Utilisateurs | **Nouvel utilisateur** | PARTIEL | Demandé par l'écran Automates ; aucune aide sur les droits à lui donner. |

### Journal d'audit

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Journal des modifications | Journal d'audit › Modifications | **Du**, **Au**, **Identifiant**, **Rechercher…**, **Appliquer** | Oui | V/AuditTrailView.cs:224 |
| Journal des consultations | › Consultations | idem | Oui | idem |
| Événements de sécurité | › Événements de sécurité | idem | Oui | idem |
| Demande d'accès d'un patient (CNDP) | › Consultations | **N° de dossier** | PARTIEL | :363. Le filtre ne s'applique qu'à l'onglet Consultations ; les modifications du dossier du patient ne sont pas filtrables par n° de dossier. Aucun export dédié : seule l'impression générique (**Imprimer** / Ctrl+P) de la grille la plus grande visible. |
| Historique d'un enregistrement précis / d'un patient par identifiant interne | — | — | MANQUANT | Filtres « enregistrement » et « patient » prévus par le service, non proposés à l'écran. |

### Catalogue des analyses

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Créer une analyse | Catalogue › Analyses | **Nouvelle analyse** | Oui | V/CatalogView.cs:450 → :1209 |
| Modifier une analyse | Catalogue › Analyses | **Modifier l'analyse** | PARTIEL — **grave** | :451. La fenêtre s'ouvre avec Libellé court, Synonymes, LOINC, Technique, Réactif, Volume, Jeûne, Stabilités, Renseignement clinique, Maladie à déclaration obligatoire, Laboratoire partenaire **vides**, Délai urgent à 120, Réalisable en urgence coché, Ordre d'affichage à 0 (V/CatalogEditors.cs:74-135). À l'enregistrement le service remplace tout (Application/Catalog/CatalogMaintenanceService.cs `Apply`, ~l.604-630) : ces données sont **effacées**. Les jours d'exécution (`ScheduledDaysCsv`) n'ont aucun champ et sont remis à vide à chaque enregistrement. Le message rouge (« ne les renseignez que si vous voulez les remplacer ») est inexact. |
| Analyse sous-traitée | fiche analyse | **Sous-traitée** + **Laboratoire partenaire** | PARTIEL | Champs présents ; mais la liste ne propose que les sites « Laboratoire correspondant », qu'aucun écran ne permet de créer dans le code actuel (pas de création de site). Sur une installation réelle (hors démo) la case est inutilisable. Réglé quand **Nouveau site** sera livré (à suivre). |
| Créer / modifier un paramètre | Catalogue › Analyses | **Nouveau paramètre**, **Modifier le paramètre** | PARTIEL | :452-453. En modification, **Réponses autorisées**, **Libellé court**, **Code LOINC** s'ouvrent vides (V/CatalogEditors.cs:420-470) : pour un paramètre à liste de choix, il faut ressaisir toutes les réponses, sinon refus, ou retrait des réponses non ressaisies ; LOINC et libellé court effacés. |
| Paramètre calculé | fiche paramètre | **Formule de calcul** | Oui | contrôle des codes avant enregistrement (V/CatalogEditors.cs ~628) |
| Valeurs usuelles | Catalogue › Analyses | **Nouvel intervalle**, **Modifier l'intervalle** | Oui | :454-455 |
| Revue périodique | Catalogue › Analyses | **Enregistrer la revue** | Oui | :456 → :1331 |
| Nouveau tarif / révision du B | Catalogue › Tarifs | **Nouveau tarif** | PARTIEL | :457 → :1350. Une seule analyse à la fois : pas de révision globale de la valeur de la lettre B (décret) ; il faut ressaisir chaque analyse. |
| Corriger un tarif | Catalogue › Tarifs | **Modifier le tarif** | Oui | :458 |
| Prix laboratoire depuis la fiche analyse | fiche analyse | **Lettre-clé**, **Coefficient**, **Valeur de la lettre-clé**, **Prix laboratoire** | Oui | CatalogView.cs:1252-1270 |
| Tarifs par le service comptable | — | — | PARTIEL | Le rôle Comptabilité a le droit des tarifs mais pas `catalog.view` : le menu Catalogue ne lui est pas affiché et la lecture des analyses est refusée. Avec les rôles livrés, seul l'administrateur peut saisir un tarif (le biologiste responsable n'a pas le droit des tarifs). |
| Profils | Catalogue › Profils | **Nouveau profil**, **Modifier le profil** | PARTIEL | :464-465 ; en modification les **Synonymes de recherche** s'ouvrent vides et sont effacés (CatalogEditors.cs:1361). |
| Analyses en cascade | Catalogue › Analyses en cascade | **Nouvelle règle en cascade**, **Modifier la règle** | Oui | :466-467 |
| Listes de référence | Catalogue › Listes de référence | **Ajouter**, **Modifier** | Oui | :468-469. Le responsable de secteur (`SupervisorUserId`) accepté par le service : champ à vérifier dans la fenêtre. |
| Supprimer une entrée | — | — | (voulu) | Pas de suppression : désactivation par **Actif**. |

### Automates

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Créer une liaison | Automates | **Nouvelle interface** | Oui | V/InstrumentInterfaceView.cs:235 → :661 |
| Modifier une liaison | Automates | **Modifier l'interface** | Oui | :236 |
| Activer / désactiver | Automates | **Activer la liaison** / **Désactiver la liaison** | Oui | :237 → :791 |
| Table de correspondance | Automates | **Table de correspondance**, **Ajouter des paramètres…**, **Retirer la ligne** | Oui | :238 → :713 |
| Tester une trame | Automates | **Tester une trame…**, **Analyser la trame**, **Charger la dernière trame reçue** | Oui | :239 → :742 |
| Voir la trame brute | Automates | **Voir la trame brute** | Oui | :244 → :821 |
| Réimporter / accepter une transmission retenue | Automates | **Réimporter** | PARTIEL | :248 → :843. Droit livré au seul biologiste responsable (et admin). Avec l'acceptation automatique désactivée (réglage recommandé), aucun technicien ne peut libérer les résultats retenus, alors que la notice dit « jusqu'à ce qu'un technicien la valide ». |
| Purger le journal | Automates | **Purger le journal** | Oui | :249 → :882 (administrateur seul) |

### Données de référence

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Équipement : créer | Données de référence › Automates | **Nouvel équipement** | Oui | V/MasterDataView.cs:393 → :958 |
| Équipement : modifier | idem | **Modifier** | PARTIEL | :394. Version logicielle, Fournisseur, Contrat de maintenance, Assistance technique, Mise en service s'ouvrent vides (V/MasterDataEditors.cs:368-380) et sont effacés à l'enregistrement (Application/Administration/MasterDataService.cs ~l.450-461). |
| Matériau / cible de contrôle | › Contrôle interne | **Nouveau matériau de contrôle**, **Nouvelle cible de contrôle**, **Modifier** | Oui | :397-403 |
| Articles | › Articles | **Nouvel article**, **Modifier** | Oui | :405-407 |
| Fournisseurs | › Fournisseurs | **Nouveau fournisseur**, **Modifier** | Oui | :409-411 |
| Prescripteur : créer | › Prescripteurs | **Nouveau prescripteur** | Oui | :413 |
| Prescripteur : modifier | › Prescripteurs | **Modifier** | PARTIEL — **grave** | :414. ICE, Téléphone, Fax, Adresse, **Téléphone résultats critiques**, Observations s'ouvrent vides (MasterDataEditors.cs:1133-1161) et sont effacés ; le téléphone enregistré est reproposé dans **Téléphone mobile** (l.1138) ; l'organisme payeur par défaut est remis à vide à chaque enregistrement (aucun champ). Le choix du type de prescripteur porte le libellé « Désignation » (l.1123 ; idem type d'article l.767). |
| Organismes payeurs | › Organismes payeurs | **Nouvel organisme payeur**, **Modifier** | Oui | :417-418 |
| Ouverture par la Comptabilité | Données de référence | — | PARTIEL | Le rôle Comptabilité voit le menu (commandes, tarifs) mais n'a pas la lecture des équipements : un message d'erreur de droits s'affiche à chaque ouverture (MasterDataView.cs:673) et la ligne d'état annonce « Équipements » comme inexploitable. |
| Libellé de la case | Données de référence | **Inclure les comptes désactivés** | PARTIEL | :584 — libellé repris des comptes utilisateurs ; il s'agit des entrées désactivées. |

### Paramètres

| Scénario | Écran | Bouton / champ | Présent ? | Remarque |
|---|---|---|---|---|
| Compléter l'identité du site | Paramètres › Site | **Enregistrer** | Oui | V/SettingsView.cs:650 → SaveSiteAsync |
| Logo | Paramètres › Site | **Choisir le logo…**, **Retirer le logo** | Oui | :652-653 |
| **Créer un site** (centre de prélèvement, laboratoire correspondant) | Paramètres › Site | **Nouveau site** | **MANQUANT (à venir)** | Dans le code livré, l'onglet ne fait que modifier les sites existants. Un bouton **Nouveau site** est en cours d'ajout (présent dans l'arbre de travail : SettingsView.cs:651 → CreateSiteAsync :1081, service `CreateSiteAsync`), décrit « à venir » dans le manuel. Conséquence actuelle : impossible d'ouvrir un 2e site ou de déclarer un laboratoire partenaire pour la sous-traitance. Contournement possible mais dangereux : changer le **Type de site** d'un site existant. |
| Numéro de dossier et bordereau de sous-traitance | Paramètres › Numérotations | — | Oui (info) | Un compteur unique partagé par tous les sites (Infrastructure/Sequencing/SequenceAllocator.cs `CounterSite`) ; colonne **Site** = « — ». Manuel à jour. |
| Modifier un format | Numérotations | **Modifier le format** | Oui | :662 |
| Clôturer / réouvrir une série | Numérotations | **Clôturer la série** / **Réouvrir la série** | Oui | :663 |
| Préparer la série de l'année suivante | Numérotations | — | MANQUANT | La série d'une nouvelle année est créée au premier document avec le format par défaut (SequenceAllocator.cs:43) : un format personnalisé n'est pas reporté et ne peut plus être corrigé une fois le premier numéro donné. Colonne année intitulée « Date ». |
| Modifier / créer un paramètre | Paramètres › Paramètres | **Modifier**, **Nouveau** | PARTIEL | :666-667. **Nouveau** crée toujours un paramètre commun à toute l'installation : pas de choix du site, de la description ni du caractère sensible (acceptés par le service). |
| Imprimantes du poste | Paramètres › Imprimantes | **Enregistrer**, **Imprimer une étiquette de test**, **Actualiser** | Oui | :528-530 |

### Boutons présents mais sans effet utile

- Utilisateurs : double-clic sur un compte sans effet (les autres écrans du chapitre ouvrent la fiche au double-clic).
- Données de référence : **Modifier** est actif dès que la liste n'est pas vide ; sans ligne courante, il ouvre une fiche de création (à vérifier).
- Catalogue, fiche analyse : case **Sous-traitée** inutilisable tant qu'aucun site « Laboratoire correspondant » n'existe (voir plus haut).

### Manques à corriger (par impact sur le travail quotidien)

1. **Modifier l'analyse efface des données** (LOINC, stabilités, jeûne, renseignement clinique, maladie à déclaration obligatoire, délai urgent, laboratoire partenaire, jours d'exécution) — préremplir la fenêtre avec la fiche complète.
2. **Modifier un prescripteur efface le téléphone résultats critiques, l'ICE, le fax, l'adresse, les observations et l'organisme par défaut** ; mélange téléphone/mobile.
3. **Création de site absente** (bouton **Nouveau site** à venir) : bloque 2e site, centres de prélèvement et laboratoire partenaire, donc la case **Sous-traitée**.
4. **Réimporter** réservé au biologiste responsable : sans acceptation automatique, les techniciens ne peuvent pas libérer les résultats des automates.
5. **Modifier le paramètre** n'affiche pas les réponses autorisées (liste de choix à ressaisir), efface LOINC et libellé court.
6. **Tarifs** inaccessibles au rôle Comptabilité (pas de lecture du catalogue) ; pas de révision globale de la valeur du B.
7. **Modifier un équipement** efface version logicielle, fournisseur, contrat, assistance, mise en service.
8. Numérotation de l'année suivante créée au format par défaut, sans possibilité de la préparer.
9. Journal d'audit : n° de dossier sans effet sur l'onglet Modifications, pas d'export dédié à la demande d'accès CNDP.
10. Mineurs : message d'erreur à l'ouverture de Données de référence pour la Comptabilité ; libellés « Désignation » sur le type de prescripteur/article, « Inclure les comptes désactivés » sur les données de référence, bouton « Rôles et droits » ambigu ; synonymes des profils effacés en modification ; **Nouveau** paramètre sans site ni description ; modules `accounting`/`assets` sans libellé dans l'arbre des droits.
