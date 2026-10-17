# Vérification de l'interface — Qualité et équipements

Méthode : pour chaque opération offerte par l'application pour ces écrans (opérations du métier et leurs règles), recherche du bouton ou du champ qui la déclenche. Contrôle refait le 2026-10-17 sur le code au commit 4e20d0d : chaque ligne a été revue, les numéros de ligne mis à jour, et des lignes ajoutées pour les fonctions apparues depuis 4b2639d. Les chemins sont relatifs à `src/MarocLIS.Desktop/Views/` sauf mention contraire.

Légende : **OUI** = présent et fonctionnel ; **PARTIEL** = présent mais incomplet, mal restreint ou trompeur ; **MANQUANT** = possible seulement par le code ou les tests ; **INUTILE** = bouton présent qui ne mène à rien.

## Commun à tous les écrans

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Aide de l'écran | tous | touche F1 | OUI | Ouvre l'aide de l'écran courant ; Maj+F1 affiche la liste des raccourcis. |
| Bouton principal au clavier | Températures, Contrats automates | Ctrl+N | OUI | TemperatureView.cs:209 (**Saisir un relevé…**) ; Contrats : **Nouveau contrat**. |
| Recherche dans la liste | Températures, Équipements, Contrats automates | zone de recherche, Ctrl+F | OUI | InstrumentContractView.cs:78-82 ; filtre instantané de la grille. |
| Menus « Plus d'actions » | Contrôle interne, Non-conformités, Documents, Contrats | **Plus d'actions** | OUI | Les actions secondaires sont regroupées dans un menu (ex. QualityControlView.cs:169-171, InstrumentContractView.cs:75). |
| Ligne conservée après une action | tous | — | OUI | La ligne sélectionnée reste sélectionnée après l'action ou l'actualisation (ex. QualityDocumentsView.cs:278 `keep`). |

