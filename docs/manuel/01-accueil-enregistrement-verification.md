# Vérification de l'interface — Accueil / Enregistrement

Fichiers d'écran contrôlés : `Views/PatientSearchView.cs`, `Forms/PatientEditorForm.cs`, `Forms/PatientEditorDialogs.cs`, `Forms/PatientMergeDialog.cs`, `Forms/ProvisionalPatientDialog.cs`, `Composition/PatientEditorFactory.cs`, `Views/OrderRegistrationView.cs`, `Views/MasterDataEditors.cs` (fenêtre prescripteur), `Views/OrderDetailView.cs`, `Views/OrderDetailDialogs.cs`.
Fonctions de l'application contrôlées : enregistrement patient (`PatientRegistrationService`), enregistrement de dossier (`OrderRegistrationService` / `RegisterOrderRequest`), modification de dossier (`OrderAmendmentService`), réimpression d'étiquettes (`SampleWorkflowService.ReprintLabelsAsync`), compte rendu archivé (`ReportReleaseService`), création de prescripteur (`MasterDataService.SavePrescriberAsync`).

## Patients

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Rechercher un patient (nom, CIN, n° fiche, mobile) | Patients | **Rechercher…** | Oui | PatientSearchView.cs:89 (recherche différée 300 ms), :413. Limité aux 50 premiers résultats, pas de pagination. |
| Voir les antécédents et ouvrir un dossier | Patients | **Antécédents d'analyses** (double-clic) | Oui | PatientSearchView.cs:100 |
| Créer une fiche | Patients / Dossiers | **Nouveau patient** → **Enregistrer** | Oui | PatientSearchView.cs:71 ; OrderRegistrationView.cs:366 ; PatientEditorForm.cs:646 → :1377 (RegisterAsync) |
| Détection de doublon avant enregistrement | Fiche patient | message sous la fiche | Oui | PatientEditorForm.cs:633-634 → :891 (FindDuplicatesAsync) |
| Décision sur doublon | Doublon possible | **Utiliser ce dossier** / **Créer un nouveau dossier** + **Motif : pourquoi…** | Oui | PatientEditorDialogs.cs:656, :665 |
| Modifier l'identité | Patients | **Modifier** → **Enregistrer** | Oui | PatientSearchView.cs:72 ; PatientEditorForm.cs:1330 (UpdateAsync). Bouton **Modifier** non soumis à droit (ouvre en lecture seule, Enregistrer grisé sans « modifier un patient ») : correct. |
| Patient non identifié | Dossiers | **Patient X urgence** → **Ouvrir le dossier provisoire** | Oui | OrderRegistrationView.cs:367 → :608 ; PatientEditorFactory.cs:43. Absent de l'écran Patients (un Préleveur, qui a « créer un patient » mais pas l'écran Dossiers, ne peut pas ouvrir de fiche X-URG). |
| Compléter une identité provisoire | Patients | **Modifier** | Oui | Géré par UpdateAsync (renommer X-URG). Aucun filtre « fiches provisoires » : on les retrouve en tapant « X-URG ». |
| Ajouter une couverture | Fiche patient, onglet **Couvertures** | **Ajouter une couverture** | Oui | PatientEditorForm.cs:636 → :1040 |
| Clore une couverture | onglet **Couvertures** | **Clore la couverture** | Partiel | PatientEditorForm.cs:637 → :1066. La date est tapée en texte libre ; une date illisible affiche « … n'est pas une valeur numérique valide » (message inadapté). Pas de sélecteur de date. |
| Corriger une couverture (n° d'immatriculation erroné, taux) | onglet **Couvertures** | — | MANQUANT | Aucune fonction de modification d'une couverture : il faut la clore et en créer une autre. |
| Recueillir un consentement | onglet **Consentements** | **Recueillir un consentement** | Partiel | PatientEditorForm.cs:638 → :1157. La fonction accepte la pièce justificative (formulaire signé scanné) mais **aucun champ ne permet de la joindre** : tout consentement écrit (génétique, VIH, prénatal…) reste affiché « Pièce absente ». |
| Retirer un consentement | onglet **Consentements** | **Retirer le consentement** | Oui | PatientEditorForm.cs:639 → :1178 |
| Ajouter une alerte | onglet **Alertes** | **Ajouter une alerte** | Oui | PatientEditorForm.cs:641 → :1240 |
| Lever / clore une alerte avant son expiration | onglet **Alertes** | — | MANQUANT | Aucune fonction ni bouton : une alerte permanente ne peut plus être retirée. |
| Acquitter une alerte critique (blocage annoncé) | Dossiers | — | MANQUANT | Le texte d'aide dit « Une alerte critique bloque l'enregistrement jusqu'à ce qu'elle soit acquittée ». Le code ne bloque rien : OrderRegistrationView.cs:675-679 affiche seulement une boîte « Alertes » sans le texte de l'alerte. Les alertes Information / Avertissement ne sont affichées nulle part hors de la fiche. |
| Fusionner deux fiches | Patients | **Fusionner les dossiers** | Oui | PatientSearchView.cs:73 → :311 ; PatientMergeDialog.cs:124. Les deux fiches doivent figurer dans la même recherche (max. 50 résultats). |
| Anonymiser (loi 08-09) | Patients | **Anonymiser** | Oui | PatientSearchView.cs:74 → :346, dérogation conservation :369-377. Droit réservé à l'Administrateur par défaut (pas au Biologiste responsable) : à confirmer avec le laboratoire. |
| Exporter les données d'un patient (droit d'accès loi 08-09) | — | — | MANQUANT | Le droit « patients.export » existe et est donné au Biologiste responsable, mais aucune fonction ni bouton ne l'utilise. |

