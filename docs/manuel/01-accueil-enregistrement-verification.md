# Vérification de l'interface — Accueil / Enregistrement

Re-vérifié le 2026-10-17 au commit 4e20d0d : chaque ligne a été recontrôlée dans le code des écrans et des fonctions ci-dessous, et les libellés comparés à `fr.json`. Les lignes corrigées passent à **Oui** avec la nouvelle référence ; des lignes ont été ajoutées pour les nouveautés (nouvelle présentation de **Patients**, devis en direct, avertissement de répétition, **Enregistrer sans étiquettes**, analyses fréquentes, copie du n° de dossier, onglet **Historique**, **Plus d'actions**).

Fichiers d'écran contrôlés : `Views/PatientSearchView.cs`, `Forms/PatientEditorForm.cs`, `Forms/PatientEditorDialogs.cs`, `Forms/PatientMergeDialog.cs`, `Forms/ProvisionalPatientDialog.cs`, `Composition/PatientEditorFactory.cs`, `Views/OrderRegistrationView.cs`, `Views/MasterDataEditors.cs` (fenêtre prescripteur), `Views/OrderDetailView.cs`, `Views/OrderDetailDialogs.cs`.
Fonctions de l'application contrôlées : recherche patient (`PatientQueries`), enregistrement patient (`PatientRegistrationService`), enregistrement de dossier (`OrderRegistrationService` / `RegisterOrderRequest`, `OrderRegistrationRules`), devis (`IOrderQuotes`), modification de dossier (`OrderAmendmentService`), réimpression d'étiquettes (`SampleWorkflowService.ReprintLabelsAsync`), compte rendu archivé (`ReportReleaseService`), création de prescripteur (`MasterDataService.SavePrescriberAsync`), droits par défaut (`ReferenceDataSeeder`).

## Patients

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Rechercher un patient (nom, CIN, n° fiche, mobile) | Patients | zone de recherche | Oui | PatientSearchView.cs:89 (recherche différée), SearchAsync :907 ; recherche dès 3 caractères :935. Toujours limité aux 50 premiers résultats (PatientQueries.cs:190-191), sans page suivante. |
| Filtrer la liste | Patients | filtres **Tous**, **Identité à compléter**, **Vus aujourd'hui** ; liste **Tous les payeurs** | Oui | Nouveau. PatientSearchView.cs:672-673 ; payeurs :655 |
| Voir la fiche et les demandes du patient sélectionné | Patients | carte latérale, cartes de demandes | Oui | Nouveau. Un clic sur une demande ouvre **Consultation d'un dossier** (PatientSearchView.cs:1136) ; remplace la liste « Antécédents d'analyses » |
| Nouvelle demande pour le patient choisi | Patients | **Nouvelle demande pour ce patient** | Oui | Nouveau. NewOrderAsync PatientSearchView.cs:766-769 ; grisé pour une fiche anonymisée (:721-737) |
| Créer une fiche | Patients / Dossiers | **Nouveau patient** (Ctrl+N) → **Enregistrer** | Oui | PatientSearchView.cs:118-123, Ctrl+N :706-709 ; OrderRegistrationView.cs:956 ; PatientEditorForm.cs RegisterAsync :1440 |
| Détection de doublon avant enregistrement | Fiche patient | message sous la fiche | Oui | PatientEditorForm.cs:669-670 → FindDuplicatesAsync :938 |
| Décision sur doublon | Doublon possible | **Utiliser ce dossier** / **Créer un nouveau dossier** + **Motif : pourquoi…** | Oui | PatientEditorDialogs.cs:663-669 |
| Contrôle du taux de couverture fractionnaire | Fiche patient | question de confirmation | Oui | Nouveau. PatientEditorDialogs.cs:289 (ConfirmFractionRate) |
| Contrôle poids / taille | Fiche patient | message | Oui | PatientEditorForm.cs:1375-1376 |
| Modifier l'identité | Patients | **Modifier** (Entrée, double-clic) → **Enregistrer** | Oui | PatientSearchView.cs:118-123, double-clic :136, Entrée :706-709 ; PatientEditorForm.cs UpdateAsync :1391. Ouvre en lecture seule sans « modifier un patient » : correct. |
| Patient non identifié | Dossiers | **Patient non identifié (urgence)** → **Ouvrir le dossier provisoire** | Oui | OrderRegistrationView.cs:957 → RegisterEmergencyPatientAsync :1586 ; PatientEditorFactory.cs:43. Toujours absent de l'écran **Patients** (un Préleveur, qui a « créer un patient » mais pas l'écran Dossiers, ne peut pas ouvrir de fiche X-URG). |
| Compléter une identité provisoire | Patients | filtre **Identité à compléter**, **Modifier** | Oui | Corrigé : le filtre retrouve les fiches provisoires (PatientSearchView.cs:672-673) ; UpdateAsync renomme la fiche X-URG. |
| Ajouter une couverture | Fiche patient, onglet **Couvertures** | **Ajouter une couverture** | Oui | PatientEditorForm.cs:672 → AddCoverageAsync :1082 |
| Clore une couverture | onglet **Couvertures** | **Clore la couverture** | Oui | Corrigé : PatientEditorForm.cs:673 → EndCoverageAsync :1108 ; une date illisible affiche maintenant un message propre (Coverage_EndDateUnreadable :1122). Date toujours tapée en texte. |
| Corriger une couverture (n° d'immatriculation erroné, taux) | onglet **Couvertures** | — | MANQUANT | Toujours : aucune fonction de modification d'une couverture : il faut la clore et en créer une autre. |
| Recueillir un consentement | onglet **Consentements** | **Recueillir un consentement** | PARTIEL | PatientEditorForm.cs:674 → AddConsentAsync :1200 ; consentement écrit PatientEditorDialogs.cs:504, :516. Toujours **aucun champ pour joindre la pièce** (formulaire signé) : tout consentement écrit reste « Pièce absente » (EvidenceMissing PatientEditorForm.cs:1154). |
| Retirer un consentement | onglet **Consentements** | **Retirer le consentement** | Oui | PatientEditorForm.cs:675 → WithdrawConsentAsync :1221 |
| Ajouter une alerte | onglet **Alertes** | **Ajouter une alerte** | Oui | PatientEditorForm.cs:677 → AddAlertAsync :1283 ; confirmation d'une alerte critique PatientEditorDialogs.cs:926 |
| Lever / clore une alerte avant son expiration | onglet **Alertes** | — | MANQUANT | Toujours : aucune fonction ni bouton. |
| Acquitter une alerte critique (blocage annoncé) | Dossiers | — | MANQUANT | Toujours : le texte d'aide « Une alerte critique bloque l'enregistrement jusqu'à ce qu'elle soit acquittée » (AlertCriticalNotice, PatientEditorDialogs.cs:907) est faux ; OrderRegistrationView.cs:1464 affiche seulement une boîte « Alertes » sans le texte de l'alerte. |
| Fusionner deux fiches | Patients | **Plus d'actions** › **Fusionner les dossiers** | Oui | PatientSearchView.cs MergeAsync :779 (au moins deux fiches dans la liste, MergeNeedsTwoFiles :788 ; interdit sur une fiche anonymisée :721-737) ; PatientMergeDialog.cs:141. Les demandes du doublon sont facturées au nom de la fiche gardée. |
| Anonymiser (loi 08-09) | Patients | **Plus d'actions** › **Anonymiser** | Oui | PatientSearchView.cs AnonymizeAsync :818, dérogation conservation :844. Droit réservé à l'Administrateur par défaut : à confirmer avec le laboratoire. |
| Exporter les données d'un patient (droit d'accès loi 08-09) | — | — | MANQUANT | Toujours : le droit « patients.export » existe et est donné au Biologiste responsable, mais aucune fonction ni bouton ne l'utilise. |

