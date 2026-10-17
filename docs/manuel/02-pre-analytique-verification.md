## Pré-analytique — vérification des boutons et fonctionnalités

Revérifié le 2026-10-17 au commit 4e20d0d (première vérification au commit 4b2639d). Chaque capacité offerte par l'application pour ce module est rapprochée du bouton qui la déclenche ; chaque ligne a été recontrôlée dans le code actuel. Références « fichier:ligne » : écrans dans `src/MarocLIS.Desktop/Views/`, règles dans `src/MarocLIS.Application/`. Libellés vérifiés dans `src/MarocLIS.Localization/Resources/fr.json`.

Légende : **OUI** = présent et utilisable ; **PARTIEL** = présent mais incomplet ou mal réglé ; **MANQUANT** = prévu par l'application mais sans bouton.

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Liste des patients à prélever, filtres | Prélèvements | puces **Tous**, **Urgents**, **À jeun requis**, bouton **Actualiser** | OUI | CollectionView.cs:904-908 ; la case « Urgent » est remplacée par les puces |
| Enregistrer un prélèvement (volume, jeûne déclaré) | Prélèvements | **Valider le prélèvement** (F12), colonne **Volume (mL)**, bandeau **Oui / Non / Ne sait pas** | OUI | CollectionView.cs:763, 1442-1531 ; droit Prélever (720) ; question de jeûne seulement sans réponse ni déclaration antérieure (1498-1513) |
| Cocher un tube en le scannant | Prélèvements | **Scannez le tube pour le cocher** | OUI | CollectionView.cs:784, 1384-1414 (nouveau) |
| Lieu de prélèvement et commentaire | Prélèvements | **Lieu de prélèvement**, **Commentaire** | OUI | CollectionView.cs:789-791, 816-831, 1529-1530 (corrigé : transmis au service) |
| Nombre de tentatives, site anatomique | Prélèvements | — | **PARTIEL** | Toujours aucun champ à l'écran : toujours 1 tentative, pas de site anatomique |
| Ouvrir le dossier depuis le prélèvement | Prélèvements | **Voir le dossier** | OUI | CollectionView.cs:766 (nouveau) |
| Expédier des tubes | Prélèvements | **Expédier au plateau** | OUI | CollectionView.cs:764, 1554-1566 ; droit Expédier (721) ; prélève d'abord les tubes non prélevés |
| Rattacher une expédition à une tournée de transport | Prélèvements | — | **MANQUANT** | L'expédition accepte un n° de tournée et une série « TR/… » existe, mais aucun écran ne crée ni ne choisit de tournée |
| Réimprimer les étiquettes avec motif | Prélèvements / Consultation d'un dossier | **Imprimer les étiquettes** (F8) | OUI | CollectionView.cs:765, 1591 ; OrderDetailView.cs:116, 352 |
| Disposition : carte de scan en haut, liste en attente visible dès l'ouverture | Réception des échantillons | carte **Scanner le tube**, carte **En attente de réception** | OUI | SampleReceptionView.cs:155, 191 (carte ancrée en haut, hauteur calculée) |
| Scanner un tube | Réception des échantillons | zone **Scannez le code-barres du tube** (Entrée), double-clic sur **En attente de réception** | OUI | SampleReceptionView.cs:840, 725 |
| Liste des tubes non reçus, filtres | Réception des échantillons | liste **En attente de réception**, puces **Tous**, **Urgents**, **Délai long** | OUI | SampleReceptionView.cs:853, 977-979 |
| Accepter un tube conforme | Réception des échantillons | **Accepter** (Entrée) | OUI | SampleReceptionView.cs:847, 1250 |
| Accepter sous réserve avec défauts | Réception des échantillons | **Accepter sous réserve**, boutons **Non-conformités constatées · cliquer pour signaler** | OUI | SampleReceptionView.cs:842, 849, 1251 |
| Température / chaîne du froid | Réception des échantillons | **Température (°C)**, **Chaîne du froid respectée** | **PARTIEL** | SampleReceptionView.cs:843-844, 1298-1303 ; visibles seulement si une analyse exige la chaîne du froid ; impossible de noter la température d'un autre tube |
| Lieu de conservation à la réception, liste fermée | Réception des échantillons | **Lieu de conservation** | **PARTIEL** | SampleReceptionView.cs:845 ; liste des lieux actifs (SampleWorkflowService.cs:952). Toujours pas de position (portoir / case) ni de commentaire de réception |
| Réception par lot | Réception des échantillons | — | PARTIEL | L'application accepte plusieurs tubes d'un coup ; l'écran les traite un par un (choix d'ergonomie acceptable) |
| Refuser avec motif, commentaire, nouveau prélèvement | Réception des échantillons | **Refuser**, **Motif du refus**, **Demander un nouveau prélèvement** | OUI | SampleReceptionView.cs:850-852, 1365-1420 ; droit Refuser (723) |
| Motif de refus choisi consciemment | Réception des échantillons | **Motif du refus** | OUI | Corrigé : aucune présélection (SampleReceptionView.cs:869, 1208), refus sans motif bloqué (1365) |
| Refus bloqué si un résultat est déjà sur un compte rendu | Réception des échantillons | **Refuser** | OUI | SampleWorkflowService.cs:578 (nouveau) |
| Impression de l'étiquette du tube de remplacement | Réception des échantillons | automatique après **Refuser** | OUI | SampleReceptionView.cs:1440-1451 |
| Ouvrir une non-conformité à partir d'un tube refusé | Réception des échantillons → Non-conformités | — | **MANQUANT** | Aucun lien depuis la réception ; la fiche **Ouvrir une non-conformité** n'a toujours ni champ tube ni dossier |
| Mention des réserves au compte rendu | Comptes rendus | automatique | OUI | Corrigé : les défauts sont nommés dans la langue du compte rendu (ReportReleaseService.cs:95-102) |
| Liste des lieux de stockage (ajouter, renommer, désactiver) | Paramètres, onglet **Lieux de stockage** | **Ajouter**, **Renommer**, **Désactiver** / **Réactiver** | OUI | SettingsView.cs:812-816, 891, 1472-1540 (nouveau) ; droit Gérer les paramètres (175, 749) |
| Liste des patients à rappeler, filtres | Re-prélèvements | recherche, puces **Tous**, **Pas encore contacté**, **À rappeler en priorité**, **Actualiser** | OUI | RecollectionView.cs:57-59, 174-176, 275-278 |
| Noter un appel | Re-prélèvements | **Noter un appel** | OUI | RecollectionView.cs:178, 329-340 |
| Noter une venue sur place | Re-prélèvements | — | **PARTIEL** | Le canal « Sur place » existe dans l'application mais l'écran n'envoie que « Appel » (RecollectionView.cs:339) |
| SMS de rappel | Re-prélèvements | **Envoyer un SMS** | OUI | RecollectionView.cs:179, 343-353 ; grisé sans fournisseur SMS |
| Abandonner un re-prélèvement | Re-prélèvements | **Abandonner** | OUI | RecollectionView.cs:180, 356-367 ; droit Annuler un dossier (138) |
| Accès de l'écran Re-prélèvements | menu | **Re-prélèvements** | PARTIEL | Le Technicien voit l'écran (droit Refuser) mais ses boutons sont grisés (RecollectionView.cs:137-138) : liste en lecture seule pour lui |
| Liste de la sérothèque, filtres | Sérothèque | recherche « Code-barres, dossier, patient ou emplacement », puces **Tous**, **Conservation dépassée**, **Non rangé** | OUI | SampleStorageView.cs:69-71, 210, 313-316 ; corrigé : la recherche trouve le nom du patient (Infrastructure/Queries/SampleQueries.cs:424-436) |
| Ranger / déplacer, date de fin de conservation | Sérothèque | **Ranger dans la sérothèque** (Lieu de conservation, Position, Conserver jusqu'au) | OUI | SampleStorageView.cs:215, 397, 544-635 ; lieu dans la liste fermée ; date passée refusée (SampleWorkflowService.cs:797) |
| Créer une aliquote | Sérothèque | **Créer une aliquote** | **PARTIEL** | SampleStorageView.cs:216, 416-420, 672-743 ; le code-barres de l'aliquote est seulement affiché, aucune étiquette n'est imprimée |
| Éliminer un tube | Sérothèque | **Éliminer le tube** | OUI | Corrigé : refus si des analyses attendent un résultat (SampleWorkflowService.cs:844) ; SampleStorageView.cs:217, 449-463 |
| Purger les tubes périmés | Sérothèque | **Plus d'actions** › **Éliminer les tubes périmés** | OUI | SampleStorageView.cs:212, 218, 482-486 ; porte sur la liste affichée (1 000 lignes au plus) |
| Déclarer un laboratoire partenaire | Paramètres, onglet Site | **Nouveau site**, **Type de site** = « Laboratoire correspondant » | **PARTIEL** | Corrigé : bouton **Nouveau site** (SettingsView.cs:837, 1202-1237). Toujours pas de case pour désactiver un partenaire |
| Marquer une analyse sous-traitée + partenaire | Catalogue des analyses | **Sous-traitée**, **Laboratoire partenaire** | OUI | CatalogEditors.cs:157-167 |
| Liste à envoyer / en attente / en retard | Sous-traitance | puces **Tout afficher**, **À envoyer**, **En attente de retour**, **En retard** | OUI | SubcontractingView.cs:70, 339-347 (la liste déroulante est remplacée par des puces) |
| Envoyer + bordereau numéroté par partenaire | Sous-traitance | **Envoyer en sous-traitance**, fenêtre **Envoyer et imprimer** | OUI | SubcontractingView.cs:224, 375-406, 578 |
| Changer de partenaire pour un envoi | Sous-traitance | **Laboratoire partenaire** dans la fenêtre d'envoi | OUI | Un seul partenaire pour tout le lot |
| Réimprimer un bordereau | Sous-traitance | **Réimprimer le bordereau** | OUI | SubcontractingView.cs:225, 426-441 |
| Référence du partenaire | Sous-traitance | **Plus d'actions** › **Référence du partenaire** | OUI | SubcontractingView.cs:46, 226, 496-515 |
| Activation des boutons selon les lignes cochées | Sous-traitance | **Envoyer…**, **Réimprimer…**, **Référence…** | OUI | SubcontractingView.cs:190-194 (nouveau) |
| Retour des résultats | Saisie des résultats | saisie ordinaire | OUI | Repère « ST » ; l'analyse quitte la liste dès qu'un résultat est saisi |
| Accès du Biologiste à la sous-traitance | menu | **Sous-traitance** | OUI | Corrigé : nouveau droit Sous-traiter (Domain/Identity/Permissions.cs:47 ; MainShellForm.cs:404), donné au Biologiste, au Technicien et au Biologiste responsable ; le Préleveur ne voit plus l'écran |
| Date de retour attendue propre au partenaire | Sous-traitance | **Retour attendu** | PARTIEL | Toujours calculée sur le délai de routine de l'analyse à partir de la réception, pas à partir de l'envoi |
| Consentement « Sous-traitance de l'analyse » | — | — | **MANQUANT** | Le motif de consentement existe dans les listes mais rien ne le demande ni ne le contrôle à l'envoi (SubcontractingService.cs) |
| Origine « Sous-traitance reçue » | Dossiers | **Origine** | OUI | OrderRegistrationView.cs:1149 ; libellé seulement, aucun effet |
| Prescripteur « Laboratoire correspondant » | Données de référence, Prescripteurs | liste de types | **PARTIEL** | La liste est toujours intitulée « Désignation » au lieu d'un libellé de type (MasterDataEditors.cs:1291) |
| Organisme « Établissement adresseur » | Données de référence, Organismes payeurs | **Nouvel organisme payeur**, **Type d'organisme** | OUI | Organisme « Laboratoire adresseur » livré (100 %) |
| Convention de sous-traitance | Visites et conventions | **Nouvelle convention**, **Type de convention** « Sous-traitance » | OUI | Fiche de suivi ; la **Remise convenue (%)** n'est appliquée à aucun prix |

**Boutons qui existent mais ne mènent nulle part ou trompent**

- **Re-prélèvements** pour le Technicien : écran visible, tous les boutons grisés.
- Le message « Les tubes cochés sont déjà prélevés : utilisez « Expédier ». » cite encore l'ancien libellé : le bouton s'appelle maintenant **Expédier au plateau**.

(Corrigés depuis la première vérification : la recherche de la sérothèque trouve le nom du patient ; le motif de refus n'est plus présélectionné.)

**Manques à corriger** (par ordre d'impact sur le travail quotidien)

1. Non-conformités : pas de passage depuis la réception et pas de champ tube / dossier sur la fiche.
2. Aliquote : pas d'impression d'étiquette pour le nouveau code-barres.
3. Réception : position de rangement, commentaire de réception et température hors chaîne du froid absents.
4. Prélèvements : nombre de tentatives et site anatomique absents.
5. Sous-traitance : date de retour calculée depuis la réception et non depuis l'envoi ; pas de désactivation d'un partenaire.
6. Tournées de transport, canal « Sur place » des re-prélèvements, consentement à la sous-traitance : prévus dans l'application, inaccessibles.
7. Fenêtre prescripteur : la liste des types est intitulée « Désignation ».
8. Message « … utilisez « Expédier ». » à mettre au nouveau libellé « Expédier au plateau ».
