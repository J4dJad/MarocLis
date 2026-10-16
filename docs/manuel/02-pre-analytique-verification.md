## Pré-analytique — vérification des boutons et fonctionnalités

Vérification faite sur le code de la branche principale (dernier commit « Sous-traitance : test de bout en bout sur SQL Server et corrections du circuit »). Chaque capacité offerte par l'application pour ce module est rapprochée du bouton qui la déclenche. Fichiers d'écran dans `src/MarocLIS.Desktop/Views/`.

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste des patients à prélever (filtre urgences) | Prélèvements | case **Urgent**, **Actualiser** | Oui | CollectionView.cs:272-273 |
| Enregistrer un prélèvement (volume, jeûne déclaré) | Prélèvements | **Prélever**, colonne **Volume (mL)**, question de jeûne | Oui | CollectionView.cs:279, 678-778 ; droit Prélever (284) |
| Nombre de tentatives, site anatomique, commentaire, lieu de prélèvement | Prélèvements | — | **PARTIEL** | Acceptés par l'application (champs prévus, libellés « Nombre de tentatives », « Site anatomique » présents dans les traductions) mais aucun champ à l'écran : toujours 1 tentative, rien d'autre |
| Expédier des tubes | Prélèvements | **Expédier** | Oui | CollectionView.cs:280 ; prélève d'abord les tubes non prélevés |
| Rattacher une expédition à une tournée de transport | Prélèvements | — | **MANQUANT** | L'expédition accepte un n° de tournée et une série « TR/… » existe, mais aucun écran ne crée ni ne choisit de tournée |
| Réimprimer les étiquettes avec motif | Prélèvements / Consultation d'un dossier | **Imprimer les étiquettes** | Oui | CollectionView.cs:281 ; OrderDetailView.cs:433 |
| Scanner un tube | Réception des échantillons | zone **Scannez le code-barres du tube** (Entrée), double-clic sur **En attente de réception** | Oui | SampleReceptionView.cs:306-311, 323-328 |
| Liste des tubes non reçus | Réception des échantillons | liste **En attente de réception** | Oui | SampleReceptionView.cs:413 |
| Accepter un tube conforme | Réception des échantillons | **Accepter** | Oui | SampleReceptionView.cs:315 |
| Accepter sous réserve avec défauts | Réception des échantillons | **Accepter sous réserve**, cadre **Non-conformités constatées** | Oui | SampleReceptionView.cs:316 |
| Température / chaîne du froid | Réception des échantillons | **Température (°C)**, **Chaîne du froid respectée** | **PARTIEL** | Visibles seulement si une analyse exige la chaîne du froid ; impossible de noter la température d'un autre tube |
| Lieu de conservation à la réception | Réception des échantillons | **Lieu de conservation** | **PARTIEL** | Le lieu est saisi, pas la position (portoir / case) ni de commentaire de réception, pourtant acceptés par l'application |
| Réception par lot | Réception des échantillons | — | PARTIEL | L'application accepte plusieurs tubes d'un coup ; l'écran les traite un par un (choix d'ergonomie acceptable) |
| Refuser avec motif, commentaire, nouveau prélèvement | Réception des échantillons | **Refuser**, liste des motifs, **Demander un nouveau prélèvement** | Oui | SampleReceptionView.cs:317, 762-837 |
| Motif de refus choisi consciemment | Réception des échantillons | liste des motifs | **PARTIEL** | Le premier motif (« Discordance d'identité ») est présélectionné à chaque tube (SampleReceptionView.cs:378, 619) : un refus validé trop vite est compté sous ce motif et fausse l'indicateur qualité. Prévoir une liste vide obligatoire |
| Impression de l'étiquette du tube de remplacement | Réception des échantillons | automatique après **Refuser** | Oui | SampleReceptionView.cs:811-813 |
| Ouvrir une non-conformité à partir d'un tube refusé | Réception des échantillons → Non-conformités | — | **MANQUANT** | Aucun lien depuis la réception. La fiche (**Ouvrir une non-conformité**) n'a aucun champ tube ni dossier, alors que l'application sait les enregistrer (NonConformityView.cs:940-951) |
| Mention des réserves au compte rendu | Comptes rendus | automatique | **PARTIEL** | La ligne imprimée est « code-barres : Haemolysed, Icteric » : les défauts sortent avec leur nom interne anglais, non traduits (ReportReleaseService.cs:693). À corriger avant usage réel |
| Liste des patients à rappeler | Re-prélèvements | liste, recherche, **Actualiser** | Oui | RecollectionView.cs:108-115 |
| Noter un appel | Re-prélèvements | **Noter un appel** | Oui | RecollectionView.cs:118, 252 |
| Noter une venue sur place | Re-prélèvements | — | **PARTIEL** | Le canal « Sur place » existe dans l'application mais l'écran n'envoie que « Appel » |
| SMS de rappel | Re-prélèvements | **Envoyer un SMS** | Oui | RecollectionView.cs:119, 265 ; grisé sans fournisseur SMS (179) |
| Abandonner un re-prélèvement | Re-prélèvements | **Abandonner** | Oui | RecollectionView.cs:120, 278 ; droit Annuler un dossier (124) |
| Accès de l'écran Re-prélèvements | menu | **Re-prélèvements** | PARTIEL | Le Technicien voit l'écran (droit Refuser) mais ses trois boutons sont grisés : liste en lecture seule pour lui |
| Liste de la sérothèque, filtre périmés | Sérothèque | recherche, **Uniquement les tubes dont la durée de conservation est dépassée** | **PARTIEL** | La zone de recherche annonce « patient » mais la recherche ne porte pas sur le nom du patient (SampleQueries.cs:416-420) |
| Ranger / déplacer, date de fin de conservation | Sérothèque | **Ranger dans la sérothèque** (Lieu, Position, Conserver jusqu'au) | Oui | SampleStorageView.cs:152, 309-326 |
| Créer une aliquote | Sérothèque | **Créer une aliquote** | **PARTIEL** | SampleStorageView.cs:153 ; le code-barres de l'aliquote est seulement affiché, aucune étiquette n'est imprimée |
| Éliminer un tube | Sérothèque | **Éliminer le tube** | **PARTIEL** | SampleStorageView.cs:154 ; aucun contrôle que les analyses du tube sont terminées (un tube « Réceptionné » encore en cours peut être éliminé) |
| Purger les tubes périmés | Sérothèque | **Éliminer les tubes périmés** | Oui | SampleStorageView.cs:155 ; porte sur la liste affichée (recherche comprise, 1 000 lignes au plus) |
| Déclarer un laboratoire partenaire | Paramètres, onglet Site | **Type de site** = « Laboratoire correspondant », **Enregistrer** | **PARTIEL** | SettingsView.cs:644 ; on ne peut que modifier un site existant. Bouton « Nouveau site » à venir. Pas de case pour désactiver un partenaire |
| Marquer une analyse sous-traitée + partenaire | Catalogue des analyses | **Sous-traitée**, **Laboratoire partenaire** | Oui | CatalogEditors.cs:109-119, 293-300 |
| Liste à envoyer / en attente / en retard | Sous-traitance | filtre **Tout afficher / À envoyer / En attente de retour / En retard** | Oui | SubcontractingView.cs:151, 251-271 |
| Envoyer + bordereau numéroté par partenaire | Sous-traitance | **Envoyer en sous-traitance**, fenêtre **Envoyer et imprimer** | Oui | SubcontractingView.cs:154, 327-376 |
| Changer de partenaire pour un envoi | Sous-traitance | **Laboratoire partenaire** dans la fenêtre d'envoi | Oui | Un seul partenaire pour tout le lot |
| Réimprimer un bordereau | Sous-traitance | **Réimprimer le bordereau** | Oui | SubcontractingView.cs:155, 378-390 |
| Référence du partenaire | Sous-traitance | **Référence du partenaire** | Oui | SubcontractingView.cs:156, 446-475 |
| Retour des résultats | Saisie des résultats | saisie ordinaire | Oui | Repère « ST » ; retrait de la liste (ResultEntryService.cs:464-468) |
| Accès du Biologiste à la sous-traitance | menu | **Sous-traitance** | PARTIEL | Le rôle Biologiste n'a pas le droit Expédier : il ne voit pas l'écran, même pour consulter les retards |
| Date de retour attendue propre au partenaire | Sous-traitance | **Retour attendu** | PARTIEL | Calculée sur le délai de routine de l'analyse à partir de la réception, pas à partir de l'envoi |
| Consentement « Sous-traitance de l'analyse » | — | — | **MANQUANT** | Le motif de consentement existe dans les listes mais rien ne le demande ni ne le contrôle à l'envoi |
| Origine « Sous-traitance reçue » | Dossiers | **Origine** | Oui | OrderRegistrationView.cs:436 ; libellé seulement, aucun effet |
| Prescripteur « Laboratoire correspondant » | Données de référence, Prescripteurs | liste de types | **PARTIEL** | La liste est intitulée « Désignation » au lieu d'un libellé de type (MasterDataEditors.cs:1123) |
| Organisme « Établissement adresseur » | Données de référence, Organismes payeurs | **Nouvel organisme payeur**, **Type d'organisme** | Oui | Organisme « Laboratoire adresseur » livré (100 %) ; facturation complète à vérifier par le test en cours |
| Convention de sous-traitance | Visites et conventions | **Nouvelle convention**, **Type de convention** « Sous-traitance » | Oui | Fiche de suivi ; la **Remise convenue (%)** n'est appliquée à aucun prix |

**Boutons qui existent mais ne mènent nulle part ou trompent**

- Zone de recherche de la **Sérothèque** : annonce « patient », ne cherche pas le nom du patient.
- **Re-prélèvements** pour le Technicien : écran visible, tous les boutons grisés.
- Liste des motifs de refus : présélection silencieuse de « Discordance d'identité ».

**Manques à corriger** (par ordre d'impact sur le travail quotidien)

1. Compte rendu : les réserves pré-analytiques s'impriment avec les noms internes anglais des défauts (« Haemolysed ») au lieu du français.
2. Refus : motif présélectionné « Discordance d'identité » ; imposer un choix explicite.
3. Sous-traitance : impossible de créer un laboratoire partenaire depuis **Paramètres** (bouton « Nouveau site » à venir).
4. Sérothèque : élimination possible d'un tube dont les analyses ne sont pas terminées ; ajouter un contrôle ou un avertissement.
5. Non-conformités : pas de passage depuis la réception et pas de champ tube / dossier sur la fiche.
6. Aliquote : pas d'impression d'étiquette pour le nouveau code-barres.
7. Sérothèque : recherche par nom de patient absente malgré le texte d'aide.
8. Prélèvements / réception : champs prévus mais absents (tentatives, site anatomique, commentaire, position de rangement, température hors chaîne du froid).
9. Sous-traitance : écran invisible pour le Biologiste ; date de retour calculée depuis la réception et non depuis l'envoi.
10. Tournées de transport, canal « Sur place » des re-prélèvements, consentement à la sous-traitance : prévus dans l'application, inaccessibles.
11. Fenêtre prescripteur : la liste des types est intitulée « Désignation ».