## Dossiers (enregistrement)

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Choisir le patient | Nouvelle demande | zone de recherche + suggestions, lien **Voir**, **Changer de patient** | Oui | SearchPatientsAsync OrderRegistrationView.cs:1412, PickPatient :1452, :958-959 ; dernière demande :1546 |
| Rappel des demandes récentes / répétition sous 30 jours | Nouvelle demande | avertissement sur la ligne, **Conserver** / **Retirer** | Oui | Nouveau. LoadRecentAnalysesAsync :1527-1532 (fenêtre de 30 jours, OrderRegistrationRules.cs:16) ; avertissements :2273-2274 |
| Priorité | Nouvelle demande | **Priorité** | PARTIEL | Toujours : la liste n'est pas filtrée selon le droit « dossier urgent » (OrderRegistrationView.cs:1593) : un Biologiste sans ce droit voit son Urgent refusé à l'enregistrement. |
| Origine (dont **Sous-traitance reçue**) | Nouvelle demande | **Origine** | PARTIEL | Toujours : simple étiquette, aucun champ pour le laboratoire adresseur ni sa référence. |
| Prescripteur de l'annuaire ou texte libre | Nouvelle demande | **Prescripteur** | Oui | Contrôle prescripteur obligatoire :2481 |
| Créer un prescripteur | Nouvelle demande | **+** → **Nouveau prescripteur** → **Enregistrer** | PARTIEL | :966-967 → CreatePrescriberAsync :1334. Toujours : la liste du type porte l'étiquette « Désignation » (MasterDataEditors.cs:1291) ; la **Remise** du prescripteur n'est appliquée nulle part (à vérifier avec le module Relations). |
| Ajouter analyses / profils | Nouvelle demande | zone de recherche du catalogue, filtres du catalogue, **Profils fréquents** | Oui | AddFromSearchAsync :1625, filtres :1273-1298, profils fréquents :1133, :1236, :1254 |
| Retirer une analyse demandée | Nouvelle demande | **✕** sur la ligne, **Tout retirer** | Oui | Corrigé : bouton visible (OnBasketMouseClick :2325, RemoveBasketRow :1845) ; **Tout retirer** :1060, RemoveAll :1867 |
| Voir les tubes à prélever | Nouvelle demande | **Tubes à prélever** | Oui | Nouveau. LayoutTubes :861 |
| Renseignements cliniques | Nouvelle demande | **Renseignements cliniques** | Oui | |
| Jeûne | Nouvelle demande | **Patient à jeun** : **Oui** / **Non** / **Non renseigné** | PARTIEL | Nouveau choix à trois valeurs (:1126, SetFasting :1382, défaut :145). Toujours : le nombre d'heures n'est saisissable qu'au Prélèvement. |
| Paiement direct (sans couverture) | Nouvelle demande | **Paiement direct** | Oui | |
| Devis / estimation avant enregistrement | Nouvelle demande | devis en direct (part organisme / part patient) | Oui | Corrigé : le devis se recalcule à chaque changement (RequestQuote :1945, RefreshQuoteAsync :1957, ShowQuote :2002). Pas de devis imprimable à remettre au patient. |
| Savoir ce qui manque avant d'enregistrer | Nouvelle demande | barre d'état de la saisie | Oui | Nouveau. RefreshReadiness :2049-2088 (boutons actifs :2053-2054) |
| Choisir la couverture à utiliser (si plusieurs) | Nouvelle demande | — | MANQUANT | Toujours : l'écran utilise la couverture principale. |
| Part AMO + mutuelle complémentaire | Nouvelle demande | — | MANQUANT | Toujours : un seul organisme par dossier. |
| Remise sur le dossier | Nouvelle demande | — | MANQUANT (ici) | Toujours ; la remise existe à la facturation. |
| Date de l'ordonnance | Nouvelle demande | — | MANQUANT (ici) | Toujours : seulement après coup via **Modifier l'en-tête**. |
| Ordonnance scannée | Nouvelle demande | — | MANQUANT | Toujours : aucun bouton pour joindre le scan. |
| Code diagnostic, Service / unité, Chambre / lit, Note interne, Langue du compte rendu | Nouvelle demande | — | MANQUANT (ici) | Toujours : seulement via **Modifier l'en-tête**. |
| Enregistrer | Nouvelle demande | **Enregistrer** (F12, F4) | Oui | :1063-1064, touches :1081-1091 → RegisterAsync :2469 ; avertissements :2519 |
| Enregistrer sans imprimer les étiquettes | Nouvelle demande | **Enregistrer sans étiquettes** (F8) | Oui | Nouveau. :1064, RegisterAsync(printLabels: false) :2530 |
| Récapitulatif part organisme / part patient | Nouvelle demande | fenêtre de récapitulatif | Oui | ShowPricing :2549, encadré :2582. Le devis en direct annonce désormais les mêmes montants. |
| Étiquettes à l'enregistrement | Nouvelle demande | (automatique avec **Enregistrer**) | Oui | :2524 |
| Abandonner la saisie | Nouvelle demande | **Annuler la saisie** (Échap) | Oui | Remplace **Effacer**. CancelEntry :2618, confirmation ConfirmEntryLoss :2615 |

