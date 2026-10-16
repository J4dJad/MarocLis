# Vérification de l'interface — Qualité et équipements

Méthode : pour chaque opération offerte par l'application pour ces écrans (opérations du métier et leurs règles), recherche du bouton ou du champ qui la déclenche. Les chemins sont relatifs à `src/MarocLIS.Desktop/Views/` sauf mention contraire.

Légende : **OUI** = présent et fonctionnel ; **PARTIEL** = présent mais incomplet, mal restreint ou trompeur ; **MANQUANT** = possible seulement par le code ou les tests ; **INUTILE** = bouton présent qui ne mène à rien.

## Contrôle interne

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Afficher la feuille de saisie | Contrôle interne | (chargement à l'ouverture), **Actualiser** | OUI | QualityControlView.cs:489, 272 |
| Filtrer la feuille par automate ou par service | Contrôle interne | — | **PARTIEL** | La lecture de la feuille accepte un automate et un service, l'écran ne propose aucun filtre (QualityControlView.cs:495 appelle sans filtre). Toutes les cibles du site s'affichent. |
| Saisir une série multiniveau | Contrôle interne | colonne **Résultat**, **Entrée**, **Enregistrer** (Ctrl+S) | OUI | SubmitAsync QualityControlView.cs:710 ; Entrée 294 |
| Commentaire sur un point de contrôle | Contrôle interne | — | **MANQUANT** | L'opération accepte un commentaire par point (affiché ensuite au survol de la carte), aucun champ pour le saisir. |
| Annonce des rejets et des résultats patients bloqués | Contrôle interne | fenêtre après **Enregistrer** | OUI | QualityControlView.cs:771-786 |
| Carte de Levey-Jennings | Contrôle interne | sélection d'une ligne, clic sur un point | OUI | QualityControlView.cs:287-288, 309 ; Controls/LeveyJenningsChart.cs:342. Période fixe de 60 jours, non modifiable. |
| Imprimer la carte de Levey-Jennings | Contrôle interne | — | **MANQUANT** | Le code prévoit l'impression monochrome (formes), mais aucun bouton n'imprime ni n'exporte la carte ; Ctrl+P imprime la grille. |
| Revue d'un contrôle rejeté avec action corrective | Contrôle interne | **Revue du contrôle** | OUI | ReviewSelectedAsync QualityControlView.cs:810 ; 15 caractères minimum |
| Ouvrir une non-conformité depuis la revue | Contrôle interne | question « Ouvrir une fiche de non-conformité pour ce rejet ? » | OUI | Automatique si résultats concernés (QualityControlView.cs:825) |
| Établir les limites du laboratoire | Contrôle interne | **Établir les limites du laboratoire** | PARTIEL | QualityControlView.cs:889. Seul un nombre de jours jusqu'à aujourd'hui est demandé : impossible de choisir une période passée (date de début et de fin), alors que l'opération l'accepte et que le but affiché est d'exclure une période expliquée. |
| Choisir les règles de Westgard | Contrôle interne | **Règles de Westgard**, **Multirègle classique…**, **1₃ₛ seule**, **Enregistrer** | OUI | QualityControlView.cs:860 ; WestgardRuleDialog.cs:97, 101, 105 |
| Évaluer un nouveau lot (saisie) | Contrôle interne | liste du lot, **Automate du lot…**, **Enregistrer** | OUI | QualityControlView.cs:717-744 |
| Démarrer / arrêter l'évaluation d'un lot transmis | Contrôle interne | **Démarrer l'évaluation**, **Arrêter l'évaluation** | OUI | ChangeEvaluationAsync QualityControlView.cs:684 |
| Dérogation à un contrôle rejeté | Validation biologique | (hors de ce chapitre) | OUI | Infrastructure/Queries/ResultQueries.cs:471 ; visible sur la carte (cercle pointillé) |
| Créer matériau et cible de contrôle | Données de référence, onglet Contrôle interne | **Nouveau matériau de contrôle**, **Nouvelle cible de contrôle**, **Modifier** | OUI | MasterDataView.cs:446 (droit Règles du contrôle interne) |
| Droits sur les boutons | Contrôle interne | — | OUI | QualityControlView.cs:283-285 ; le blocage est maintenu même quand la carte réactive **Établir les limites du laboratoire** (ligne 668). |

## Non-conformités

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Registre, filtre ouvertes / catégorie | Non-conformités | case **Ouverte**, liste des catégories, **Actualiser** | OUI | NonConformityView.cs:229-231 |
| Ouvrir une non-conformité | Non-conformités | **Ouvrir une non-conformité** | PARTIEL | NonConformityView.cs:557. Manquent dans la fenêtre : le **Pilote** (toujours l'auteur), le lien vers le dossier, l'échantillon, l'automate, le lot de réactif ou le service. L'opération les accepte. Sans service, la détection de récidive compare toute la catégorie. |
| Déclaration par le technicien ou le secrétariat | Non-conformités | **Ouvrir une non-conformité** | **PARTIEL** | Le droit de gestion des non-conformités n'est donné qu'aux biologistes, au responsable qualité et à l'administrateur. Un technicien voit le registre, bouton grisé ; le secrétariat n'a pas l'écran. Aucun moyen de signaler un écart pour le personnel de paillasse et d'accueil (ISO 15189 attend que tout le personnel puisse signaler). |
| Changer le pilote ou l'échéance d'une fiche | Non-conformités | — | **MANQUANT** | Aucune opération ni bouton pour réattribuer ou reporter. |
| Ajouter une action | Non-conformités | **Ajouter une action** | PARTIEL | NonConformityView.cs:577. Le responsable de l'action est toujours l'utilisateur connecté (envoi d'un identifiant vide, ligne 588) : impossible de confier l'action à un collègue alors que l'opération l'accepte. |
| Démarrer une action | Non-conformités | **Démarrer l'action** | OUI | NonConformityView.cs:752 |
| Solder une action avec preuve | Non-conformités | **Solder l'action** | OUI | NonConformityView.cs:600 |
| Annuler une action | Non-conformités | **Annuler l'action** | PARTIEL | NonConformityView.cs:773. Le bouton est présenté comme le moyen de débloquer une fiche, mais la clôture exige que **toutes** les actions soient « Soldées » : une action annulée bloque définitivement la clôture (« Toutes les actions doivent être soldées avant la clôture. »). Domaine : src/MarocLIS.Domain/Quality/NonConformity.cs:137. |
| Analyse des causes, impact clinique, efficacité | Non-conformités | **Analyse des causes et efficacité** | PARTIEL | NonConformityView.cs:636. La fenêtre ne réaffiche pas ce qui est déjà enregistré ; la catégorie de cause, préremplie sur « Erreur humaine », est réenregistrée à chaque ouverture et écrase le choix précédent si l'on ne la rechoisit pas. Deux champs portent le même libellé « Analyse des causes » (NonConformityView.cs:1101 et 1113). |
| Clôturer | Non-conformités | **Clôturer la fiche** | OUI | NonConformityView.cs:669 ; liste complète des manques affichée |
| Réouvrir une fiche clôturée | Non-conformités | **Réouvrir** | **PARTIEL (inutilisable)** | Le bouton n'est jamais actif : pour une fiche clôturée, LoadDetailAsync appelle SetDetailEnabled(false) (NonConformityView.cs:494), et **Réouvrir** n'est activé que si ce paramètre est vrai (ligne 526). L'opération existe (ReopenAsync ligne 695) mais est injoignable. |
| Écarter un signalement | Non-conformités | **Écarter la fiche** | OUI | NonConformityView.cs:724 |
| Consulter le détail complet (action immédiate, analyse, efficacité, comptes rendus rectifiés) | Non-conformités | — | **MANQUANT** | Le panneau ne montre que description, source, auteur, jours, récidive. L'action immédiate et l'analyse enregistrées ne sont lisibles nulle part à l'écran. |
| Imprimer / exporter une fiche | Non-conformités | — | **MANQUANT** | Seule la grille s'imprime (Ctrl+P). |

## Évaluation externe

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste par année, en attente seulement | Évaluation externe | **Année**, **Évaluations en attente seulement**, **Actualiser** | OUI | EqaView.cs:141-145 |
| Enregistrer une campagne | Évaluation externe | **Enregistrer une campagne** | OUI | EqaView.cs:314 |
| Saisir le résultat transmis | Évaluation externe | **Saisir le résultat transmis** | OUI | EqaView.cs:345 ; actif seulement si « En attente » |
| Saisir l'évaluation, NC automatique | Évaluation externe | **Saisir l'évaluation** | PARTIEL | EqaView.cs:358. Le texte d'aide dit « |Z| ≥ 3 est non satisfaisant », le calcul classe |Z| = 3 en « Douteux » (src/MarocLIS.Domain/Quality/ControlledDocument.cs:340-342). |
| Corriger ou supprimer une campagne mal enregistrée | Évaluation externe | — | **MANQUANT** | Ni modification (programme, campagne, date limite, automate) ni suppression d'une ligne enregistrée par erreur. |
| Joindre le rapport de l'organisateur | Évaluation externe | — | **MANQUANT** | Aucune pièce jointe. |

## Documents qualité

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Registre, documents retirés | Documents qualité | **Afficher aussi les documents retirés**, **Actualiser** | OUI | QualityDocumentsView.cs:184-185 |
| Créer un document | Documents qualité | **Nouveau document** | OUI | QualityDocumentsView.cs:460 |
| Modifier titre, type, périodicité, responsable | Documents qualité | — | **MANQUANT** | Aucune opération de modification après création ; le **Responsable** est toujours le créateur. |
| Nouvelle version (fichier) | Documents qualité | **Nouvelle version**, **Parcourir…** | OUI | QualityDocumentsView.cs:479 |
| Abandonner un brouillon erroné | Documents qualité | — | **MANQUANT** | Un brouillon en cours bloque toute nouvelle version (« Une version de ce document est déjà en cours… ») et ne peut être ni supprimé ni renvoyé ; on ne peut que le mener jusqu'à l'application. |
| Renvoyer une version en revue au rédacteur | Documents qualité | — | **MANQUANT** | Pas de refus de revue / d'approbation. |
| Soumettre, revoir, approuver | Documents qualité | **Soumettre à la revue**, **Enregistrer la revue**, **Approuver** | OUI | QualityDocumentsView.cs:193-198 ; contrôles rédacteur ≠ vérificateur ≠ approbateur dans le domaine |
| Mettre en application | Documents qualité | **Mettre en application** | OUI | QualityDocumentsView.cs:511 |
| Ouvrir le fichier | Documents qualité | **Ouvrir le fichier** | OUI | QualityDocumentsView.cs:531 |
| Prise de connaissance | Documents qualité | **J'ai pris connaissance** | PARTIEL | QualityDocumentsView.cs:556. Réservé aux rôles ayant la consultation qualité : le secrétariat, les préleveurs et la comptabilité n'ont pas l'écran, donc ne peuvent ni lire ni attester les procédures qui les concernent. |
| Voir qui a lu / qui n'a pas lu | Documents qualité | — | **MANQUANT** | Les prises de connaissance sont enregistrées, mais seule la colonne **Lu** de l'utilisateur connecté est visible. |
| Confirmer la revue périodique sans changement | Documents qualité | — | **MANQUANT** | Le message « La revue périodique est dépassée : le document doit être relu et confirmé ou révisé » n'a pas de bouton « confirmer » ; seule une nouvelle version complète remet l'échéance à jour (à vérifier). |
| Retirer / réactiver | Documents qualité | **Retirer le document** / **Réactiver le document** | OUI | QualityDocumentsView.cs:574 |

## Températures

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Saisir un relevé, NC si hors limites | Températures | **Saisir un relevé…**, double-clic | OUI | TemperatureView.cs:65-70, 324 |
| Créer / modifier / désactiver une enceinte | Températures | **Nouvelle enceinte…**, **Modifier l'enceinte…**, case **Actif** | OUI | TemperatureView.cs:71-72, 340 |
| Fiche mensuelle | Températures | **Fiche du mois (PDF)**, **Fiche du mois (CSV)** | OUI | TemperatureView.cs:352 |
| Corriger un relevé erroné | Températures | — | **MANQUANT** | Aucune correction ni annulation motivée ; une faute de frappe hors plage ouvre une NC qu'il faudra écarter. |
| Alerte relevés insuffisants | Températures | colonne **Relevés du jour** | PARTIEL | « Pas de relevé aujourd'hui » ne se déclenche qu'à zéro relevé ; 1 relevé sur 2 attendus n'est pas signalé (TemperatureMonitoringService.cs:27). |
| Lien enceinte ↔ équipement | Températures / Équipements | — | **MANQUANT** | Un réfrigérateur saisi dans les deux écrans n'est pas relié. |

## Équipements

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Registre, filtre échéances | Équipements | **N'afficher que les échéances dépassées…**, **Actualiser** | OUI | EquipmentView.cs:143-144 |
| Enregistrer une intervention | Équipements | **Enregistrer une intervention** | PARTIEL | EquipmentView.cs:331. L'intervention est toujours datée du jour (impossible de saisir une intervention de la veille). Décocher **Contrôle après intervention conforme** ne change pas le statut, alors que le texte dit « l'équipement n'est alors pas remis en service » (EquipmentView.cs:489). Une « Vérification métrologique » ne recale pas l'échéance de vérification. |
| Enregistrer un étalonnage | Équipements | **Enregistrer un étalonnage** | PARTIEL | EquipmentView.cs:351. Étalonnage par paramètre impossible (toujours envoyé sans paramètre, ligne 358) ; daté du jour uniquement. Le texte annonce le lot d'étalon obligatoire aussi pour « Conforme sous réserve », mais ce n'est pas contrôlé (EquipmentView.cs:675), et « Conforme sous réserve » ne recale pas l'échéance : l'automate reste bloqué (à vérifier si voulu). Technicien sans droit d'étalonnage. |
| Changer le statut | Équipements | **Changer le statut** | OUI | EquipmentView.cs:371 |
| Consulter l'historique (fiche de vie) | Équipements | — | **MANQUANT** | Interventions et étalonnages enregistrés ne sont visibles nulle part (sauf interventions des automates sous contrat dans **Contrats automates**). Indispensable au dossier ISO 15189. |
| Créer un équipement, périodicités | Données de référence | **Nouvel équipement**, **Modifier** | OUI | MasterDataView.cs:445 |
| Échéance de vérification métrologique | Équipements | — | **MANQUANT** | Calculée par le domaine, jamais affichée. |

## Contrats automates

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Tableau des contrats et alertes | Contrats automates | **Afficher aussi les contrats résiliés**, **Actualiser** | OUI | InstrumentContractView.cs:162 |
| Créer / modifier un contrat (réactifs captifs, engagement) | Contrats automates | **Nouveau contrat**, **Modifier**, double-clic | OUI | InstrumentContractView.cs:166-175, 449 |
| Joindre / ouvrir le contrat | Contrats automates | **Joindre le contrat…**, **Ouvrir le contrat joint** | PARTIEL | InstrumentContractView.cs:479, 518. Le sélecteur de fichier n'accepte pas XLSX alors que le service l'accepte ; l'ancien fichier est « archivé » mais aucune liste ne permet de l'ouvrir. |
| Heures d'appel et d'arrivée | Contrats automates | **Heures d'appel et d'arrivée…** | OUI | InstrumentContractView.cs:529 ; réservé au droit Maintenance (pas à la comptabilité) |
| Résilier | Contrats automates | case **Contrat en vigueur** dans **Modifier** | OUI | — |
| Suivi des visites préventives / an | Contrats automates | — | **PARTIEL** | Le nombre de visites prévues est saisi mais non comparé aux maintenances préventives réalisées (à vérifier). |

## Boutons qui ne mènent à rien

- **Réouvrir** (Non-conformités) : jamais cliquable (voir ci-dessus).
- **Annuler l'action** (Non-conformités) : fonctionne, mais rend la fiche impossible à clôturer — effet contraire à son but.
- Case **Contrôle après intervention conforme** (Équipements) : enregistrée, sans effet sur le statut malgré le texte.

## Manques à corriger (par impact sur le travail quotidien)

1. **Non-conformités — une action annulée bloque la clôture** : toute fiche dont une action a été annulée ne peut plus être clôturée. Compter « Annulée » comme terminée dans la règle de clôture.
2. **Non-conformités — Réouvrir toujours grisé** : corriger l'activation du bouton pour les fiches clôturées.
3. **Non-conformités — déclaration réservée aux biologistes / qualité** : donner aux techniciens (et au secrétariat) la possibilité de signaler un écart.
4. **Équipements — pas d'historique (fiche de vie)** des interventions et étalonnages consultable ni imprimable.
5. **Non-conformités — choix du pilote de la fiche et du responsable d'action** absent ; pas de réattribution ni de report d'échéance.
6. **Non-conformités — analyse des causes** : fenêtre non préremplie, catégorie de cause écrasée par « Erreur humaine » ; action immédiate et analyse non affichées dans le détail.
7. **Documents qualité — pas d'abandon de brouillon ni de refus en revue** : un brouillon erroné bloque le document ; pas de confirmation de revue périodique sans nouvelle version.
8. **Documents qualité — secrétariat, préleveurs, comptabilité sans accès** aux procédures ni à la prise de connaissance ; pas de liste « qui a lu ».
9. **Équipements — interventions et étalonnages datés du jour uniquement**, étalonnage par paramètre absent, « Conforme sous réserve » laisse l'automate bloqué, case de contrôle après intervention sans effet.
10. **Contrôle interne — période de calcul des limites** limitée à « N derniers jours » ; pas de commentaire par point ; pas d'impression de la carte de Levey-Jennings ; pas de filtre automate/service.
11. **Températures** : correction d'un relevé erroné impossible ; alerte seulement à zéro relevé par jour.
12. **Évaluation externe** : pas de correction/suppression de campagne, pas de rapport joint ; seuil |Z| = 3 incohérent entre texte et calcul.
13. **Contrats automates** : XLSX refusé par le sélecteur, ancien fichier joint inaccessible, visites préventives non suivies.
