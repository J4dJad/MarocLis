# Vérification de l'interface — Pilotage, statistiques et relations

Légende : **Oui** = le bouton ou le champ existe et déclenche la fonction ; **PARTIEL** = il existe, mais il manque quelque chose ou il est mal ciblé ; **MANQUANT** = la fonction existe dans l'application, mais aucun écran ne permet de la déclencher.
Les références « fichier:ligne » renvoient au code de l'écran (src/MarocLIS.Desktop/Views) ou des fonctions (src/MarocLIS.Application). Revérifié le 2026-10-17 sur le commit 4e20d0d. Changements : la barre de période est faite de pastilles (et non plus d'une liste), **Calculer** porte le raccourci F5, et les actions rares sont passées dans « Plus d'actions ».

## Barre de période commune

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Choisir une période prédéfinie / libre | tous | pastilles **Ce mois-ci** … **Période libre**, **Du**, **Au** | Oui | RelationsAndSteeringShared.cs:83-84 (pastilles), 107-108 (date → Période libre), 158-185 |
| Calculer (F5) | tous | **Calculer** | Oui | RelationsAndSteeringShared.cs:69-71 |
| Exporter un onglet vide | tous sauf Statistiques | **Exporter (CSV)** | Oui | RelationsAndSteeringShared.cs:224-231 : « Aucune donnée » (corrigé ; l'ancien message « Aucun élément sélectionné. » égarait) |

## Statistiques

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Calculer les statistiques d'activité | Statistiques | **Calculer** | Oui | StatisticsView.cs:117 → LoadAsync l.163 ; calcul aussi à l'ouverture |
| Consulter par secteur / analyse / payeur / jour | Statistiques | onglets **Par secteur**, **Par analyse**, **Par payeur**, **Par jour** | Oui | StatisticsView.cs:70, 126-129 |
| Exporter l'onglet | Statistiques | **Exporter (CSV)** | PARTIEL | l.118 → ExportAsync l.252. Le bouton est grisé tant qu'aucun calcul n'a abouti (l.204, 245) ; sur un onglet vide, il ne se passe toujours rien (l.255, aucun message). |
| Statistiques multi-sites | Statistiques | — | Normal | Site de connexion uniquement ; le multi-sites est dans Pilotage. |
| Déclaration épidémiologique | — | — | **MANQUANT** | Le droit « statistics.declaration » est attribué au Biologiste responsable, mais il n'a ni écran ni fonction. |

## Pilotage

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Calculer le tableau de pilotage | Pilotage | barre de période, **Calculer** | Oui | SteeringView.cs:71 → LoadAsync l.99 ; à l'ouverture |
| Masse salariale réservée au droit salaires | Pilotage | tuile **Masse salariale / CA** | Oui | SteeringView.cs:146 ; SteeringService.CanSeePayroll |
| Revenus par jour / site / payeur / prescripteur | Pilotage | onglets **Par jour**, **Par site**, **Par payeur**, **Par prescripteur** | Oui | SteeringView.cs:64, 83-86 |
| Balance âgée des créances | Pilotage | onglet **Créances échues** | Oui | SteeringView.cs:87 |
| Indicateurs qualité | Pilotage | onglet **Qualité** | Oui | SteeringView.cs:88 |
| Exporter l'onglet | Pilotage | **Exporter (CSV)** | Oui | SteeringView.cs:72 |
| Aller de la ligne d'une créance ou d'un prescripteur vers son détail | Pilotage | — | PARTIEL | Toujours aucun double-clic ni lien : il faut ouvrir Facturation / Statistiques prescripteurs à la main. |

## Statistiques prescripteurs

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Calculer l'activité par prescripteur avec comparaisons | Statistiques prescripteurs | **Calculer** | Oui | PrescriberStatisticsView.cs:87 → LoadAsync l.129 ; à l'ouverture |
| Nouveaux / perdus / par clinique / par mois / détail | Statistiques prescripteurs | onglets **Par prescripteur**, **Par clinique / établissement**, **Par mois**, **Nouveaux prescripteurs**, **Prescripteurs perdus**, **Détail mensuel** | Oui | PrescriberStatisticsView.cs:80, 113-118 |
| Exporter | Statistiques prescripteurs | **Exporter (CSV)** | Oui | PrescriberStatisticsView.cs:88 |
| Planifier une visite depuis un prescripteur perdu | Statistiques prescripteurs | — | PARTIEL | Pas de bouton ni de double-clic vers **Nouvelle visite** ; il faut passer par Visites et conventions et rechoisir le prescripteur. |
| Accès cohérent avec la fonction | — | menu | à vérifier | La fonction accepte aussi le droit « pilotage », mais le menu exige « relations — consultation ». Sans effet avec les rôles livrés. |

## Visites et conventions

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les visites de la période | Visites et conventions | onglet **Visites**, **Actualiser** | Oui | RelationsView.cs:138 (bouton renommé l.187), LoadAsync l.218 |
| Compteurs sous le titre | Visites et conventions | « N visite(s) · N à prévoir · N convention(s) » | Oui | RelationsView.cs:252 |
| Filtrer les visites par prescripteur | Visites et conventions | — | **MANQUANT** | La fonction de liste accepte un prescripteur, mais l'écran passe toujours « tous » (RelationsView.cs:224). |
| Voir les visites à prévoir | Visites et conventions | onglet **Visites à prévoir** | Oui | RelationsView.cs:115 |
| Créer une visite | Visites et conventions | **Nouvelle visite** (droit gestion) | Oui | RelationsView.cs:146 → EditVisitAsync l.297 |
| Modifier une visite | Visites et conventions | **Modifier**, double-clic | PARTIEL | RelationsView.cs:147-148. **Modifier** est grisé sans ligne choisie (l.177). Le double-clic ne marche toujours que dans l'onglet **Visites**, pas dans **Visites à prévoir**. |
| Supprimer une visite avec motif | Visites et conventions | **Plus d'actions** > **Supprimer** + motif | Oui | RelationsView.cs:122, 149 → DeleteVisitAsync l.349 |
| Créer / modifier une convention | Visites et conventions | **Nouvelle convention**, **Modifier**, double-clic | Oui | RelationsView.cs:150-152 → EditPartnershipAsync l.363 |
| État de la convention en pastille | Visites et conventions | colonne **État** | Oui | RelationsView.cs:105-109 |
| Mettre fin à une convention | Visites et conventions | case **Actif** dans **Modifier** | Oui | pas de suppression (voulu) |
| Afficher les conventions inactives | Visites et conventions | **Afficher les conventions inactives** (au bout de la ligne de rappel) | Oui | RelationsView.cs:124-131 (case au bout de la ligne de rappel), 145 |
| Joindre le document signé | Visites et conventions | **Plus d'actions** > **Joindre le document…** | Oui | RelationsView.cs:153 → AttachAsync l.438 |
| Ouvrir le document | Visites et conventions | **Plus d'actions** > **Ouvrir le document** | Oui | RelationsView.cs:154 → OpenFileAsync l.472 ; grisé sans document (l.179). Il ouvre la dernière version, sans accès aux versions précédentes (à vérifier). |
| Appliquer la remise / le délai de paiement de la convention | Facturation | — | **MANQUANT** | Les champs **Remise convenue (%)** et **Délai de paiement (jours)** sont enregistrés, mais aucune facture ni aucun tarif ne les lit. |
| Convention de sous-traitance liée aux envois | Sous-traitance | — | **MANQUANT** | Une convention « Sous-traitance » n'est reliée ni au laboratoire partenaire ni aux envois. C'est un registre documentaire seulement. |
| Lecture seule pour la Comptabilité | Visites et conventions | boutons masqués | Oui | RelationsView.cs:117-120, 166-175 ; double-clic sans effet sans le droit (l.148, 152) |
| Exporter l'onglet | Visites et conventions | **Exporter (CSV)** | Oui | RelationsView.cs:139 |
| Période appliquée aux onglets | Visites et conventions | **Du** / **Au** | PARTIEL | La période ne filtre que **Visites** ; sur les deux autres onglets, elle est affichée mais sans effet. |

## Rappels et enquêtes

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir les rappels dus aujourd'hui | Rappels et enquêtes | onglet **Rappels à envoyer** | Oui | PatientMessagingView.cs:130, 144 (recalcul au changement d'onglet) |
| Envoyer les rappels cochés | Rappels et enquêtes | case **Envoyer**, **Envoyer la sélection** | Oui | PatientMessagingView.cs:147 → SendRemindersAsync l.354 |
| Créer une règle | Rappels et enquêtes | **Nouvelle règle** | Oui | PatientMessagingView.cs:148 → EditRuleAsync l.386 |
| Modifier / désactiver une règle | Rappels et enquêtes | **Modifier**, double-clic, case **Actif** | Oui | PatientMessagingView.cs:149-150 ; pas de suppression (voulu) |
| Texte anglais d'une règle | Rappels et enquêtes | — | PARTIEL | Seuls **Texte en français** et **Texte en arabe** sont saisis ; un texte anglais existant serait effacé à l'enregistrement (PatientMessagingView.cs:430). |
| Paramétrer l'enquête | Rappels et enquêtes | **Plus d'actions** > **Paramètres de l'enquête** | Oui | PatientMessagingView.cs:154 → EditSettingsAsync l.473 |
| Voir et envoyer les enquêtes | Rappels et enquêtes | onglet **Enquêtes à envoyer**, **Envoyer la sélection** | Oui | PatientMessagingView.cs:151 → SendSurveysAsync l.367 |
| Saisir une réponse | Rappels et enquêtes | **Plus d'actions** > **Saisir une réponse** | Oui | PatientMessagingView.cs:152 → RecordAnswerAsync l.438 |
| Importer des réponses SMS | Rappels et enquêtes | **Plus d'actions** > **Importer des réponses SMS** | PARTIEL | PatientMessagingView.cs:153 → ImportRepliesAsync l.454. Les lignes refusées s'affichent toujours avec un code technique non traduit (PatientMessagingService.cs:758, ex. « K7PQ2M: Survey_AlreadyAnswered »). |
| Résultats des enquêtes | Rappels et enquêtes | onglet **Résultats des enquêtes** | Oui | PatientMessagingView.cs:128-129 |
| Journal des envois | Rappels et enquêtes | onglet **Journal des envois** | PARTIEL | Le statut est en pastille (l.118-122). La raison d'un échec est toujours affichée en code technique entre crochets (l.324), non traduite. |
| Exporter avec trace d'accès | Rappels et enquêtes | **Exporter (CSV)** | Oui | PatientMessagingView.cs:143 → ExportCurrentAsync l.227 (LogExportAsync l.239) |
| Renvoyer un SMS en échec | Rappels et enquêtes | — | PARTIEL | Pas de bouton « renvoyer » : un rappel en échec revient dans **Rappels à envoyer** tant qu'il est dans sa fenêtre ; une enquête en échec revient dans **Enquêtes à envoyer**. |
| Période sur les onglets Rappels / Règles | Rappels et enquêtes | **Du** / **Au** | PARTIEL | Affichée, mais sans effet sur ces deux onglets (calcul « aujourd'hui »). |
| Envoi sans passerelle SMS | Rappels et enquêtes | bandeau d'avertissement | Oui | Messaging.SmsNotConfiguredNotice (PatientMessagingView.cs:202) ; l'envoi est refusé avec « Le fournisseur SMS n'est pas configuré. » |
| Écran bloqué pendant un envoi | Rappels et enquêtes | — | Oui | PatientMessagingView.cs:157-166 (barre et onglets désactivés) |

## Manques à corriger (par impact sur le travail quotidien)

1. **Codes techniques non traduits** dans **Importer des réponses SMS** (lignes refusées) et dans **Journal des envois** (raison de l'échec).
2. **Remise et délai de paiement des conventions sans effet** sur la facturation, et **convention de sous-traitance non reliée** au module Sous-traitance.
3. **Pas de filtre par prescripteur** dans **Visites**, et pas de passage direct de **Statistiques prescripteurs** vers **Nouvelle visite**.
4. Période affichée mais ignorée sur plusieurs onglets (Visites à prévoir, Conventions, Rappels à envoyer, Règles de rappel).
5. Règle de rappel : le texte anglais est perdu à l'enregistrement.
6. Double-clic inactif dans **Visites à prévoir** ; pas de lien depuis les lignes du **Pilotage** vers le détail.
7. **Exporter (CSV)** muet sur un onglet vide dans **Statistiques**.
8. Droit « déclaration épidémiologique » sans écran.
