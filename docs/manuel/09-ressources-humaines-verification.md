# Vérification de l'interface — Ressources humaines (chapitre 09)

Méthode : chaque opération offerte par l'application pour ce module (gestion du personnel, documents RH, solde de tout compte, congés, planning, paie, accidents) a été rapprochée du bouton qui la déclenche. Fichiers d'écran dans `src/MarocLIS.Desktop/Views/`. « Présent ? » : **OUI**, **PARTIEL**, **MANQUANT**.

## Dossiers du personnel

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les salariés (avec sortis) | Dossiers du personnel | Actualiser, Afficher les salariés sortis | OUI | PersonnelViews.cs:157-158 |
| Rechercher | Dossiers du personnel | Nom, matricule ou fonction… (Entrée) | OUI | PersonnelViews.cs:150 ; filtre local |
| Créer un dossier | Dossiers du personnel | Nouveau dossier → Enregistrer | OUI | PersonnelViews.cs:160 ; PersonnelDialogs.cs:198 ; droit Gérer le personnel |
| Modifier un dossier | Dossiers du personnel | Modifier le dossier / double-clic | OUI | PersonnelViews.cs:161-166 |
| Salaire, RIB, banque | Fenêtre du dossier | Salaire de base brut (DH), RIB (24 chiffres), Banque | OUI | PersonnelDialogs.cs:167-176 ; visible seulement avec droit Salaires |
| Relier le compte de connexion | Fenêtre du dossier | Compte SIL | OUI | PersonnelDialogs.cs:126 ; indispensable pour demander ses congés |
| Ajouter une pièce | Pièces du dossier | Ajouter une pièce | OUI | PersonnelDialogs.cs:346 |
| Modifier une pièce | Pièces du dossier | Modifier la pièce / double-clic | OUI | PersonnelDialogs.cs:347, 364 |
| Joindre un scan | Pièces du dossier | Joindre un scan | OUI | PersonnelDialogs.cs:348 |
| Ouvrir un scan | Pièces du dossier | Ouvrir le scan | OUI | PersonnelDialogs.cs:349 ; ouvert aussi pour le Responsable qualité (lecture) |
| Retirer une pièce | Pièces du dossier | Retirer la pièce (motif) | OUI | PersonnelDialogs.cs:350 |
| Enregistrer une sortie | Dossiers du personnel | Enregistrer la sortie | OUI | PersonnelViews.cs:168, 417-447 |
| Corriger une sortie non encore effective | Dossiers du personnel | Modifier la sortie → Corriger la sortie | OUI | PersonnelViews.cs:169, 449 |
| Annuler une sortie non encore effective | Dossiers du personnel | Modifier la sortie → Annuler la sortie | OUI | PersonnelViews.cs:170, 472 |
| Réintégrer | Dossiers du personnel | Réintégrer | OUI | PersonnelViews.cs:436-441 ; les dates de contrat sont à ressaisir par Modifier le dossier (deux étapes) |
| Attestation de travail | Dossiers du personnel | Attestations → Attestation de travail | OUI | PersonnelViews.cs:171-181 |
| Attestation de salaire | Dossiers du personnel | Attestations → Attestation de salaire | OUI | PersonnelViews.cs:182 |
| Certificat de travail | Dossiers du personnel | Attestations → Certificat de travail (sortie) | OUI | PersonnelViews.cs:183 |
| Calculer / enregistrer le solde de tout compte | Dossiers du personnel | Solde de tout compte → Calculer le solde de tout compte… | OUI | PersonnelViews.cs:185, 349 ; PersonnelDialogs.cs:786 |
| Imprimer le reçu | Dossiers du personnel | Solde de tout compte → Imprimer le reçu pour solde de tout compte | OUI | PersonnelViews.cs:186 |
| Enregistrer la signature du reçu | Dossiers du personnel | Solde de tout compte → Reçu signé par le salarié… | OUI | PersonnelViews.cs:187 |
| Désactiver le compte de connexion d'un sorti | Dossiers du personnel | — | PARTIEL | Pas de lien avec la sortie ; à faire à part dans l'administration des utilisateurs (à vérifier) |

## Habilitations

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir la matrice | Habilitations | Actualiser | OUI | PersonnelViews.cs:576 |
| Accorder / changer de niveau | Habilitations | Accorder une habilitation / double-clic | OUI | PersonnelViews.cs:578, 581 ; « Habilité » / « Formateur » refusés si le compte n'a pas la qualification de biologiste |
| Retirer | Habilitations | Retirer l'habilitation (motif) | OUI | PersonnelViews.cs:579 |
| Exporter pour l'audit | Habilitations | Exporter (CSV) | OUI | PersonnelViews.cs:577 |
| Consulter l'historique (habilitations retirées ou remplacées) | Habilitations | — | PARTIEL | Gardé en base mais seules les habilitations en cours sont affichées et exportées (PersonnelQueries.cs:96) ; un auditeur demande souvent l'historique |
| Droit d'accorder pour le rôle Biologiste | — | — | PARTIEL | Le droit Gérer les habilitations n'est donné qu'à l'Administrateur et au Biologiste responsable ; l'Administrateur sans qualification ne peut accorder qu'« En formation » |