## Contrôle interne

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Afficher la feuille de saisie | Contrôle interne | (chargement à l'ouverture), **Actualiser** | OUI | ActivateAsync QualityControlView.cs:479. Les valeurs tapées et non enregistrées sont conservées à l'actualisation. |
| Filtrer la feuille par automate ou par service | Contrôle interne | — | **PARTIEL** | La lecture de la feuille accepte un automate et un service, l'écran ne propose aucun filtre. Toutes les cibles du site s'affichent. |
| Saisir une série multiniveau | Contrôle interne | colonne **Résultat**, **Entrée**, **Enregistrer** (Ctrl+S) | OUI | SubmitAsync QualityControlView.cs:735. Valeur techniquement impossible ou non numérique refusée. |
| Commentaire sur un point de contrôle | Contrôle interne | — | **MANQUANT** | L'opération accepte un commentaire par point, aucun champ pour le saisir. |
| Annonce des rejets et des résultats patients bloqués | Contrôle interne | fenêtre après **Enregistrer**, pastille « … résultats patients bloqués » | OUI | SubmitAsync QualityControlView.cs:735 et suivantes. |
| Carte de Levey-Jennings | Contrôle interne | sélection d'une ligne, clic sur un point | OUI | ShowChartAsync QualityControlView.cs:674 ; ligne « Règles actives : … » sous la carte. Période non modifiable. |
| Imprimer la carte de Levey-Jennings | Contrôle interne | — | **MANQUANT** | Aucun bouton n'imprime ni n'exporte la carte ; Ctrl+P imprime la grille. |
| Revue d'un contrôle rejeté avec action corrective | Contrôle interne | **Revue du contrôle** | OUI | ReviewSelectedAsync QualityControlView.cs:835 ; 15 caractères minimum. |
| Seconde revue d'un même rejet | Contrôle interne | **Revue du contrôle** | OUI (nouveau) | Refusée par la règle (contrôle déjà revu). |
| Ouvrir une non-conformité depuis la revue | Contrôle interne | question « Ouvrir une fiche de non-conformité pour ce rejet ? » | OUI | Proposée si des résultats patients sont concernés (ReviewSelectedAsync). |
| Établir les limites du laboratoire | Contrôle interne | **Plus d'actions** de la carte › **Établir les limites du laboratoire** | PARTIEL | EstablishLimitsAsync QualityControlView.cs:924, période calculée ligne 954 (N derniers jours, 1 à 3650). Impossible de choisir une période passée (début et fin), alors que l'opération l'accepte. Écart type nul refusé (nouveau). |
| Choisir les règles de Westgard | Contrôle interne | **Plus d'actions** de la carte › **Règles de Westgard**, **Multirègle classique…**, **1₃ₛ seule**, **Enregistrer** | OUI | EditRulesAsync QualityControlView.cs:877 ; WestgardRuleDialog.cs:96, 100, 104. **Enregistrer** grisé si aucune règle n'est cochée (nouveau). |
| Évaluer un nouveau lot (saisie) | Contrôle interne | liste **Lot de réactif**, **Automate du lot…**, **Enregistrer** | OUI | LoadReagentLotsAsync QualityControlView.cs:408, analyseurs ligne 451. |
| Démarrer / arrêter l'évaluation d'un lot transmis | Contrôle interne | **Démarrer l'évaluation**, **Arrêter l'évaluation** | OUI | ChangeEvaluationAsync QualityControlView.cs:709 ; activation lignes 474-476. |
| Dérogation à un contrôle rejeté | Validation biologique | (hors de ce chapitre) | OUI | Infrastructure/Queries/ResultQueries.cs ; visible sur la carte (cercle pointillé). |
| Créer matériau et cible de contrôle | Données de référence, onglet Contrôle interne | **Nouveau matériau de contrôle**, **Nouvelle cible de contrôle**, **Modifier** | OUI | MasterDataView.cs:376 (droit Règles du contrôle interne). |
| Droits sur les boutons | Contrôle interne | — | OUI | QualityControlView.cs:269-271. |

## Non-conformités

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Registre, filtre ouvertes / catégorie | Non-conformités | pastille **Ouverte**, liste **Toutes les catégories**, **Actualiser** | OUI | ActivateAsync NonConformityView.cs:303. |
| Panneau de détail et barre d'actions | Non-conformités | panneau latéral, barre d'actions | OUI (nouveau) | LoadDetailAsync NonConformityView.cs:395 ; boutons limités au statut de la fiche (SetDetailEnabled ligne 510). Fiche clôturée ou écartée en lecture seule. |
| Ouvrir une non-conformité | Non-conformités | **Ouvrir une non-conformité** | PARTIEL | OpenNewAsync NonConformityView.cs:556. Manquent dans la fenêtre : le **Pilote** (toujours l'auteur), le lien vers le dossier, l'échantillon, l'automate, le lot de réactif ou le service. Sans service, la détection de récidive compare toute la catégorie. |
| Déclaration par le technicien ou le secrétariat | Non-conformités | **Ouvrir une non-conformité** | PARTIEL | Corrigé pour le technicien : nouveau droit « déclarer une non-conformité » (NonConformityView.cs:194), donné aussi aux biologistes, au responsable qualité et à l'administrateur. Le secrétariat et les préleveurs n'ont toujours pas l'écran. |
| Changer le pilote ou l'échéance d'une fiche | Non-conformités | — | **MANQUANT** | Aucune opération ni bouton pour réattribuer ou reporter. |
| Ajouter une action | Non-conformités | **Ajouter une action** | PARTIEL | AddActionAsync NonConformityView.cs:576. Le responsable de l'action est toujours l'utilisateur connecté (identifiant vide, ligne 587). Une action ajoutée à une fiche « En attente de vérification » la remet « En cours » (nouveau). |
| Démarrer une action | Non-conformités | **Démarrer l'action** | OUI | StartActionAsync NonConformityView.cs:758. |
| Solder une action avec preuve | Non-conformités | **Solder l'action** | OUI | CompleteActionAsync NonConformityView.cs:599. |
| Annuler une action | Non-conformités | **Annuler l'action** | OUI | CancelActionAsync NonConformityView.cs:779. Corrigé : une action annulée compte comme terminée pour la clôture (src/MarocLIS.Domain/Quality/NonConformity.cs:135) ; au moins une action doit être soldée (ligne 159-160). |
| Analyse des causes, impact clinique, efficacité | Non-conformités | **Causes et efficacité** | OUI | RecordAnalysisAsync NonConformityView.cs:635. Corrigé : la fenêtre est préremplie avec ce qui est enregistré ; le champ s'appelle **Catégorie de cause**. |
| Clôturer | Non-conformités | **Clôturer la fiche** | OUI | CloseAsync NonConformityView.cs:675 ; liste complète des manques affichée. |
| Réouvrir une fiche clôturée | Non-conformités | **Réouvrir** | OUI | Corrigé : ReopenAsync NonConformityView.cs:701, bouton actif pour une fiche clôturée (SetDetailEnabled ligne 510-528). Motif d'au moins 10 caractères. |
| Écarter un signalement | Non-conformités | **Écarter la fiche** | OUI | DismissAsync NonConformityView.cs:730. |
| Non-conformité automatique (CQ, température, EEQ, équipement) | — | — | OUI | Le pilote est la personne qui a déclenché l'événement. |
| Consulter le détail complet (action immédiate, analyse, efficacité, comptes rendus rectifiés) | Non-conformités | — | **PARTIEL** | Le panneau montre les faits principaux ; l'analyse se relit en rouvrant **Causes et efficacité**. Pas de vue complète d'une fiche. |
| Imprimer / exporter une fiche | Non-conformités | — | **MANQUANT** | Seule la grille s'imprime (Ctrl+P). |

## Évaluation externe

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste par année, en attente seulement | Évaluation externe | **Année**, **Évaluations en attente seulement**, **Actualiser** | OUI | LoadAsync EqaView.cs:195. |
| Enregistrer une campagne | Évaluation externe | **Enregistrer une campagne** | OUI | RegisterRoundAsync EqaView.cs:304 ; droit ligne 132. |
| Saisir le résultat transmis | Évaluation externe | **Saisir le résultat transmis** | OUI | RecordSubmissionAsync EqaView.cs:335 ; actif seulement si « En attente » ; droit ligne 133. |
| Saisir l'évaluation, NC automatique | Évaluation externe | **Saisir l'évaluation** | PARTIEL | RecordEvaluationAsync EqaView.cs:348. Score hors limites refusé (nouveau). Le texte d'aide dit « |Z| ≥ 3 est non satisfaisant », le calcul classe |Z| = 3 en « Douteux » (src/MarocLIS.Domain/Quality/ControlledDocument.cs:380-381). |
| Corriger ou supprimer une campagne mal enregistrée | Évaluation externe | — | **MANQUANT** | Ni modification ni suppression d'une ligne enregistrée par erreur. |
| Joindre le rapport de l'organisateur | Évaluation externe | — | **MANQUANT** | Aucune pièce jointe. |

## Documents qualité

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Registre, documents retirés | Documents qualité | **Afficher aussi les documents retirés**, **Actualiser** | OUI | LoadAsync QualityDocumentsView.cs:278. |
| Créer un document | Documents qualité | **Nouveau document** | OUI | CreateDocumentAsync QualityDocumentsView.cs:466. |
| Modifier titre, type, périodicité, responsable | Documents qualité | — | **MANQUANT** | Aucune modification après création ; le **Responsable** est toujours le créateur. |
| Nouvelle version (fichier) | Documents qualité | **Nouvelle version**, **Parcourir…** | OUI | AddVersionAsync QualityDocumentsView.cs:485 ; grisé si une version est déjà en cours ou si le document est retiré (RefreshButtons ligne 429-438, nouveau). |
| Abandonner un brouillon erroné | Documents qualité | — | **MANQUANT** | Un brouillon en cours bloque toute nouvelle version et ne peut être ni supprimé ni renvoyé. |
| Renvoyer une version en revue au rédacteur | Documents qualité | — | **MANQUANT** | Pas de refus de revue ni d'approbation. |
| Soumettre, revoir, approuver | Documents qualité | **Soumettre à la revue**, **Enregistrer la revue**, **Approuver** | OUI | StepAsync QualityDocumentsView.cs:503 ; droits lignes 193-194 ; rédacteur ≠ vérificateur ≠ approbateur dans le domaine. |
| Mettre en application (à une date future) | Documents qualité | **Mettre en application** | OUI | MakeEffectiveAsync QualityDocumentsView.cs:517. Nouveau : une date future donne le statut « Programmé » (ligne 171). |
| Versions figées d'un document retiré | Documents qualité | — | OUI (nouveau) | Aucune étape possible sur les versions d'un document retiré. |
| Ouvrir le fichier | Documents qualité | **Ouvrir le fichier** | OUI | OpenAsync QualityDocumentsView.cs:537 ; message dédié si Windows refuse l'accès au fichier (nouveau). |
| Prise de connaissance | Documents qualité | **J'ai pris connaissance** | PARTIEL | AcknowledgeAsync QualityDocumentsView.cs:562. Le secrétariat, les préleveurs et la comptabilité n'ont pas l'écran, donc ne peuvent ni lire ni attester les procédures qui les concernent. |
| Voir qui a lu / qui n'a pas lu | Documents qualité | — | **MANQUANT** | Seule la colonne **Lu** de l'utilisateur connecté est visible. |
| Confirmer la revue périodique sans changement | Documents qualité | — | **MANQUANT** | L'échéance part de la date d'application de la version en vigueur (src/MarocLIS.Domain/Quality/ControlledDocument.cs:66-67) : seule une nouvelle version appliquée la remet à jour. |
| Retirer / réactiver | Documents qualité | **Retirer le document** / **Réactiver le document** | OUI | ToggleActiveAsync QualityDocumentsView.cs:580. |

## Températures

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Saisir un relevé, NC si hors limites | Températures | **Saisir un relevé…** (Ctrl+N), double-clic | OUI | RecordAsync TemperatureView.cs:335 ; Ctrl+N ligne 209 ; actif seulement pour une enceinte active (ligne 93). Valeurs invraisemblables refusées (nouveau). |
| Créer / modifier / désactiver une enceinte | Températures | **Nouvelle enceinte…**, **Modifier l'enceinte…**, case **Actif** | OUI | EditUnitAsync TemperatureView.cs:351 ; droits lignes 80-81. Plage invraisemblable refusée (nouveau). |
| Fiche mensuelle | Températures | sélecteur du mois, **Fiche du mois (PDF)**, **Fiche du mois (CSV)** | OUI | ExportAsync TemperatureView.cs:363 ; grisés sans enceinte choisie (nouveau). |
| Corriger un relevé erroné | Températures | — | **MANQUANT** | Aucune correction ni annulation motivée ; une faute de frappe hors plage ouvre une NC qu'il faudra écarter. |
| Alerte relevés insuffisants | Températures | colonne **Relevés du jour** (en orange) | PARTIEL | L'alerte ne se déclenche qu'à zéro relevé (src/MarocLIS.Application/Inventory/TemperatureMonitoringService.cs:27) ; 1 relevé sur 2 attendus n'est pas signalé. |
| Lien enceinte ↔ équipement | Températures / Équipements | — | **MANQUANT** | Un réfrigérateur saisi dans les deux écrans n'est pas relié. |

## Équipements

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Registre, filtre échéances, recherche | Équipements | **N'afficher que les échéances dépassées…**, zone de recherche, **Actualiser** | OUI | LoadAsync EquipmentView.cs:250. |
| Enregistrer une intervention | Équipements | **Enregistrer une intervention** | PARTIEL | RecordMaintenanceAsync EquipmentView.cs:400 ; nature par défaut « Réparation après panne ». Toujours datée du jour. Une « Vérification métrologique » ne recale pas l'échéance de vérification. |
| Contrôle après intervention non conforme | Équipements | case **Contrôle après intervention conforme** | OUI | Corrigé : décochée, l'équipement passe Hors service et une non-conformité préremplie (Équipement, Majeure) est proposée (OfferNonConformityAsync EquipmentView.cs:426, droit ligne 432). |
| Enregistrer un étalonnage | Équipements | **Enregistrer un étalonnage** | PARTIEL | RecordCalibrationAsync EquipmentView.cs:457. Étalonnage par paramètre impossible (toujours sans paramètre, ligne 465) ; daté du jour. Corrigé : le lot d'étalon est exigé aussi pour « Conforme sous réserve » (RefreshLotRequirement ligne 787). « Conforme sous réserve » ne recale toujours pas l'échéance (à vérifier si voulu). Technicien sans droit d'étalonnage. |
| Changer le statut | Équipements | **Changer le statut** | OUI | ChangeStatusAsync EquipmentView.cs:477 ; motif d'au moins 5 caractères. |
| Équipement réformé | Équipements | — | OUI (nouveau) | « Réformé » est définitif : boutons grisés avec un message (RefreshNotice ligne 925). |
| Consulter l'historique (fiche de vie) | Équipements | — | **MANQUANT** | Interventions et étalonnages enregistrés ne sont visibles nulle part (sauf interventions des automates sous contrat dans **Contrats automates**). |
| Créer un équipement, périodicités | Données de référence | **Nouvel équipement**, **Modifier** | OUI | MasterDataView.cs:375. |
| Échéance de vérification métrologique | Équipements | — | **MANQUANT** | Calculée par le domaine, jamais affichée. |
| Droits sur les boutons | Équipements | — | OUI | EquipmentView.cs:175-177. |

## Contrats automates

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Tableau des contrats et alertes | Contrats automates | **Afficher aussi les contrats résiliés**, zone de recherche, **Actualiser** | OUI | LoadAsync InstrumentContractView.cs:234 ; filtres ligne 78. |
| Créer / modifier un contrat (réactifs captifs, engagement) | Contrats automates | **Nouveau contrat** (Ctrl+N), **Modifier**, double-clic | OUI | EditAsync InstrumentContractView.cs:455 ; double-clic ligne 160 ; droit ligne 172. |
| Joindre / ouvrir le contrat | Contrats automates | **Plus d'actions** › **Joindre le contrat…**, **Ouvrir le contrat joint** | PARTIEL | AttachAsync InstrumentContractView.cs:485, OpenFileAsync 524. Le sélecteur de fichier n'accepte pas XLSX alors que le service l'accepte (ligne 496) ; l'ancien fichier est « archivé » mais aucune liste ne permet de l'ouvrir. |
| Heures d'appel et d'arrivée | Contrats automates | **Heures d'appel et d'arrivée…** | OUI | RecordTimesAsync InstrumentContractView.cs:535 ; droit Maintenance (ligne 173), pas la comptabilité. |
| Résilier | Contrats automates | case **Contrat en vigueur** dans **Modifier** | OUI | InstrumentContractView.cs:562. |
| Suivi des visites préventives / an | Contrats automates | — | PARTIEL | Le nombre de visites prévues est saisi mais non comparé aux maintenances préventives réalisées. |

## Boutons qui ne mènent à rien

Aucun au 2026-10-17. Les trois cas relevés précédemment (**Réouvrir**, **Annuler l'action**, case **Contrôle après intervention conforme**) sont corrigés.

## Manques à corriger (par impact sur le travail quotidien)

1. **Équipements — pas d'historique (fiche de vie)** des interventions et étalonnages consultable ni imprimable.
2. **Non-conformités — choix du pilote de la fiche et du responsable d'action** absent ; pas de réattribution ni de report d'échéance ; pas de vue complète ni d'impression d'une fiche.
3. **Non-conformités — secrétariat et préleveurs** ne peuvent toujours pas signaler un écart (les techniciens le peuvent désormais).
4. **Documents qualité — pas d'abandon de brouillon ni de refus en revue** ; pas de confirmation de revue périodique sans nouvelle version.
5. **Documents qualité — secrétariat, préleveurs, comptabilité sans accès** aux procédures ni à la prise de connaissance ; pas de liste « qui a lu ».
6. **Équipements — interventions et étalonnages datés du jour uniquement**, étalonnage par paramètre absent, « Conforme sous réserve » ne recale pas l'échéance, échéance de vérification non affichée.
7. **Contrôle interne — période de calcul des limites** limitée à « N derniers jours » ; pas de commentaire par point ; pas d'impression de la carte ; pas de filtre automate/service.
8. **Températures** : correction d'un relevé erroné impossible ; alerte seulement à zéro relevé par jour ; pas de lien avec l'équipement.
9. **Évaluation externe** : pas de correction ni de suppression de campagne, pas de rapport joint ; seuil |Z| = 3 incohérent entre texte et calcul.
10. **Contrats automates** : XLSX refusé par le sélecteur, ancien fichier joint inaccessible, visites préventives non suivies.
