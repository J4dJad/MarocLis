# Vérification de l'interface — Phase analytique et comptes rendus

Méthode : chaque fonction offerte par l'application pour ce module (saisie, validation technique, validation biologique, résultats critiques, comptes rendus, remises) a été recherchée dans les écrans. Références « fichier:ligne » = endroit du code où le bouton est branché (pour l'éditeur).

Légende : **OUI** = présent et utilisable ; **PARTIEL** = présent mais incomplet ou mal réglé ; **MANQUANT** = possible seulement par programme / tests, aucun bouton.

## Saisie des résultats

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Ouvrir un dossier par n° ou code-barres | Saisie des résultats | **N° de dossier / Code-barres** + Entrée, **Ouvrir** | OUI | ResultEntryView.cs:472, 494 |
| Liste des dossiers à saisir | Saisie des résultats | **À saisir (n)**, **Actualiser** | OUI | ResultEntryView.cs:473-490 |
| Filtrer la grille par secteur | Saisie des résultats | — | MANQUANT | L'ouverture accepte un secteur (ResultEntryService.OpenAsync, departmentId) mais l'écran ne le propose pas : un technicien voit toutes les disciplines du dossier |
| Saisir valeurs numériques, censurées, titres, codes, texte | Saisie des résultats | colonne **Résultat**, **Enregistrer** (Ctrl+S) | PARTIEL | ResultEntryView.cs:502 → 1048. Les réponses codées se tapent par leur code, aucune liste déroulante (les réponses autorisées sont chargées mais non affichées) |
| Commentaire technique | Saisie des résultats | colonne **Commentaire technique** | PARTIEL | Toujours marqué « imprimé sur le compte rendu » dès qu'il est rempli (ResultEntryView.cs:1076) : pas de commentaire purement interne |
| Modifier seulement le commentaire d'une valeur existante | Saisie des résultats | **Enregistrer** | PARTIEL (à vérifier) | La valeur affichée est renvoyée telle quelle : pour un nombre ≥ 1000 affiché avec séparateur de milliers, ou une ligne « Non fait », la relecture peut échouer (« n'est pas une valeur numérique valide »). Le renvoi remet aussi la ligne au statut Saisi |
| Marquer une analyse « Non fait » avec motif | Saisie des résultats | — | MANQUANT | Prévu par l'application (case vide = non fait, ResultEntryService ApplyEntry) mais l'écran ignore les cases vides (ResultEntryView.cs:1073) |
| Effacer une valeur saisie par erreur | Saisie des résultats | — | MANQUANT | Case vidée = ignorée |
| Facteur de dilution | Saisie des résultats | — | MANQUANT | Pris en charge par l'enregistrement (DilutionFactor, signalement ×) sans champ à l'écran |
| Automate / lot de réactif d'une valeur manuelle | Saisie des résultats | — | MANQUANT | Lot déduit automatiquement du stock ; l'automate n'est jamais renseigné pour une saisie manuelle (vérification d'étalonnage impossible pour ces valeurs) |
| Paramètres calculés | Saisie des résultats | lignes « ƒ » grisées | OUI | Recalcul à l'enregistrement |
| Signalements (↑ ↓ Δ CIQ ! …) | Saisie des résultats | colonne **Signalements** | PARTIEL | UiTheme.FlagGlyphs : dès qu'un signalement (Δ, !, R…) existe, la flèche haut/bas/critique n'est plus affichée (seule la couleur la signale) |
| Delta-check | Saisie des résultats | Δ, colonne **Antériorité** | OUI | Calcul automatique |
| Alerte valeur critique à la saisie | Saisie des résultats | fenêtre « Résultats critiques » | PARTIEL | ResultEntryView.cs:1127 ; avertissement seulement, pas d'accès à la communication ; le Technicien n'a pas le droit de saisir l'appel |
| Analyses en cascade | Saisie des résultats | fenêtre « Analyses en cascade » | PARTIEL | ResultEntryView.cs:1138 : simple information. Les règles « ajout automatique » n'ajoutent rien ; aucun bouton « Ajouter au dossier » |
| Validation technique du dossier | Saisie des résultats | **Validation technique** | PARTIEL | ResultEntryView.cs:503 → 1152 : tout le dossier en bloc (l'application accepte une sélection de lignes) |
| Corriger une valeur validée techniquement | Saisie des résultats | **Enregistrer** | PARTIEL | Réservé au droit « modifier un résultat », absent du rôle Technicien par défaut |
| Rectifier une valeur validée ou rendue (rectification motivée) | — | — | MANQUANT | La fonction de rectification de valeur (TestResult.Revise) n'est appelée nulle part ; le message « toute modification doit passer par une rectification motivée » renvoie vers une action inexistante |
| Antériorités (courbe + tableau) | Saisie des résultats | **Antériorités**, double-clic **Antériorité** | OUI | ResultEntryView.cs:513-522 → 928 ; ResultHistoryDialog |
| Fiche de paillasse | Saisie des résultats | **Fiche de paillasse** → **Secteur**, **Automate**, **Urgences seulement**, **Imprimer** | OUI | ResultEntryView.cs:491 → 979 ; BenchWorksheetDialog.cs |

## Résultats des automates

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Accepter une transmission retenue | Automates | **Réimporter** | PARTIEL | InstrumentInterfaceView.cs:248 → 843. Droit réservé par défaut au Biologiste responsable et à l'Administrateur : la paillasse ne peut pas accepter elle-même les résultats retenus (réglage par défaut des interfaces) |
| Voir la trame | Automates | **Voir la trame brute** | OUI | |
| Alerte valeur critique reçue de l'automate | — | — | MANQUANT | Les valeurs critiques détectées à l'import ne déclenchent aucune alerte (InstrumentMessageSink, résultat de l'analyse ignoré) ; seuls la liste de validation et le tableau de bord les montrent |
| Analyses en cascade sur valeurs d'automate | — | — | MANQUANT | Non évaluées à l'import |
| Alarmes de l'automate | Saisie / Validation | info-bulle **Signalements** ; colonne **Alarme automate** | OUI | |

## Validation biologique

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste des dossiers à valider, triée par urgence | Validation biologique | liste, **Urgent**, **Actualiser** | OUI | ValidationView.cs:282-283 |
| Filtrer par secteur | Validation biologique | — | MANQUANT | Prévu par l'application (departmentId) ; seul le périmètre de l'utilisateur filtre |
| Retrouver les dossiers validés non imprimés | Validation biologique | **Afficher les résultats validés** | OUI | ValidationView.cs:284 |
| Tout cocher / décocher | Validation biologique | **Tout sélectionner** | OUI | ValidationView.cs:293 → 682 |
| Valider (avec verrous qualification / CIQ / critique / étalonnage) | Validation biologique | **Validation biologique** | OUI | ValidationView.cs:299 → 764 |
| Dérogation CIQ motivée | Validation biologique | fenêtre « Dérogation contrôle interne » | OUI | ValidationView.cs:800 ; une seule justification pour toutes les lignes CIQ |
| Commentaire du biologiste par résultat | Validation biologique | — | MANQUANT | Prévu (BiologistComment) mais aucun champ |
| Refuser un résultat / le renvoyer à la paillasse | Validation biologique | case décochée | PARTIEL | Décocher ne fait que compter un refus ; le renvoi se fait ailleurs (« Repasser l'analyse » dans Consultation d'un dossier) |
| Ouvrir le dossier en saisie depuis la liste | Validation biologique | double-clic sur un dossier | PARTIEL | ValidationView.cs:330 : le double-clic met seulement le curseur dans la grille du bas, n'ouvre pas la saisie (le commentaire du code dit le contraire) |
| Communiquer un critique avec relecture | Validation biologique | **Résultats critiques** | PARTIEL | ValidationView.cs:300 → 859. Mode toujours « téléphone » ; numéro appelé, fonction de l'interlocuteur et prescripteur non demandés ; la valeur répétée n'est pas comparée à la valeur réelle (tout texte non vide vaut confirmation) |
| Tracer « injoignable » avec un commentaire | Validation biologique | (Annuler sur le nom) | PARTIEL | Enregistré avec un texte fixe « Communication abandonnée par l'opérateur » ; pas de zone de note |
| Tracer un refus de l'interlocuteur / une escalade au biologiste de garde | — | — | MANQUANT | Prévu dans les données (Refusé, Escaladé) sans bouton |
| Liste des résultats critiques en attente d'appel / historique des appels | — | — | MANQUANT | Seulement un compteur au Tableau de bord et les lignes rouges de la liste ; aucun écran « Résultats critiques » au menu, aucune consultation des appels passés |
| Appeler un critique dès la saisie (technicien) | Saisie des résultats | — | MANQUANT | Bouton seulement dans Validation biologique ; le rôle Technicien n'a pas le droit |
| Antériorités | Validation biologique | **Antériorités**, double-clic **Antériorité** | OUI | ValidationView.cs:314-323 → 637 |
| Éditer le compte rendu | Validation biologique | **Compte rendu** | PARTIEL | ValidationView.cs:301 → 917 : ni conclusion du biologiste, ni choix de la langue à la première édition (seulement en rectification) |
| Dévalider avant impression | Validation biologique | **Dévalider** | PARTIEL | ValidationView.cs:302 → 715. Droit absent du rôle Biologiste par défaut (seul le Biologiste responsable l'a) |

## Comptes rendus

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Rechercher, filtrer, période | Comptes rendus | recherche + Entrée, **Uniquement les comptes rendus non remis**, période, **Actualiser** | OUI | ReportRegisterView.cs:241-250 |
| Émettre un compte rendu en attente | Comptes rendus | **Émettre le compte rendu**, double-clic dans À émettre | OUI | ReportRegisterView.cs:253-254 → 653 |
| Réimprimer l'original archivé | Comptes rendus | **Réimprimer l'original**, double-clic registre | PARTIEL | ReportRegisterView.cs:258-260 → 625. Droit absent des rôles Secrétariat / accueil et Biologiste : le guichet ne peut pas réimprimer un compte rendu perdu |
| Enregistrer une remise (guichet, courriel, SMS, fax, coursier, interface) | Comptes rendus | **Enregistrer une remise** → **Mode de remise**, **Qualité du destinataire**, **Destinataire**, **Adresse / numéro**, **Porteur**, **CIN du porteur** | OUI | ReportRegisterView.cs:261 → 696 ; ReportRegisterDialogs.cs:186 |
| Remise à un tiers avec consentement | Comptes rendus | **Référence du consentement** | PARTIEL | ReportRegisterDialogs.cs:208 : exige l'identifiant interne du consentement (GUID) tapé à la main ; aucune liste des consentements du patient. Inutilisable en pratique au guichet |
| Marquer un SMS / fax / coursier comme finalement remis, annuler une remise | Comptes rendus | — | MANQUANT | Ces remises restent « Envoyé » ; aucun bouton pour les confirmer ou les annuler |
| Renvoyer après un échec d'envoi | Comptes rendus | **Enregistrer une remise** | OUI | Nouvelle tentative |
| Rectification (nature, motif FR/AR, conclusion, langue) | Comptes rendus | **Émettre une rectification** | PARTIEL | ReportRegisterView.cs:262 → 758 ; ReportRegisterDialogs.cs:333. Une « Correction de résultat » ne peut corriger aucune valeur déjà rendue (voir rectification de valeur MANQUANTE) : elle réimprime les mêmes valeurs. Le « Retrait du compte rendu » réimprime lui aussi les résultats (à vérifier) |
| Liste des remises d'une version | Comptes rendus | **Remises enregistrées** | OUI | ReportRegisterView.cs:541 |

## Boutons présents mais sans effet utile

- Double-clic sur un dossier dans Validation biologique : ne fait que déplacer le curseur (ValidationView.cs:330).
- Bouton **Compte rendu** de Validation biologique : actif même quand rien n'est validé ; il est alors refusé (« Aucun résultat validé : le compte rendu ne peut être édité. »).
- Fenêtre « Analyses en cascade » : informe sans proposer l'ajout, y compris pour les règles réglées en ajout automatique.
- Message « toute modification doit passer par une rectification motivée » : aucune action de rectification de valeur n'existe.

## Manques à corriger (par impact sur le travail quotidien)

1. **Rectifier une valeur déjà validée ou rendue** : aucun écran ne permet de corriger une valeur imprimée ; la rectification de compte rendu ne peut donc pas corriger un résultat.
2. **Accepter les transmissions retenues des automates** : réservé au Biologiste responsable / Administrateur alors que l'acceptation automatique est désactivée par défaut : la paillasse est bloquée.
3. **« Non fait » et effacement d'une valeur** impossibles depuis la saisie.
4. **Alerte critique et cascade pour les valeurs d'automate** absentes (la majorité des résultats).
5. **Réimpression interdite au Secrétariat** (et au Biologiste) par défaut ; **Dévalider** absent du rôle Biologiste.
6. **Résultats critiques** : pas de liste/historique des appels, pas de note, de mode, de fonction ni de numéro ; relecture non comparée ; pas de refus ni d'escalade.
7. **Analyses en cascade** : pas d'ajout (automatique ou en un clic) au dossier.
8. **Remise à un tiers** : référence de consentement à taper sous forme d'identifiant interne.
9. Validation technique par ligne ; filtre par secteur en saisie et en validation ; commentaire du biologiste par résultat ; conclusion et langue dès la première édition.
10. Liste déroulante des réponses codées ; commentaire technique non imprimé ; flèches d'interprétation masquées par les autres signalements ; dilution non saisissable ; confirmation des remises SMS/fax/coursier.