## Consultation d'un dossier

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste et recherche (n°, code-barres, nom, CIN) | Consultation | zone de recherche, filtres | Oui | Filtres OrderDetailView.cs:1040 ; en-têtes :1070-1091 ; aucun résultat :1358. 200 dossiers récents (OrderAmendmentService.cs:705) ; au-delà, chercher. |
| Copier le n° de dossier | Consultation | double-clic sur le n° | Oui | Nouveau. OrderDetailView.cs:461, :976 |
| Ajouter des analyses | Consultation | **Ajouter des analyses** | Oui | :983 ; grisé sur un dossier facturé ou rendu (RefreshActions :2039) |
| Annuler une analyse | Consultation | **Plus d'actions** › **Annuler l'analyse**, clic droit | Oui | :996, :1001, menu clic droit :1003-1010 |
| Repasser une analyse | Consultation | **Plus d'actions** › **Repasser l'analyse**, clic droit | PARTIEL | :997, :1002. Corrigé en partie : grisé tant qu'aucune valeur n'est saisie (OrderAmendmentService.cs:118). Reste : actif sans le droit « modifier un résultat » sur une analyse validée techniquement ; refus après saisie du motif. |
| Corriger l'en-tête | Consultation | **Modifier l'en-tête** | PARTIEL | :986 ; OrderDetailDialogs.cs:339-341. Toujours : **un prescripteur de l'annuaire ne peut pas être remplacé** (lecture seule, OrderDetailDialogs.cs:406). |
| Annuler le dossier | Consultation | **Plus d'actions** › **Annuler le dossier** | Oui | :998 |
| Réimprimer les étiquettes | Consultation | **Imprimer les étiquettes** (F8) | PARTIEL | :984, F8 :1027. Toujours : toutes les étiquettes du dossier, impossible d'en choisir une. |
| Ouvrir le compte rendu PDF | Consultation | **Compte rendu (PDF)** | Oui | Corrigé : droit donné au Secrétariat et au Biologiste (ReferenceDataSeeder.cs:574, :642) ; :985, :2052 |
| Historique des modifications du dossier | Consultation | onglet **Historique** | Oui | Corrigé. OrderDetailView.cs:1687, :1865 |
| Encaisser / voir le règlement depuis le dossier | Consultation | onglet **Facturation**, **Ouvrir la facturation** | PARTIEL | Corrigé en partie : le bouton ouvre l'écran **Facturation** (:1014), mais sans sélectionner la facture du dossier. |
| Modifier la couverture d'un dossier | Consultation | — | Volontairement absent | Rappel affiché dans **Modifier l'en-tête** (avoir puis nouvelle facture). |

