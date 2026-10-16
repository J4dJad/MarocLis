# Vérification de l'interface — Pilotage, statistiques et relations

Légende : **Oui** = le bouton ou le champ existe et déclenche la fonction ; **PARTIEL** = il existe mais il manque quelque chose ou il est mal ciblé ; **MANQUANT** = la fonction existe dans l'application mais aucun écran ne permet de la déclencher.
Les références « fichier:ligne » renvoient au code de l'écran.

## Statistiques

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Choisir une période prédéfinie / libre | Statistiques | liste (**Ce mois-ci**… **Période libre**), **Du**, **Au** | Oui | StatisticsView.cs:193-195 |
| Calculer les statistiques d'activité | Statistiques | **Calculer** | Oui | StatisticsView.cs:196 → GetActivityAsync ; calcul aussi à l'ouverture (294) |
| Consulter par secteur / analyse / payeur / jour | Statistiques | onglets **Par secteur**, **Par analyse**, **Par payeur**, **Par jour** | Oui | StatisticsView.cs:135-141 |
| Exporter l'onglet | Statistiques | **Exporter (CSV)** | PARTIEL | StatisticsView.cs:197 → 383. Sur un onglet vide, rien ne se passe et aucun message (les autres écrans affichent « Aucun élément sélectionné. »). |
| Statistiques multi-sites | Statistiques | — | Normal | Site de connexion uniquement ; le multi-sites est dans Pilotage. |
| Déclaration épidémiologique | — | — | **MANQUANT** | Droit « statistics.declaration » attribué au Biologiste responsable sans aucun écran ni fonction. |

## Pilotage

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Calculer le tableau de pilotage | Pilotage | barre de période, **Calculer** | Oui | SteeringView.cs:70 → GetAsync ; à l'ouverture (96) |
| Masse salariale réservée au droit salaires | Pilotage | tuile **Masse salariale / CA** | Oui | SteeringView.cs:140 ; SteeringService.CanSeePayroll |
| Revenus par jour / site / payeur / prescripteur | Pilotage | onglets **Par jour**, **Par site**, **Par payeur**, **Par prescripteur** | Oui | SteeringView.cs:63, 158-161 |
| Balance âgée des créances | Pilotage | onglet **Créances échues** | Oui | SteeringView.cs:163-167 |
| Indicateurs qualité | Pilotage | onglet **Qualité** | Oui | SteeringView.cs:169-180 |
| Exporter l'onglet | Pilotage | **Exporter (CSV)** | Oui | SteeringView.cs:71 |
| Aller de la ligne d'une créance ou d'un prescripteur vers son détail | Pilotage | — | PARTIEL | Aucun double-clic ni lien : il faut ouvrir Facturation / Statistiques prescripteurs à la main. |

## Statistiques prescripteurs

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Calculer l'activité par prescripteur avec comparaisons | Statistiques prescripteurs | **Calculer** | Oui | PrescriberStatisticsView.cs:83 → GetAsync ; à l'ouverture (123) |
| Nouveaux / perdus / par clinique / par mois / détail | Statistiques prescripteurs | onglets **Par prescripteur**, **Par clinique / établissement**, **Par mois**, **Nouveaux prescripteurs**, **Prescripteurs perdus**, **Détail mensuel** | Oui | PrescriberStatisticsView.cs:76, 186-200 |
| Exporter | Statistiques prescripteurs | **Exporter (CSV)** | Oui | PrescriberStatisticsView.cs:84 |
| Planifier une visite depuis un prescripteur perdu | Statistiques prescripteurs | — | PARTIEL | Pas de bouton ni double-clic vers **Nouvelle visite** ; passer par Visites et conventions et rechoisir le prescripteur. |
| Accès cohérent avec la fonction | — | menu | à vérifier | La fonction accepte aussi le droit « pilotage », mais le menu exige « relations — consultation ». Sans effet avec les rôles livrés (le Biologiste responsable a les deux). |

## Visites et conventions

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les visites de la période | Visites et conventions | onglet **Visites**, **Actualiser** | Oui | RelationsView.cs:101, 168 |
| Filtrer les visites par prescripteur | Visites et conventions | — | **MANQUANT** | La fonction de liste accepte un prescripteur (RelationsService.GetVisitsAsync) mais l'écran passe toujours « tous » (RelationsView.cs:168). |
| Voir les visites à prévoir | Visites et conventions | onglet **Visites à prévoir** | Oui | RelationsView.cs:170 |
| Créer une visite | Visites et conventions | **Nouvelle visite** (droit gestion) | Oui | RelationsView.cs:107 → 224 → SaveVisitAsync |
| Modifier une visite | Visites et conventions | **Modifier**, double-clic | PARTIEL | RelationsView.cs:108-109. Le double-clic ne marche que dans l'onglet **Visites**, pas dans **Visites à prévoir** (le bouton **Modifier** fonctionne dans les deux). |
| Supprimer une visite avec motif | Visites et conventions | **Supprimer** + motif | Oui | RelationsView.cs:110 → 276 → DeleteVisitAsync |
| Créer / modifier une convention | Visites et conventions | **Nouvelle convention**, **Modifier**, double-clic | Oui | RelationsView.cs:111-113 → 289 → SavePartnershipAsync |
| Mettre fin à une convention | Visites et conventions | case **Actif** dans **Modifier** | Oui | RelationsView.cs:334 ; pas de suppression (voulu) |
| Afficher les conventions inactives | Visites et conventions | **Afficher les conventions inactives** | Oui | RelationsView.cs:106 |
| Joindre le document signé | Visites et conventions | **Joindre le document…** | Oui | RelationsView.cs:114 → 364 → AttachDocumentAsync |
| Ouvrir le document | Visites et conventions | **Ouvrir le document** | Oui | RelationsView.cs:115 → 398 ; ouvre la dernière version, pas d'accès aux versions précédentes (à vérifier) |
| Appliquer la remise / le délai de paiement de la convention | Facturation | — | **MANQUANT** | Champs **Remise convenue (%)** et **Délai de paiement (jours)** enregistrés mais utilisés nulle part ailleurs (aucune facture ni tarif ne les lit). |
| Convention de sous-traitance liée aux envois | Sous-traitance | — | **MANQUANT** | Une convention de type « Sous-traitance » n'est reliée ni au laboratoire partenaire ni aux envois du module Sous-traitance. Registre documentaire seulement. |
| Lecture seule pour la Comptabilité | Visites et conventions | boutons masqués | Oui | RelationsView.cs:87-92, 127-133 |
| Exporter l'onglet | Visites et conventions | **Exporter (CSV)** | Oui | RelationsView.cs:102 |
| Période appliquée aux onglets | Visites et conventions | **Du** / **Au** | PARTIEL | La période ne filtre que **Visites** ; sur les deux autres onglets elle est affichée mais sans effet (peut tromper). |