## Échéances RH

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les échéances | Échéances RH | Dans les N jours, Actualiser | OUI | PersonnelViews.cs:912-913 |
| Ouvrir les pièces | Échéances RH | Pièces du dossier | OUI | PersonnelViews.cs:914 |
| Ouvrir le dossier | Échéances RH | Modifier le dossier | OUI | PersonnelViews.cs:915 |
| Traiter une ligne d'accident (déclaration 48 h, sérologie) | Échéances RH | Pièces du dossier / Modifier le dossier | PARTIEL | Les deux boutons ouvrent le dossier du personnel, pas le dossier d'accident : ils ne mènent pas à l'action à faire |
| Traiter une réévaluation d'habilitation | Échéances RH | — | PARTIEL | Pas de bouton vers Habilitations ; passer par le menu |

## Congés et absences

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les demandes | Congés et absences | année, Actualiser, En attente seulement (toutes années) | OUI | LeaveViews.cs:126-128 |
| Demander un congé | Congés et absences | Demander un congé → Envoyer la demande | OUI | LeaveViews.cs:132, 271 |
| Aperçu des jours et du solde | Fenêtre de demande | (calcul affiché) | OUI | LeaveViews.cs:446 |
| Saisir pour un collègue / absence injustifiée | Fenêtre de demande | Salarié, Type | OUI | LeaveViews.cs:279, 395 |
| Valider | Congés et absences | Valider | PARTIEL | LeaveViews.cs:133, 318 ; le commentaire de validation prévu par l'application n'est pas proposé (toujours vide) |
| Refuser | Congés et absences | Refuser (motif) | OUI | LeaveViews.cs:134 |
| Annuler | Congés et absences | Annuler la demande | OUI | LeaveViews.cs:135 |
| **Noter le justificatif remis après la demande** | Congés et absences | — | **MANQUANT** | Le justificatif ne peut être coché qu'à l'envoi de la demande (LeaveViews.cs:413). Rien ne le change ensuite, et la validation l'exige (Leave.cs:75). Un arrêt maladie envoyé sans certificat ne peut donc jamais être validé : il faut l'annuler et le ressaisir |
| Justificatif coché par le salarié lui-même | Fenêtre de demande | Justificatif remis à la RH (certificat, acte d'état civil…) | PARTIEL | Le salarié coche lui-même la case ; la RH ne confirme rien |
| Voir les soldes | Congés et absences | Soldes de congés | OUI | LeaveViews.cs:136 ; acquisition mensuelle automatique (LeaveService.cs:149) |
| Ajuster un solde | Soldes de congés | Ajuster le solde | OUI | LeaveViews.cs:548 |
| Corriger un congé validé déjà commencé (retour anticipé) | Congés et absences | — | PARTIEL | L'annulation est refusée une fois le congé commencé ; on ne peut que corriger le solde. Les dates restent fausses dans le planning et la paie |
| Jours fériés : voir, ajouter, retirer | Jours fériés | Ajouter un jour férié, Retirer | OUI | LeaveViews.cs:137, 690, 694 |

## Planning des gardes

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Naviguer par mois | Planning des gardes | Mois précédent, Mois suivant, Actualiser | OUI | PlanningView.cs:134-136 |
| Affecter un poste (une ou plusieurs cases) | Planning des gardes | Affecter un poste / double-clic | OUI | PlanningView.cs:137, 141 |
| Retirer un poste | Planning des gardes | Retirer le poste / Suppr | OUI | PlanningView.cs:138, 146 |
| Recopier une semaine | Planning des gardes | Recopier la semaine précédente | OUI | PlanningView.cs:139 ; recopie seulement la semaine juste avant, pas une semaine au choix |
| Modifier un poste existant | Planning des gardes | Affecter un poste | PARTIEL | La fenêtre est pré-remplie avec le poste existant, mais l'enregistrement **ajoute** un poste (refusé pour chevauchement) au lieu de le modifier : il faut retirer puis affecter |
| Points à vérifier | Planning des gardes | liste du bas (clic) | OUI | PlanningView.cs:152 |
| Imprimer / diffuser le planning | En-tête | Imprimer (Ctrl+P) | PARTIEL | Impression générique de la grille (MainShellForm.cs:699) ; pas de mise en page dédiée ; lisibilité d'un mois à vérifier |
| Voir son propre planning (technicien, secrétaire…) | — | — | MANQUANT | L'écran demande Consulter le personnel ou Planning ; les autres rôles ne voient pas leurs gardes |
| Heures du planning vers la paie | — | — | MANQUANT | Aucun lien : les heures supplémentaires se ressaisissent à la main dans la paie |

## Paie

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Choisir le mois | Paie | liste des mois | OUI | PayrollView.cs:148 |
| Taux et barème | Paie | Taux et barème | OUI | PayrollView.cs:151, 549 |
| Ouvrir un mois | Paie | Ouvrir un mois | OUI | PayrollView.cs:149, 353 |
| Variables d'un bulletin | Paie | Variables du mois / double-clic | OUI | PayrollView.cs:152-153 |
| Reprendre les dossiers | Paie | Reprendre les dossiers | OUI | PayrollView.cs:160 |
| Imprimer un ou tous les bulletins | Paie | Bulletin, Tous les bulletins | OUI | PayrollView.cs:154-155 ; un brouillon est marqué « PROVISOIRE » |
| Journal de paie / récapitulatif CNSS | Paie | Journal de paie, Récapitulatif CNSS | OUI | PayrollView.cs:156-157 |
| Clôturer / rouvrir | Paie | Clôturer le mois / Rouvrir le mois | OUI | PayrollView.cs:161, 394 |
| Ordre de virement, fichier banque | Paie | Virement → Ordre de virement (PDF) / Fichier pour la banque (CSV) | OUI | PayrollView.cs:162-164 |
| Fichier BDS DAMANCOM | Paie | Fichier BDS (DAMANCOM)… | OUI | PayrollView.cs:158 |
| État 9421 Simpl-IR | Paie | État 9421 (Simpl-IR)… | OUI | PayrollView.cs:159 |
| Marquer la paie comme payée | Paie | — | MANQUANT | Pas de fonction ; le paiement n'est suivi que par le rapprochement bancaire en Trésorerie |
| Paie gérée par la comptabilité | — | — | PARTIEL | Le rôle Comptabilité n'a pas le droit Salaires : la paie revient au seul Biologiste responsable / Administrateur (à vérifier si c'est voulu) |

