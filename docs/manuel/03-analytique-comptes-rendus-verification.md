# Vérification de l'interface — Phase analytique et comptes rendus

Méthode : revérifié le 2026-10-17 au commit 4e20d0d (première vérification au commit 4b2639d), puis complété le même jour pour les changements du cycle 5 (commits c0b6581, 1a1c0e3, fd6beeb, 7d23c62 ; état du code au commit 1bbef5b). Chaque fonction offerte par l'application pour ce module (saisie, validation technique, automates, validation biologique, résultats critiques, comptes rendus, remises) a été recherchée dans les écrans et chaque ligne a été recontrôlée dans le code actuel. Références « fichier:ligne » = endroit du code où le bouton est branché (écrans dans `src/MarocLIS.Desktop/Views/`, règles dans `src/MarocLIS.Application/`).

Légende : **OUI** = présent et utilisable ; **PARTIEL** = présent mais incomplet ou mal réglé ; **MANQUANT** = possible seulement par programme / tests, aucun bouton.

## Saisie des résultats

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Ouvrir un dossier par n° ou code-barres | Saisie des résultats | **N° de dossier ou code-barres** + Entrée, **Ouvrir** | OUI | ResultEntryView.cs:650, 672, 1076-1078 ; la frappe filtre aussi la liste (1342-1351) |
| Liste des dossiers à saisir | Saisie des résultats | **À saisir · n**, **Actualiser** | OUI | ResultEntryView.cs:651, 1081, 1254 |
| Dossier gardé dans la liste jusqu'à la validation technique | Saisie des résultats | pastille **À valider** | OUI | ResultEntryView.cs:1016-1020 ; Results/BenchWorksheetService.cs:180 (includeEntered) (nouveau) |
| Filtrer par secteur | Saisie des résultats | puces **Tous** + une par secteur | PARTIEL | ResultEntryView.cs:271-273, 1334-1348 : filtre la liste des dossiers ; la grille d'un dossier montre toujours toutes ses disciplines (OpenAsync sans secteur, 1471) |
| Saisir valeurs numériques, censurées, titres, codes, texte | Saisie des résultats | colonne **Résultat**, **Enregistrer** (Ctrl+S) | PARTIEL | ResultEntryView.cs:684, 2015-2016. Les réponses codées se tapent toujours par leur code, aucune liste déroulante |
| Commentaire technique | Saisie des résultats | **Plus d'actions** › **Commentaire technique…**, F3 | PARTIEL | ResultEntryView.cs:713, 771, 1794 : toujours imprimé sur le compte rendu ; pas de commentaire purement interne. Colonne masquée à l'écran, imprimée avec Ctrl+P |
| Modifier seulement le commentaire d'une valeur existante | Saisie des résultats | **Commentaire technique…** | OUI | Corrigé : fenêtre propre au commentaire (ResultEntryView.cs:1794), sans retaper la valeur |
| Marquer une analyse « Non fait » avec motif | Saisie des résultats | **Plus d'actions** › **Non fait…** | OUI | ResultEntryView.cs:710, 2041, 2057-2073 (nouveau ; motif 3 à 500 caractères) |
| Effacer une valeur saisie par erreur | Saisie des résultats | **Plus d'actions** › **Effacer la valeur** | OUI | ResultEntryView.cs:711, 2042, 2081-2095 (nouveau) |
| Facteur de dilution | Saisie des résultats | — | MANQUANT | Toujours pris en charge par l'enregistrement (signalement ×) sans champ à l'écran |
| Automate / lot de réactif d'une valeur manuelle | Saisie des résultats | colonne **Origine** | PARTIEL | ResultEntryView.cs:1108, 1759 : l'origine affiche l'automate d'une valeur reçue ou « Manuel » ; on ne peut toujours pas désigner l'automate d'une valeur tapée |
| Paramètres calculés | Saisie des résultats | lignes « ƒ » grisées | OUI | Recalcul à l'enregistrement |
| Signalements (▲ ▼ Δ CIQ ! …) | Saisie des résultats | colonne **Indic.** | OUI | Corrigé : la pastille d'interprétation et les autres signalements s'affichent ensemble (ResultEntryView.cs:925-942, 952-956) |
| Delta-check | Saisie des résultats | Δ, colonne **Antériorité** | OUI | Calcul automatique |
| Alerte valeur critique à la saisie | Saisie des résultats | fenêtre « Résultats critiques » | PARTIEL | ResultEntryView.cs:2154-2156 ; avertissement seulement, pas d'accès à la communication ; le Technicien de laboratoire n'a pas le droit de saisir l'appel |
| Analyses en cascade | Saisie des résultats | fenêtre « Analyses en cascade » | PARTIEL | ResultEntryView.cs:2169 : simple information. Les règles « ajout automatique » n'ajoutent rien ; aucun bouton « Ajouter au dossier » |
| Validation technique du dossier | Saisie des résultats | **Validation technique** | PARTIEL | ResultEntryView.cs:685, 2177-2196 : tout le dossier en bloc (l'application accepte une sélection de lignes) |
| Seconde validation technique sans écraser le premier contrôle | Saisie des résultats | **Validation technique** | OUI | Corrigé (cycle 5, c0b6581) : Results/ResultEntryService.cs:857 ne reprend plus les valeurs déjà validées techniquement ; le nom et l'heure du premier technicien restent |
| Bouton grisé une fois tout validé | Saisie des résultats | **Validation technique** | OUI | Corrigé (cycle 5, c0b6581) : ResultEntryView.cs:2264 (actif seulement s'il reste une valeur au statut Saisi) |
| Compteur « … valeurs restent à saisir » / « Tout est saisi » toujours visible | Saisie des résultats | barre du bas | OUI | Corrigé (cycle 5, 1a1c0e3) : ResultEntryView.cs:337-407, le texte passe sur sa propre ligne quand la barre est trop étroite (arabe, fenêtre réduite) |
| Corriger une valeur validée techniquement | Saisie des résultats | **Enregistrer** | PARTIEL | Réservé au droit « modifier un résultat », absent du rôle Technicien de laboratoire par défaut (choix de sécurité) |
| Corriger une valeur validée ou rendue (rectification motivée) | Saisie des résultats | **Plus d'actions** › **Corriger une valeur validée…** | OUI | Corrigé : ResultEntryView.cs:712, 2045-2048, 2103-2140 ; Results/ResultEntryService.cs:561-680 (ancienne valeur gardée, motif ≥ 15 caractères, revalidation, calculs dépendants rouverts) |
| Antériorités (courbe + tableau) | Saisie des résultats | **Antériorités**, double-clic **Antériorité** | OUI | ResultEntryView.cs:695, 715, 1860 ; ResultHistoryDialog |
| Fiche de paillasse | Saisie des résultats | **Fiche de paillasse** → **Secteur**, **Automate**, **Urgences seulement**, **Imprimer** | OUI | ResultEntryView.cs:669, 1869-1919 ; BenchWorksheetDialog.cs |
| Question avant de perdre des valeurs tapées | Saisie des résultats | « Des modifications n'ont pas été enregistrées. Voulez-vous continuer ? » | OUI | ResultEntryView.cs:1554 ; posée aussi en quittant l'écran |

## Résultats des automates

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Accepter une transmission retenue | Automates | **Réimporter** | OUI | Corrigé : InstrumentInterfaceView.cs:262, 668, 887-899 ; droit « rejouer les messages » donné par défaut au Technicien de laboratoire et au Biologiste (Infrastructure/Persistence/ReferenceDataSeeder.cs, rôles) |
| Voir la trame | Automates | **Voir la trame brute** | OUI | InstrumentInterfaceView.cs:261, 865 |
| Tester une trame sans rien enregistrer | Automates / fenêtre de l'interface | **Tester une trame…** → **Analyser la trame**, **Charger la dernière trame reçue** | OUI | InstrumentInterfaceView.cs:260, 752 ; InstrumentInterfaceDialogs.cs:223 ; InstrumentFrameTestDialog.cs:84-93 |
| Exemple de trame INTEGRA | fenêtre « Tester une trame » | **Exemple de trame** | OUI | InstrumentFrameTestDialog.cs:100-108 (nouveau ; seulement pour une liaison INTEGRA) |
| Préréglage COBAS INTEGRA 400 plus | fenêtre de l'interface | **Profil fabricant** « Roche cobas INTEGRA 400 plus » | OUI | InstrumentInterfaceDialogs.cs:194, 270-282, 352, 393-408 (protocole, RS-232, liste de travail, 9600 8N1, XOnXOff, ASCII, 180 s, 3 tentatives) ; note 426 (nouveau) |
| Correspondance par numéro de test INTEGRA | **Table de correspondance** | code automate = n° de test (« 031 », « 31 », « 0-031 ») | OUI | Corrigé (cycle 5, 1a1c0e3) : le résultat qui revient en « 003 » retrouve une ligne écrite « 0-003 » ou « 3 » (Instruments/Hosting/InstrumentMessageSink.cs:846) ; avant, il était refusé « code non correspondant ». Instruments/Protocol/RocheIntegraProtocol.cs:412-423 ; table de démonstration Infrastructure/Persistence/Demo/DemoDataSeeder.Reference.cs:803-830 (nouveau). HDL, LDL direct, CRP, HbA1c sans numéro livré |
| Table INTEGRA : code qui n'est pas un numéro de test | **Table de correspondance**, **Enregistrer** | refus `Interface_IntegraTestNumberInvalid` | OUI | Nouveau (cycle 5, fd6beeb) : Application/Instruments/InstrumentInterfaceService.cs:492-503, 539-552 ; même numéro écrit deux fois refusé (`Interface_DuplicateMappingCode`, l.551). Décrit au chapitre 10 |
| Parité, bits d'arrêt, contrôle de flux en français | fenêtre de l'interface | **Parité**, **Bits d'arrêt**, **Contrôle de flux** | OUI | Corrigé (cycle 5, c0b6581) : InstrumentInterfaceDialogs.cs:102-110, 446-449 ; valeurs enregistrées inchangées (None, One, XOnXOff…) |
| Rangée d'actions de « Tester une trame » en arabe | fenêtre « Tester une trame » | **Analyser la trame**, **Charger la dernière trame reçue**, **Exemple de trame** | OUI | Corrigé (cycle 5, 1a1c0e3) : InstrumentFrameTestDialog.cs:128-147, boutons placés depuis la droite en arabe |
| Alerte valeur critique reçue de l'automate | — | — | MANQUANT | Les valeurs critiques détectées à l'import ne déclenchent toujours aucune alerte (Instruments/Hosting/InstrumentMessageSink.cs) ; seuls la liste de validation et le tableau de bord les montrent |
| Analyses en cascade sur valeurs d'automate | — | — | MANQUANT | Toujours non évaluées à l'import |
| Alarmes de l'automate | Saisie / Validation | info-bulle **Indic.** ; colonne **Alarme automate** | OUI | ResultEntryView.cs:1599 ; ValidationView.cs:997 |

## Validation biologique

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste des dossiers à valider, triée par urgence, filtres | Validation biologique | puces **Tous**, **Urgents**, **Avec anomalies**, **Bloqués par le CQ**, **Plus d'actions** › **Actualiser** | OUI | ValidationView.cs:210, 630, 1121-1124 (la case « Urgent » est remplacée par les puces) |
| Filtrer par secteur | Validation biologique | — | MANQUANT | Prévu par l'application (departmentId) ; seul le périmètre de l'utilisateur filtre |
| Retrouver les dossiers validés non imprimés | Validation biologique | **Afficher les résultats validés** | OUI | ValidationView.cs:60, 176, 963 |
| Contexte du dossier (anomalies, délai, critiques, CQ, renseignements cliniques) | Validation biologique | carte **Contexte**, lien **Voir le dossier complet ›** | OUI | ValidationView.cs:964-966, 709-710 (nouveau) |
| Tout cocher / décocher | Validation biologique | **Plus d'actions** › **Tout sélectionner**, touche Espace | OUI | ValidationView.cs:640, 658-663, 729-733 |
| Valider (avec verrous qualification / CIQ / critique / étalonnage) | Validation biologique | **Valider la sélection (n)** (F9) | OUI | ValidationView.cs:646, 722, 1422, 1655-1703 |
| Message après validation | Validation biologique | ligne d'état « … résultat(s) validé(s) » | OUI | Corrigé (cycle 5, 7d23c62) : ValidationView.cs:1705 (clé `Validation.ValidatedCount`) ; avant : « 3 validé par » (phrase coupée), « 0 validé par » après un refus |
| Bouton grisé sans dossier ouvert | Validation biologique | **Valider la sélection** | OUI | Corrigé (cycle 5, 1a1c0e3) : ValidationView.cs:1718 ; reste actif pour un Administrateur sans qualification, refusé au clic (choix de conception) |
| Dossier suivant au clavier | Validation biologique | F10 | OUI | ValidationView.cs:726-727 (nouveau) |
| Dérogation CIQ motivée | Validation biologique | fenêtre « Dérogation contrôle interne » | OUI | ValidationView.cs:1666-1668 ; une seule justification pour toutes les lignes CIQ |
| Commentaire du biologiste par résultat | Validation biologique | **Commentaire biologique · paramètre** | OUI | Corrigé : ValidationView.cs:707, 1446-1447, 1471, 1706 ; enregistré à la validation de la ligne, imprimé sous la valeur |
| Refuser un résultat / le renvoyer à la paillasse | Validation biologique | case décochée | PARTIEL | Décocher laisse seulement la ligne en attente ; le renvoi se fait ailleurs (« Repasser l'analyse » dans Consultation d'un dossier) |
| Double-clic sur un dossier | Validation biologique | double-clic | OUI | ValidationView.cs:700-705 : place le curseur dans les résultats pour cocher (Espace) et valider (F9), comme le dit maintenant le code ; n'ouvre pas la saisie (le lien **Voir le dossier complet ›** ouvre le dossier) |
| Communiquer un critique avec relecture | Validation biologique | **Résultats critiques…** | PARTIEL | ValidationView.cs:647, 973, 1740-1770. Mode toujours « téléphone » ; numéro appelé, fonction de l'interlocuteur et prescripteur non demandés ; la valeur répétée n'est pas comparée à la valeur réelle |
| Tracer « injoignable » avec un commentaire | Validation biologique | (Annuler sur le nom) | PARTIEL | ValidationView.cs:1757-1760 : texte fixe « Communication abandonnée par l'opérateur » ; pas de zone de note |
| Tracer un refus de l'interlocuteur / une escalade au biologiste de garde | — | — | MANQUANT | Prévu dans les données (Refusé, Escaladé) sans bouton |
| Liste des résultats critiques en attente d'appel / historique des appels | — | — | MANQUANT | Seulement le compteur du Tableau de bord, les pastilles de la liste et la carte Contexte ; aucun écran d'historique des appels |
| Appeler un critique dès la saisie (technicien) | Saisie des résultats | — | MANQUANT | Bouton seulement dans Validation biologique ; le rôle Technicien de laboratoire n'a pas le droit |
| Antériorités | Validation biologique | **Antériorités**, double-clic **Antériorité** | OUI | ValidationView.cs:669, 675 |
| Émettre le compte rendu | Validation biologique | **Compte rendu** | PARTIEL | ValidationView.cs:648, 654, 1376, 1794-1816 : ni conclusion du biologiste, ni choix de la langue à la première émission (seulement en rectification). Bouton grisé quand rien n'est à imprimer (corrigé) |
| Dévalider avant impression | Validation biologique | **Plus d'actions** › **Dévalider…** | OUI | Corrigé : ValidationView.cs:649, 970, 1393, 1572-1616 ; droit donné au Biologiste par défaut |

## Validation à distance (téléphone)

Service `MarocLIS.Api` (`src/MarocLIS.Api/`) et application du téléphone `MarocLIS.Mobile` (`src/MarocLIS.Mobile/`, Blazor WebAssembly servi par le service). Tests dans `tests/MarocLIS.Tests/Api/` et `tests/MarocLIS.Tests/Mobile/`. Jeu de démonstration : `tools/mobile-demo`.

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Se connecter depuis le téléphone (deux droits exigés), choix de la langue | Connexion | **Identifiant**, **Mot de passe**, **Se connecter**, **Français** / **العربية** / **English** | OUI | Pages/SignIn.razor ; Endpoints/SessionEndpoints.cs |
| Session fermée à l'inactivité, après 12 h, au changement du compte, sur un autre appareil | Connexion (variantes) | **Se reconnecter** | OUI | Services/SessionState.cs (même délai que le serveur) ; Security/SessionCookie.cs, Security/MobileSessionStore.cs ; toute réponse 401 ramène à la connexion |
| Liste à valider avec filtres et compteurs | À valider | **Tous**, **Urgents**, **Avec anomalies**, **CQ**, **Actualiser** | OUI | Pages/Worklist.razor ; mêmes règles que le poste (Application/Results/ValidationScreenRules.cs) |
| Ouvrir un dossier, contexte, antériorités | Dossier, Antériorités | **Dossier ›**, **Ant.** | OUI | Pages/OrderPage.razor, Components/ContextSheet.razor, Pages/HistoryPage.razor (courbe SVG, valeurs « reprise ») |
| Valider avec contrôle de la valeur affichée | Dossier | **Valider la sélection (*n*)** | OUI | Envoi de l'heure de saisie affichée ; 409 : message et relecture du dossier |
| Dérogation CIQ (15 à 1000 caractères) | Dérogation contrôle interne | **Justification**, **Valider avec dérogation** | OUI | Components/DerogationSheet.razor |
| Communiquer un critique / injoignable | Communiquer une valeur critique | **Téléphone** / **Sur place**, **Personne prévenue**, **Valeur répétée par la personne**, **Enregistrer la communication**, **Injoignable** | OUI | Components/CriticalSheet.razor ; Endpoints/CriticalEndpoints.cs (moyen Téléphone ou Sur place) |
| Émettre le compte rendu et le voir en images | Validée, Émettre, Compte rendu | **Émettre et signer le compte rendu**, **Émettre et signer**, **Terminé** | OUI | Pages/OutcomePage.razor, Components/IssueSheet.razor, Pages/PreviewPage.razor ; aperçu seulement pendant la session qui l'a émis, sans téléchargement |
| Mon compte, langue, déconnexion | Mon compte | **Langue**, **Se déconnecter** | OUI | Components/AccountSheet.razor ; `PUT /api/session/langue` (langue de la session seulement) |
| Hors connexion | Toutes | bandeau **Hors connexion** | OUI | Layout/MainLayout.razor ; le service worker ne met jamais `/api` en cache (wwwroot/service-worker.published.js) |
| Installer sur l'écran d'accueil | — | Android : **Ajouter à l'écran d'accueil** ; iPhone : **Sur l'écran d'accueil** | OUI | wwwroot/manifest.webmanifest (« MarocLIS Validation », icônes 192 et 512) |

## Comptes rendus

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Onglets À émettre / Émis / À remettre | Comptes rendus | onglets avec compteurs | OUI | ReportRegisterView.cs:663-680 (nouveau ; remplace la case « Uniquement les comptes rendus non remis ») |
| Rechercher, période | Comptes rendus | recherche + Entrée, « *n* derniers jours », **Plus d'actions** › **Actualiser** | OUI | ReportRegisterView.cs:832, 838, 907, 928 |
| Aperçu de la première page, signataire, versions | Comptes rendus | carte **Aperçu**, **Ouvrir en PDF** | OUI | ReportRegisterView.cs:834, 844-847, 1180-1200 (nouveau ; la colonne Signataire passe dans l'aperçu) |
| Émettre un compte rendu en attente | Comptes rendus | **Émettre le compte rendu** (Ctrl+E), double-clic dans À émettre | OUI | ReportRegisterView.cs:839, 842, 1208, 1270-1293 |
| Réimprimer l'original archivé | Comptes rendus | **Réimprimer l'original**, double-clic registre, **Ouvrir en PDF** | OUI | Corrigé : ReportRegisterView.cs:840, 1212-1214, 1242-1256 ; droit donné au Secrétariat / accueil et au Biologiste par défaut |
| Enregistrer une remise (guichet, courriel, SMS, fax, coursier, interface, portail) | Comptes rendus | **Enregistrer une remise** → **Mode de remise**, **Qualité du destinataire**, **Destinataire**, **Adresse / numéro**, **Porteur**, **CIN du porteur** | OUI | ReportRegisterView.cs:841, 1217-1219, 1334-1355 ; ReportRegisterDialogs.cs:76-104, 217-248 |
| Remise refusée sur une version remplacée | Comptes rendus | **Enregistrer une remise** | OUI | ReportRegisterView.cs:1217 ; Reports/ReportReleaseService.cs:453 (nouveau) |
| Remise à un tiers avec consentement | Comptes rendus | **Référence du consentement** | PARTIEL | ReportRegisterDialogs.cs:104-106 : toujours l'identifiant interne du consentement (38 caractères) tapé à la main, sans liste des consentements du patient. Le service vérifie maintenant que c'est un consentement « communication à un tiers » en vigueur (ReportReleaseService.cs:473) |
| Marquer un SMS / fax / coursier comme finalement remis, annuler une remise | Comptes rendus | — | MANQUANT | Ces remises restent « Envoyé » ; aucun bouton pour les confirmer ou les annuler |
| Renvoyer après un échec d'envoi | Comptes rendus | **Enregistrer une remise** | OUI | Nouvelle tentative (ReportRegisterView.cs:1341-1344) |
| Rectification (nature, motif FR/AR, conclusion, langue) | Comptes rendus | **Plus d'actions** › **Émettre une rectification…** | PARTIEL | ReportRegisterView.cs:837, 1222, 1379-1403 ; ReportRegisterDialogs.cs:321-381. Corrigé : une « Correction de résultat » porte la valeur corrigée par la saisie et nomme les paramètres corrigés (ReportReleaseService.cs:30, 307, 837). Reste à vérifier : le « Retrait du compte rendu » réimprime lui aussi les résultats |
| Liste des remises d'une version | Comptes rendus | **Remises enregistrées** | OUI | ReportRegisterView.cs:876-896 |

## Boutons présents mais sans effet utile

- Fenêtre « Analyses en cascade » : informe sans proposer l'ajout, y compris pour les règles réglées en ajout automatique.

(Corrigés depuis la première vérification : le double-clic sur un dossier en Validation biologique fait ce qu'annonce le code ; le bouton **Compte rendu** est grisé quand rien n'est à imprimer ; le message « toute modification doit passer par une rectification motivée » mène maintenant à **Corriger une valeur validée…**.)

## Manques à corriger (par impact sur le travail quotidien)

1. **Alerte critique et cascade pour les valeurs d'automate** absentes (la majorité des résultats).
2. **Résultats critiques** : pas de liste/historique des appels, pas de note, de mode, de fonction ni de numéro ; relecture non comparée ; pas de refus ni d'escalade.
3. **Analyses en cascade** : pas d'ajout (automatique ou en un clic) au dossier.
4. **Remise à un tiers** : référence de consentement à taper sous forme d'identifiant interne.
5. Validation technique par ligne ; filtre par secteur dans la grille de saisie et en validation ; conclusion et langue dès la première émission.
6. Liste déroulante des réponses codées ; commentaire technique non imprimé ; dilution non saisissable ; automate d'une valeur manuelle ; confirmation des remises SMS/fax/coursier.
