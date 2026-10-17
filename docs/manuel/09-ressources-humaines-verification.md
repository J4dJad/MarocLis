# Vérification de l'interface — Ressources humaines (chapitre 09)

Méthode : chaque opération offerte par l'application pour ce module (gestion du personnel, documents RH, solde de tout compte, congés, planning, paie, accidents) a été rapprochée du bouton qui la déclenche. Fichiers d'écran dans `src/MarocLIS.Desktop/Views/`, règles dans `src/MarocLIS.Application` et `src/MarocLIS.Domain`. « Présent ? » : **OUI**, **PARTIEL**, **MANQUANT**. Revérifié le 2026-10-17 sur le commit 4e20d0d (nouvelle présentation : en-tête, barre de filtres, carte, barre d'actions sous la liste), puis sur la branche reco/r5 pour les décisions du cycle 5 (compte désactivé à la sortie, « Justificatif reçu »).

## Dossiers du personnel

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les salariés (avec sortis) | Dossiers du personnel | Actualiser, pastille Afficher les salariés sortis | OUI | PersonnelViews.cs:92-93, 132 |
| Rechercher | Dossiers du personnel | Nom, matricule ou fonction… (Entrée) | OUI | PersonnelViews.cs:93, 125, 246 ; filtre local |
| Créer un dossier | Dossiers du personnel | Nouveau dossier (en-tête) → Enregistrer | OUI | PersonnelViews.cs:92, 135 ; droit Gérer le personnel (l.166) |
| Modifier un dossier | Dossiers du personnel | Modifier le dossier (panneau) / double-clic | OUI | PersonnelViews.cs:136-139 ; double-clic sans effet sans le droit |
| Salaire, RIB, banque | Fenêtre du dossier | Salaire de base brut (DH), RIB (24 chiffres), Banque | OUI | PersonnelDialogs.cs:178-188 ; visible seulement avec le droit Salaires (l.34) ; RIB contrôlé l.234 |
| Relier le compte de connexion | Fenêtre du dossier | Compte SIL | OUI | PersonnelDialogs.cs:137 ; indispensable pour demander ses congés |
| Ajouter / modifier / joindre / ouvrir / retirer une pièce | Pièces du dossier | Ajouter une pièce, Modifier la pièce (double-clic), Joindre un scan, Ouvrir le scan, Retirer la pièce | OUI | PersonnelDialogs.cs:361-365, 379 |
| Enregistrer une sortie | Dossiers du personnel | Enregistrer la sortie | OUI | PersonnelViews.cs:143, 431 |
| Sortie refusée tant qu'un congé tombe après | Fenêtre de sortie | — | OUI | **Nouveau.** PersonnelService.cs:469, 500, 562-573 (Employee_LeaveAfterExit) : un congé en attente ou validé non commencé qui finit après la sortie bloque la sortie |
| Corriger une sortie non encore effective | Dossiers du personnel | Modifier la sortie → Corriger la sortie | OUI | PersonnelViews.cs:144, 463 |
| Annuler une sortie non encore effective | Dossiers du personnel | Modifier la sortie → Annuler la sortie | OUI | PersonnelViews.cs:145, 486 |
| Réintégrer | Dossiers du personnel | Réintégrer (même bouton que la sortie) | OUI | PersonnelViews.cs:431 ; dialogue PersonnelDialogs.cs:711 ; les dates de contrat sont à ressaisir par Modifier le dossier |
| Attestation de travail / de salaire, certificat de travail | Dossiers du personnel | Attestations → Attestation de travail, Attestation de salaire, Certificat de travail (sortie) | OUI | PersonnelViews.cs:146-158 ; chaque ligne grisée selon le droit et l'état du salarié (l.151-153) ; menu réservé à Gérer le personnel ou Salaires (l.167) |
| Calculer / enregistrer le solde de tout compte | Dossiers du personnel | Solde de tout compte → Calculer le solde de tout compte… | OUI | PersonnelViews.cs:159-160, 363 ; PersonnelDialogs.cs:825 ; droit Salaires (l.168) ; grisé si pas de sortie (l.337) |
| Imprimer le reçu / enregistrer la signature | Dossiers du personnel | Imprimer le reçu pour solde de tout compte, Reçu signé par le salarié… | OUI | PersonnelViews.cs:161-162 ; PersonnelDialogs.cs:978 |
| Annoncer la désactivation du compte à la sortie | Fenêtre de sortie | « Le compte LIS « … » lié à ce dossier sera désactivé à la date de sortie… » | OUI | **Décision cycle 5.** PersonnelViews.cs:442 (compte relié passé à la fenêtre) ; PersonnelDialogs.cs:708-709 (ligne affichée seulement si un compte est relié) |
| Désactiver le compte de connexion d'un sorti | Dossiers du personnel | Enregistrer la sortie / Corriger la sortie | OUI | **Décision cycle 5.** PersonnelService.cs:484, 498 → DepartedAccountDeactivator.ApplyExitAsync : date atteinte → désactivé aussitôt (journal d'audit « Désactivation du compte … : sortie de … », journal de sécurité « Utilisateur désactivé ») ; date à venir → désactivé ce jour-là, à la connexion (AuthenticationService.cs:46) ou au contrôle de session du poste (SecurityServices.cs:289). Le dernier administrateur n'est jamais désactivé ainsi. Ligne d'état : PersonnelViews.cs:471 (ShowAccountOutcome) |
| Compte réactivé après une sortie annulée | Dossiers du personnel | Modifier la sortie → Annuler la sortie | PARTIEL | Volontaire : la réactivation reste une décision de l'administrateur (Utilisateurs) ; la ligne d'état le rappelle (PersonnelService.cs:551) |