## Accidents du travail

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister par année | Accidents du travail | année, Actualiser | OUI | AccidentView.cs:171-172 |
| Déclarer | Accidents du travail | Déclarer un accident | OUI | AccidentView.cs:173 |
| Compléter (déclaration envoyée, assureur, IPP) | Accidents du travail | Compléter / double-clic | OUI | AccidentView.cs:174-175 |
| Arrêt, prolongation, rechute | Accidents du travail | Arrêt / prolongation | OUI | AccidentView.cs:180 |
| **Raccourcir ou annuler un arrêt** | Accidents du travail | — | **MANQUANT** | Aucune fonction. La reprise est refusée pendant un arrêt (« la reprise ne peut être qu'après ») et le message suppose qu'on raccourcit l'arrêt d'abord, ce qui est impossible. Une fois l'arrêt commencé, il ne peut pas non plus être annulé depuis Congés et absences. Une reprise anticipée ne peut donc pas être enregistrée |
| Reprise du travail | Accidents du travail | Reprise du travail | OUI | AccidentView.cs:181 |
| Suivi assureur | Accidents du travail | Suivi assureur | OUI | AccidentView.cs:182 |
| Contrôle sérologique fait | Accidents du travail | Contrôle fait | OUI | AccidentView.cs:183 |
| Déclaration PDF | Accidents du travail | Déclaration (PDF) | OUI | AccidentView.cs:184 |
| Registre annuel | Accidents du travail | Registre de l'année | OUI | AccidentView.cs:185 |
| Clore | Accidents du travail | Clore le dossier | OUI | AccidentView.cs:186 |

## Boutons qui ne mènent pas où on l'attend

- **Échéances RH**, lignes « Déclaration d'accident à l'assureur (48 h) » et « Contrôle sérologique après AES » : **Pièces du dossier** et **Modifier le dossier** ouvrent le dossier du personnel, sans rapport avec l'action à faire.
- **Planning des gardes**, **Affecter un poste** sur une case déjà remplie : la fenêtre montre le poste actuel, comme pour le modifier, mais l'enregistrement est refusé (« Le salarié a déjà un poste sur ces heures. »).
- **Accidents du travail**, **Reprise du travail** pendant un arrêt en cours : le refus renvoie vers un raccourcissement d'arrêt qui n'existe pas.

## Manques à corriger (par impact sur le travail quotidien)

1. **Justificatif de congé non modifiable après la demande** : un arrêt maladie, une naissance ou un décès envoyé sans justificatif ne peut jamais être validé. Il faut annuler puis ressaisir. Ajouter un bouton RH « Justificatif reçu ».
2. **Pas de raccourcissement ni d'annulation d'un arrêt d'accident** : une reprise anticipée ne peut pas être enregistrée, et la paie retient trop de jours.
3. **Congé validé déjà commencé non modifiable** (retour anticipé, maladie pendant les congés) : seul le solde se corrige ; le planning et la paie gardent les mauvaises dates.
4. **Pas de modification directe d'un poste au planning** (retirer puis affecter) et recopie limitée à la semaine précédente.
5. **Le personnel ne voit pas son planning** : seuls la direction et le Responsable qualité y ont accès.
6. **Échéances RH sans lien vers l'action** pour les accidents et les réévaluations d'habilitation.
7. **Validation de congé sans commentaire possible.**
8. **Historique des habilitations non consultable à l'écran ni dans l'export** (preuve d'audit ISO 15189).
9. **Paie** : pas de marque « payée » ; les heures du planning ne passent pas dans la paie ; le rôle Comptabilité n'a pas accès (à confirmer avec le laboratoire).
10. **Sortie d'un salarié sans désactivation de son compte de connexion** (à faire à part dans l'administration ; à vérifier).