## Dossiers (enregistrement)

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Choisir le patient | Dossiers | zone de recherche + liste déroulante | Oui | OrderRegistrationView.cs:364-365, :549 |
| Priorité | Dossiers | **Priorité** | Partiel | :963. Le rôle Biologiste a « créer un dossier » mais pas « dossier urgent » : sa saisie Urgent est refusée après coup ; la liste n'est pas filtrée selon le droit. |
| Origine (dont **Sous-traitance reçue**) | Dossiers | **Origine** | Partiel | :945. Simple étiquette : aucun champ pour le laboratoire adresseur ni sa référence ; le payeur reste la couverture principale de la fiche patient (il faut une couverture « Laboratoire adresseur » sur la fiche pour le facturer). |
| Prescripteur de l'annuaire ou texte libre | Dossiers | **Prescripteur** | Oui | :538, :966-967 ; contrôle prescripteur obligatoire :951 |
| Créer un prescripteur | Dossiers | **+** → **Nouveau prescripteur** → **Enregistrer** | Partiel | :374 → :505 ; MasterDataEditors.cs:1094. La liste du type de prescripteur porte l'étiquette « Désignation » (clé Field.Name) au lieu d'un libellé « Type ». Le champ **Remise** du prescripteur est enregistré mais n'est appliqué nulle part au prix d'un dossier (à vérifier avec le module Relations). |
| Ajouter analyses / profils (clavier) | Dossiers | **Analyses / Profils** + Entrée, flèche bas, double-clic | Oui | :380-403, :690, :787 |
| Retirer une analyse demandée | Dossiers | double-clic ou **Suppr** dans **Analyses demandées** | Oui | :405-410. Aucun bouton visible « Retirer » (geste non signalé à l'écran). |
| Renseignements cliniques | Dossiers | **Renseignements cliniques** | Oui | :968 |
| Jeûne | Dossiers | **Patient à jeun** | Partiel | :970. Oui/non seulement ; le nombre d'heures de jeûne (prévu par la fonction) n'est pas saisissable ici (il l'est au Prélèvement, CollectionView.cs:733). |
| Paiement direct (sans couverture) | Dossiers | **Paiement direct** | Oui | :971 |
| Choisir la couverture à utiliser (si plusieurs) | Dossiers | — | MANQUANT | La fonction accepte une couverture précise ; l'écran utilise toujours la couverture principale. Impossible de choisir la mutuelle plutôt que l'AMO pour ce dossier. |
| Part AMO + mutuelle complémentaire | Dossiers | — | MANQUANT | Un seul organisme par dossier dans le calcul ; aucune répartition AMO puis mutuelle à l'enregistrement (à vérifier en Facturation). |
| Remise sur le dossier | Dossiers | — | MANQUANT (ici) | La fonction accepte remise + motif ; pas de champ. La remise existe à la facturation (BillingView.cs:634), ce qui peut suffire. |
| Date de l'ordonnance | Dossiers | — | MANQUANT (ici) | Prévue par la fonction ; saisissable seulement après coup via **Modifier l'en-tête**. Utile pour les demandes AMO. |
| Ordonnance scannée | Dossiers | — | MANQUANT | La fonction accepte le scan de l'ordonnance ; aucun bouton pour le joindre, nulle part. |
| Code diagnostic, Service / unité, Chambre / lit, Note interne, Langue du compte rendu | Dossiers | — | MANQUANT (ici) | Prévus par la fonction ; seulement via **Modifier l'en-tête** après enregistrement. Gênant pour l'origine **Clinique / hôpital**. |
| Enregistrer | Dossiers | **Enregistrer** (Ctrl+S) | Oui | :413 → :941 |
| Récapitulatif part organisme / part patient | Dossiers | fenêtre **Facture** | Oui | :1019. Le prix montré dans le catalogue est celui du laboratoire, sans tenir compte du payeur : l'écart avec la fenêtre finale peut surprendre. |
| Étiquettes à l'enregistrement | Dossiers | (automatique) | Oui | :1059 |
| Devis / estimation avant enregistrement | Dossiers | — | MANQUANT | Aucun devis patient. Le total du bas est indicatif (montant brut seulement, sans part patient). Les « Demandes de devis » de l'application concernent les fournisseurs. |
| Effacer la saisie | Dossiers | **Effacer** | Oui | :414 |

## Consultation d'un dossier

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste et recherche (n°, code-barres, nom, CIN) | Consultation | zone de recherche, **Actualiser** | Oui | OrderDetailView.cs:393-426, :667. 200 dossiers récents ; au-delà, chercher. |
| Ajouter des analyses | Consultation | **Ajouter des analyses** | Oui | :428 → :950 ; OrderDetailDialogs.cs:266 |
| Annuler une analyse | Consultation | **Annuler l'analyse** | Oui | :429 → :984 |
| Repasser une analyse | Consultation | **Repasser l'analyse** | Oui | :430 → :1008. Bouton actif pour qui a « saisir des résultats » même si l'analyse est validée techniquement et que l'utilisateur n'a pas « modifier un résultat » : refus après saisie du motif. |
| Corriger l'en-tête | Consultation | **Modifier l'en-tête** | Partiel | :431 → :1028 ; OrderDetailDialogs.cs:300. **Un prescripteur choisi dans l'annuaire ne peut pas être remplacé** (champ en lecture seule, l'identifiant est renvoyé tel quel, OrderDetailDialogs.cs:326). Or le cas le plus fréquent est justement « mauvais médecin sélectionné ». Un prescripteur libre ne peut pas non plus être rattaché à l'annuaire. |
| Annuler le dossier | Consultation | **Annuler le dossier** | Oui | :432 → :1046 |
| Réimprimer les étiquettes | Consultation | **Imprimer les étiquettes** | Partiel | :433 → :1085. Réimprime toutes les étiquettes du dossier ; impossible de n'en choisir qu'une. |
| Ouvrir le compte rendu PDF | Consultation | **Compte rendu (PDF)** | Partiel | :434 → :1111. Droit « réimprimer un compte rendu validé » absent des rôles Secrétariat / accueil et Biologiste par défaut : le bouton est grisé pour la personne qui remet les résultats. |
| Historique des modifications du dossier | Consultation | — | MANQUANT | Les motifs sont au journal d'audit, mais l'écran n'a pas de bouton « Historique » ; seule la dernière annulation / reprise apparaît dans les colonnes. Il faut passer par **Journal d'audit** (administration). |
| Encaisser / voir le règlement depuis le dossier | Consultation | — | MANQUANT (ici) | Le n° de facture est affiché, sans lien vers la caisse ou la facture. |
| Modifier la couverture d'un dossier | Consultation | — | Volontairement absent | Rappel affiché dans **Modifier l'en-tête** (avoir puis nouvelle facture). |

## Boutons qui existent mais n'aboutissent pas ou induisent en erreur

- Message d'alerte critique à l'enregistrement : boîte titrée « Patient » contenant seulement le mot « Alertes » (OrderRegistrationView.cs:678) — ne dit pas quelle alerte.
- Texte d'aide « Une alerte critique bloque l'enregistrement jusqu'à ce qu'elle soit acquittée » (fenêtre **Ajouter une alerte**) : faux, rien ne bloque.
- État « Pièce absente » des consentements écrits : impossible à corriger depuis l'interface.
- **Clore la couverture** : message d'erreur de date inadapté.
- Champ type de prescripteur étiqueté « Désignation ».

## Manques à corriger (par impact sur le travail quotidien)

1. **Modifier l'en-tête** ne permet pas de changer un prescripteur de l'annuaire (erreur de saisie fréquente, rejets AMO).
2. **Compte rendu (PDF)** grisé pour Secrétariat / accueil et Biologiste (droit « réimprimer un compte rendu validé » manquant dans les rôles par défaut).
3. Alertes patient : pas de blocage ni d'affichage du texte à l'enregistrement, pas de moyen de clore une alerte.
4. Consentement écrit : pas de pièce jointe possible → « Pièce absente » permanent sur génétique / VIH / prénatal.
5. Choix de la couverture du dossier impossible (toujours la principale) ; pas de part complémentaire mutuelle à l'enregistrement.
6. Champs absents à l'enregistrement : date de l'ordonnance, ordonnance scannée, service / chambre, code diagnostic, langue du compte rendu, heures de jeûne (seulement après coup ou au prélèvement).
7. **Sous-traitance reçue** : pas de laboratoire adresseur ni de référence externe sur le dossier.
8. Rôle Biologiste : « créer un dossier » sans « dossier urgent » ni « créer un patient » — refus en fin de saisie.
9. Pas d'historique des modifications sur l'écran **Consultation d'un dossier**.
10. Couvertures non modifiables (clore puis recréer) ; date de fin en texte libre.
11. Pas de devis patient ; total du bas en montant brut seulement.
12. Points secondaires : remise prescripteur non appliquée (à vérifier), libellé « Désignation » du type de prescripteur, recherche patient limitée à 50 résultats sans pagination, export patient (droit sans fonction).