## Habilitations

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir la matrice | Habilitations | Actualiser | OUI | PersonnelViews.cs:553, 571 |
| Accorder / changer de niveau | Habilitations | Accorder une habilitation / double-clic | OUI | PersonnelViews.cs:564, 573, 576-578, 755 ; « Habilité » / « Formateur » refusés sans qualification de biologiste |
| Date de décision avant l'embauche | Fenêtre d'habilitation | Date | OUI | **Nouveau.** Employee.cs:381 (Habilitation_BeforeHire) |
| Retirer | Habilitations | Retirer l'habilitation (motif) | OUI | PersonnelViews.cs:564, 574, 784 ; grisé sans case choisie (l.743-750) |
| Exporter pour l'audit | Habilitations | Exporter (CSV) (en-tête) | OUI | PersonnelViews.cs:553, 572, 801 ; grisé sans salarié (l.730) |
| Consulter l'historique (habilitations retirées ou remplacées) | Habilitations | — | PARTIEL | Seules les habilitations en cours sont lues (PersonnelQueries.cs:96, 138) |
| Droit d'accorder pour le rôle Biologiste | — | — | PARTIEL | Gérer les habilitations (PersonnelViews.cs:581) : Administrateur et Biologiste responsable seulement |

## Échéances RH

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les échéances | Échéances RH | Dans les N jours, Actualiser | OUI | PersonnelViews.cs:890, 894, 922-923 ; changer l'horizon recharge la liste |
| Ouvrir les pièces / le dossier | Échéances RH | Pièces du dossier, Modifier le dossier | OUI | PersonnelViews.cs:915, 924-925 ; Modifier le dossier demande Gérer le personnel (l.927) ; grisés si liste vide (l.1024) |
| Traiter une ligne d'accident (déclaration 48 h, sérologie) | Échéances RH | Pièces du dossier / Modifier le dossier | PARTIEL | Les deux boutons ouvrent le dossier du personnel, pas le dossier d'accident |
| Traiter une réévaluation d'habilitation | Échéances RH | — | PARTIEL | Pas de bouton vers Habilitations |