## Rappels et enquêtes

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir les rappels dus aujourd'hui | Rappels et enquêtes | onglet **Rappels à envoyer** | Oui | PatientMessagingView.cs:125, 221 → PreviewRemindersAsync |
| Envoyer les rappels cochés | Rappels et enquêtes | case **Envoyer**, **Envoyer la sélection** | Oui | PatientMessagingView.cs:128 → 312 → SendRemindersAsync |
| Créer une règle | Rappels et enquêtes | **Nouvelle règle** | Oui | PatientMessagingView.cs:129 → 344 → GetTargetsAsync, SaveRuleAsync |
| Modifier / désactiver une règle | Rappels et enquêtes | **Modifier**, double-clic, case **Actif** | Oui | PatientMessagingView.cs:130-131 ; pas de suppression (voulu) |
| Texte anglais d'une règle | Rappels et enquêtes | — | PARTIEL | Seuls **Texte en français** et **Texte en arabe** sont saisis ; un texte anglais existant serait effacé à l'enregistrement (PatientMessagingView.cs:388). Patients anglophones : texte français (à vérifier). |
| Paramétrer l'enquête | Rappels et enquêtes | **Paramètres de l'enquête** | Oui | PatientMessagingView.cs:135 → 429 → GetSettingsAsync, SaveSettingsAsync |
| Voir et envoyer les enquêtes | Rappels et enquêtes | onglet **Enquêtes à envoyer**, **Envoyer la sélection** | Oui | PatientMessagingView.cs:132 → 325 → PreviewSurveysAsync, SendSurveysAsync |
| Saisir une réponse | Rappels et enquêtes | **Saisir une réponse** | Oui | PatientMessagingView.cs:133 → 396 → RecordAnswerAsync |
| Importer des réponses SMS | Rappels et enquêtes | **Importer des réponses SMS** | PARTIEL | PatientMessagingView.cs:134 → 411 → ImportRepliesAsync. Les lignes refusées s'affichent avec un code technique non traduit (ex. « K7PQ2M: Survey_AlreadyAnswered ») au lieu du message français. |
| Résultats des enquêtes | Rappels et enquêtes | onglet **Résultats des enquêtes** | Oui | PatientMessagingView.cs:261 → GetSurveyResultsAsync |
| Journal des envois | Rappels et enquêtes | onglet **Journal des envois** | PARTIEL | PatientMessagingView.cs:275 → GetLogAsync. La raison d'un échec est affichée en code technique entre crochets (ex. « [Messaging_SmsFailed …] »), non traduite. |
| Exporter avec trace d'accès | Rappels et enquêtes | **Exporter (CSV)** | Oui | PatientMessagingView.cs:124 → 192 → LogExportAsync |
| Renvoyer un SMS en échec | Rappels et enquêtes | — | PARTIEL | Pas de bouton « renvoyer » : un rappel en échec revient dans **Rappels à envoyer** tant qu'il est dans sa fenêtre ; une enquête en échec revient dans **Enquêtes à envoyer**. |
| Période sur les onglets Rappels / Règles | Rappels et enquêtes | **Du** / **Au** | PARTIEL | Affichée mais sans effet sur ces deux onglets (calcul « aujourd'hui »). |
| Envoi sans passerelle SMS | Rappels et enquêtes | bandeau d'avertissement | Oui | PatientMessagingView.cs:178 ; l'envoi est refusé avec « Le fournisseur SMS n'est pas configuré. » |

## Manques à corriger (par impact sur le travail quotidien)

1. **Codes techniques non traduits** dans **Importer des réponses SMS** (lignes refusées) et dans **Journal des envois** (raison d'échec) : l'utilisateur ne comprend pas pourquoi.
2. **Remise et délai de paiement des conventions sans effet** sur la facturation, et **convention de sous-traitance non reliée** au module Sous-traitance : risque de croire qu'une remise convenue est appliquée.
3. **Pas de filtre par prescripteur** dans **Visites** (la fonction existe), et pas de passage direct de **Statistiques prescripteurs** vers **Nouvelle visite**.
4. Période affichée mais ignorée sur plusieurs onglets (Visites à prévoir, Conventions, Rappels à envoyer, Règles de rappel) : prévoir de la masquer ou de l'indiquer.
5. Règle de rappel : le texte anglais est perdu à l'enregistrement (seuls FR et AR sont saisis).
6. Double-clic inactif dans **Visites à prévoir** ; pas de lien depuis les lignes du **Pilotage** vers le détail.
7. **Exporter (CSV)** muet sur un onglet vide dans **Statistiques**.
8. Droit « déclaration épidémiologique » sans écran.