## Boutons qui existent mais n'aboutissent pas ou induisent en erreur

- Message d'alerte critique à l'enregistrement : boîte titrée « Patient » contenant seulement le mot « Alertes » (OrderRegistrationView.cs:1464) — ne dit pas quelle alerte.
- Texte d'aide « Une alerte critique bloque l'enregistrement jusqu'à ce qu'elle soit acquittée » (fenêtre **Ajouter une alerte**) : faux, rien ne bloque.
- État « Pièce absente » des consentements écrits : impossible à corriger depuis l'interface.
- Champ type de prescripteur étiqueté « Désignation ».

Corrigés depuis la vérification précédente : message de date de **Clore la couverture**, bouton visible pour retirer une analyse, devis en direct, **Compte rendu (PDF)** pour Secrétariat et Biologiste, onglet **Historique**, lien vers la facturation, filtre des fiches provisoires.

## Manques à corriger (par impact sur le travail quotidien)

1. **Modifier l'en-tête** ne permet pas de changer un prescripteur de l'annuaire (erreur de saisie fréquente, rejets AMO).
2. Alertes patient : pas de blocage ni d'affichage du texte à l'enregistrement, pas de moyen de clore une alerte ; texte d'aide faux.
3. Consentement écrit : pas de pièce jointe possible → « Pièce absente » permanent sur génétique / VIH / prénatal.
4. Choix de la couverture du dossier impossible (toujours la principale) ; pas de part complémentaire mutuelle à l'enregistrement.
5. Champs absents à l'enregistrement : date de l'ordonnance, ordonnance scannée, service / chambre, code diagnostic, langue du compte rendu, heures de jeûne.
6. **Sous-traitance reçue** : pas de laboratoire adresseur ni de référence externe sur le dossier.
7. **Priorité** non filtrée selon le droit « dossier urgent » (refus en fin de saisie).
8. Couvertures non modifiables (clore puis recréer).
9. **Ouvrir la facturation** n'ouvre pas la facture du dossier ; réimpression d'étiquettes tout ou rien ; **Repasser l'analyse** actif sans le droit de modification.
10. Points secondaires : remise prescripteur non appliquée (à vérifier), libellé « Désignation » du type de prescripteur, recherche patient limitée à 50 résultats, export patient (droit sans fonction), fiche X-URG impossible depuis **Patients**.