## Congés et absences

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les demandes | Congés et absences | Année, pastille En attente seulement (toutes années), Actualiser | OUI | LeaveViews.cs:63, 71-72, 105-106, 179 |
| Demander un congé | Congés et absences | Demander un congé (en-tête) → Envoyer la demande | OUI | LeaveViews.cs:63, 118, 352 |
| Aperçu des jours et du solde | Fenêtre de demande | (calcul affiché) | OUI | LeaveViews.cs:415-438 |
| Saisir pour un collègue / absence injustifiée | Fenêtre de demande | Salarié, Type | OUI | LeaveViews.cs:272, 374, 380 |
| Solde insuffisant (message au pluriel) | Fenêtre de demande / Valider | — | OUI | LeaveService.cs:219, 249 ; fr.json:299 (« … jours demandés, … disponibles ») |
| Valider | Congés et absences | Valider | PARTIEL | LeaveViews.cs:97, 111, 293-302 ; droit Valider les congés (l.120) ; le commentaire de validation est toujours vide (`ApproveAsync(…, null)`, l.302) |
| Refuser | Congés et absences | Refuser (motif) | OUI | LeaveViews.cs:97, 120 |
| Annuler | Congés et absences | Annuler la demande | OUI | LeaveViews.cs:97, 118 |
| Justificatif manquant signalé | Congés et absences | colonne Justificatif | OUI | LeaveViews.cs:135-137, 207-208 : en rouge pour une demande en attente |
| Noter le justificatif remis après la demande | Congés et absences | Justificatif reçu (barre sous la liste) | OUI | **Décision cycle 5.** LeaveViews.cs:98, 115, 124, 247, 314 ; actif pour une demande en attente dont le justificatif exigé est « Manquant » ; droit Gérer le personnel ; confirmation ; LeaveService.cs:272 (journal d'audit « Justificatif reçu pour le congé … ») ; Leave.cs:104 (refus : demande déjà traitée, type sans justificatif, déjà reçu) ; pas sur sa propre demande |
| Justificatif coché par le salarié lui-même | Fenêtre de demande | Justificatif remis à la RH (certificat, acte d'état civil…) | PARTIEL | Le salarié coche lui-même ; la RH ne confirme rien (« Justificatif reçu » ne sert qu'à cocher une case restée vide) |
| Voir les soldes | Congés et absences | Soldes de congés (barre de filtres) | OUI | LeaveViews.cs:73, 114 ; acquisition mensuelle (LeaveService.cs:146-153) |
| Ajuster un solde | Soldes de congés | Ajuster le solde | OUI | LeaveViews.cs:534, 543 |
| Corriger un congé validé déjà commencé (retour anticipé) | Congés et absences | — | PARTIEL | Annulation refusée une fois le congé commencé ; seul le solde se corrige |
| Jours fériés : voir, ajouter, retirer | Jours fériés (barre de filtres) | Ajouter un jour férié, Retirer | OUI | LeaveViews.cs:73, 115, 680, 684, 693 |
| Recompte des congés touchés par un jour férié ajouté | Jours fériés | — | OUI | **Nouveau.** LeaveViews.cs:725 (Holiday.Recounted) : liste des congés recomptés, différence rendue au solde des congés validés |

## Planning des gardes

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Naviguer par mois | Planning des gardes | Mois précédent, Mois suivant (barre), Actualiser | OUI | PlanningView.cs:74, 129-131 |
| Affecter un poste (une ou plusieurs cases) | Planning des gardes | Affecter un poste / double-clic | OUI | PlanningView.cs:97, 132, 136-138 |
| Retirer un poste | Planning des gardes | Retirer le poste / Suppr | OUI | PlanningView.cs:97, 133, 141-143 |
| Recopier une semaine | Planning des gardes | Recopier la semaine précédente (en-tête) | OUI | PlanningView.cs:66, 134, 430 ; seulement la semaine juste avant |
| Droits | Planning des gardes | — | OUI | PlanningView.cs:161 (Planning — gérer) |
| Modifier un poste existant | Planning des gardes | Affecter un poste | PARTIEL | Fenêtre pré-remplie mais l'enregistrement ajoute un poste, refusé (Planning_ShiftOverlaps, PlanningService.cs:293) : retirer puis affecter |
| Imprimer / diffuser le planning | En-tête | Imprimer (Ctrl+P) | PARTIEL | Impression générique de la grille ; pas de mise en page dédiée |
| Voir son propre planning (technicien, secrétaire…) | — | — | MANQUANT | Les autres rôles n'ont pas l'écran |
| Heures du planning vers la paie | — | — | MANQUANT | Aucun lien |

## Paie

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Choisir le mois | Paie | Mois de paie | OUI | PayrollView.cs:89, 129 |
| Ouvrir un mois | Paie | Ouvrir un mois (barre de filtres) | OUI | PayrollView.cs:89, 130, 345 |
| Taux et barème | Paie | Plus d'actions → Taux et barème | OUI | PayrollView.cs:85, 132, 541 |
| Fichier BDS DAMANCOM / État 9421 Simpl-IR | Paie | Plus d'actions → Fichier BDS (DAMANCOM)…, État 9421 (Simpl-IR)… | OUI | PayrollView.cs:85, 139-140, 467, 500 |
| Variables d'un bulletin | Paie | Variables du mois / double-clic | OUI | PayrollView.cs:120, 133-134 |
| Reprendre les dossiers | Paie | Reprendre les dossiers | OUI | PayrollView.cs:141 |
| Imprimer un ou tous les bulletins | Paie | Bulletin, Tous les bulletins | OUI | PayrollView.cs:135-136 ; brouillon marqué « PROVISOIRE » |
| Journal de paie / récapitulatif CNSS | Paie | Journal de paie, Récapitulatif CNSS | OUI | PayrollView.cs:137-138 |
| Clôturer / rouvrir | Paie | Clôturer le mois / Rouvrir le mois | OUI | PayrollView.cs:142, 386 |
| Ordre de virement, fichier banque | Paie | Virement → Ordre de virement (PDF) / Fichier pour la banque (CSV) | OUI | PayrollView.cs:145 |
| Marquer la paie comme payée | Paie | — | MANQUANT | Pas de fonction |
| Paie gérée par la comptabilité | — | — | PARTIEL | Le rôle Comptabilité n'a pas le droit Salaires (à confirmer) |

## Accidents du travail

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister par année | Accidents du travail | Année, Actualiser | OUI | AccidentView.cs:77, 85, 161-162 |
| Déclarer | Accidents du travail | Déclarer un accident (en-tête) | OUI | AccidentView.cs:77, 163 |
| Compléter | Accidents du travail | Compléter / double-clic | OUI | AccidentView.cs:145, 164-167 |
| Arrêt, prolongation, rechute | Accidents du travail | Arrêt / prolongation | OUI | AccidentView.cs:145, 170 ; chevauchement refusé (AccidentService.cs:290) |
| **Raccourcir ou annuler un arrêt** | Accidents du travail | — | **MANQUANT** | Toujours aucune fonction ; la reprise pendant un arrêt est refusée (Accident_ReturnDuringStoppage, AccidentService.cs:330) |
| Reprise du travail | Accidents du travail | Reprise du travail | OUI | AccidentView.cs:171 ; pas dans le futur (AccidentService.cs:322), pas avant l'accident (Accident.cs:136) |
| Suivi assureur | Accidents du travail | Suivi assureur | OUI | AccidentView.cs:172 |
| Contrôle sérologique fait | Accidents du travail | Contrôle fait | OUI | AccidentView.cs:173 |
| Déclaration PDF | Accidents du travail | Déclaration (PDF) | OUI | AccidentView.cs:174 |
| Registre annuel | Accidents du travail | Registre de l'année (barre de filtres) | OUI | AccidentView.cs:85, 175 |
| Clore | Accidents du travail | Clore le dossier | OUI | AccidentView.cs:176 ; droit Accidents (l.178) |

## Boutons qui ne mènent pas où on l'attend

- **Échéances RH**, lignes « Déclaration d'accident à l'assureur (48 h) » et « Contrôle sérologique après AES » : **Pièces du dossier** et **Modifier le dossier** ouvrent le dossier du personnel, sans rapport avec l'action à faire.
- **Planning des gardes**, **Affecter un poste** sur une case déjà remplie : la fenêtre montre le poste actuel, mais l'enregistrement est refusé (« Le salarié a déjà un poste sur ces heures. »).
- **Accidents du travail**, **Reprise du travail** pendant un arrêt en cours : le refus renvoie vers un raccourcissement d'arrêt qui n'existe pas.

## Manques à corriger (par impact sur le travail quotidien)

1. **Pas de raccourcissement ni d'annulation d'un arrêt d'accident.**
2. **Congé validé déjà commencé non modifiable** (retour anticipé).
3. **Pas de modification directe d'un poste au planning**, recopie limitée à la semaine précédente.
4. **Le personnel ne voit pas son planning.**
5. **Échéances RH sans lien vers l'action** pour les accidents et les réévaluations d'habilitation.
6. **Validation de congé sans commentaire possible.**
7. **Historique des habilitations non consultable** (preuve d'audit ISO 15189).
8. **Paie** : pas de marque « payée » ; heures du planning non reprises ; rôle Comptabilité sans accès (à confirmer).

Corrigés par les décisions du cycle 5 : le justificatif de congé se coche après la demande (« Justificatif reçu ») ; la sortie d'un salarié désactive son compte de connexion à la date de sortie.
