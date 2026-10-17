# Vérification de l'interface — Stocks et achats

Méthode : pour chaque opération offerte par l'application pour ces écrans, recherche du bouton ou du champ qui la déclenche. Contrôle refait le 2026-10-17 sur le code au commit 4e20d0d : chaque ligne a été revue, les numéros de ligne mis à jour, et des lignes ajoutées pour les fonctions apparues depuis 4b2639d.

Légende : **Oui** = bouton présent et relié ; **PARTIEL** = présent mais incomplet ou mal ouvert ; **MANQUANT** = possible dans le logiciel mais aucun bouton ne le déclenche. Les références « fichier:ligne » renvoient au code de l'écran (src/MarocLIS.Desktop/Views) sauf mention contraire.

## Commun à tous les écrans

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Bouton principal au clavier | Stocks, Bons de commande, Demandes de devis, Factures fournisseurs, Inventaires | Ctrl+N | Oui (nouveau) | InventoryView.cs:103, PurchaseOrderView.cs:94, QuoteRequestView.cs:83, SupplierInvoiceView.cs:97, InventoryCountView.cs:113. |
| Recherche dans la liste | tous les écrans du chapitre | zone **Rechercher …**, Ctrl+F | Oui (nouveau) | Filtre instantané de la grille (StockSearchBox). |
| Menus « Plus d'actions » | Stocks, Bons de commande, Demandes de devis, Factures fournisseurs, Consommation, Inventaires | **Plus d'actions** | Oui (nouveau) | InventoryView.cs:264, PurchaseOrderView.cs:92 et 104, QuoteRequestView.cs:102, SupplierInvoiceView.cs:95 et 127, ConsumptionView.cs:83, InventoryCountView.cs:111. |
| Ligne conservée après une action | tous | — | Oui (nouveau) | Rechargement avec `keep` (ex. PurchaseOrderView.cs:300). |

## Fiche article et réception

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Créer / modifier un article (tests par coffret, analyses consommatrices, seuils) | Données de référence › Articles | **Nouvel article**, **Modifier** | PARTIEL | MasterDataView.cs:335, droit ligne 377 → StockItemEditorDialog MasterDataEditors.cs:866. Réservé au droit « gérer les commandes » (Administrateur, Comptabilité) : le biologiste responsable et le responsable qualité ne peuvent pas saisir les données techniques dont dépendent le coût de revient et la consommation. Le type d'article porte toujours le libellé « Désignation » (MasterDataEditors.cs:895). |
| Proposer le seuil calculé | Fiche article | **Appliquer** | Oui | MasterDataEditors.cs:975-983. |
| Voir le stock et les lots | Stocks | tableau des articles, **Lots reçus**, **N'afficher que les articles en alerte**, **En quarantaine** | Oui | Corrigé : colonnes **Type**, **Jours restants**, **Ouvert le** (InventoryView.cs:405 et suivantes) au lieu de « Désignation » et deux « Date ». |
| Réceptionner un lot hors commande | Stocks | **Réceptionner un lot** (Ctrl+N) | Oui | ReceiveAsync InventoryView.cs:636 ; ReceiveLotDialog InventoryView.cs:879. |
| Saisir le prix d'un lot | Stocks › Réceptionner un lot | **Prix** | PARTIEL | InventoryView.cs:984, facultatif, sans indication HT/TTC ni d'unité. Sur commande, le prix vient de la ligne (net de remise). |
| Corriger le prix, la péremption, l'emplacement ou la stabilité d'un lot déjà reçu | — | — | MANQUANT | Aucun écran de modification d'un lot. |
| Stabilité après ouverture à la réception sur commande | Bons de commande › Réceptionner la ligne | **Stabilité après ouverture (jours)** | PARTIEL | Présent en réception directe (InventoryView.cs:992), absent de la réception sur commande (PurchaseOrderReceiptDialog PurchaseOrderView.cs:903). |
| Scanner un DataMatrix GS1 | Réceptions | **Scanner le DataMatrix GS1** | Oui | ReceptionChecksPanel PurchasingQualityDialogs.cs:17 ; changement d'article proposé (InventoryView.cs:1115). |
| Contrôles de réception (température, cases) et non-conformité | Réceptions | **Température à l'arrivée (°C)**, **Conforme à la commande**, **Emballage intact**, **Étiquetage en français**, **Certificat d'analyse reçu** | Oui | PurchasingQualityDialogs.cs:17 et 153-156. |
| Refuser une livraison | Réceptions | **Refuser la livraison** | Oui | InventoryView.cs:1022 ; PurchaseOrderView.cs:963. Le bouton dépend du service de réception, toujours enregistré dans l'application (src/MarocLIS.Application/ApplicationServiceCollectionExtensions.cs:63) : il est donc toujours actif. |
| Réception refusée sur commande pour péremption différente | Bons de commande | **Réceptionner la ligne** | Oui (corrigé) | La quantité refusée ne compte plus comme reçue sur la ligne. |

## Stocks

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Libérer un lot | Stocks | **Libérer** | Oui | ReleaseAsync InventoryView.cs:719 ; actif seulement pour un lot en quarantaine ni refusé ni rappelé (RefreshLotActions ligne 615). |
| Libérer par dérogation au CQ | Stocks | **Libérer** puis **Libération par dérogation** | Oui | Corrigé : la question annonce le même minimum que la règle (15 caractères, ReagentLot.QcOverrideJustificationMinLength ; InventoryView.cs:739). |
| Voir le verdict CQ d'un lot avant de cliquer Libérer | Stocks | — | MANQUANT | Le verdict n'apparaît qu'en cas de refus. |
| Refuser un lot | Stocks | **Refuser** | Oui | RejectAsync InventoryView.cs:761 ; droit réception ou revue du CQ (ligne 216). |
| Annuler un refus | Stocks | **Plus d'actions › Annuler le refus** | PARTIEL | ClearRejectionAsync InventoryView.cs:789. Droit « ajuster le stock » (ligne 219) : Administrateur et Responsable qualité seulement ; pas le biologiste responsable. |
| Rappel fabricant + liste des patients | Stocks | **Plus d'actions › Rappel de lot**, **Résultats produits avec ce lot…**, **Exporter en CSV** | PARTIEL | RecallAsync InventoryView.cs:815 ; ShowRecallListAsync ligne 282 ; RecallListDialog PurchasingQualityDialogs.cs:180. **Rappel de lot** réservé à Administrateur et Responsable qualité. |
| Comparer un lot au précédent | Stocks | **Plus d'actions** ou clic droit › **Comparer avec le lot précédent…** | Oui | CompareLotsAsync InventoryView.cs:305 ; LotComparisonDialog PurchasingQualityDialogs.cs:265. Corrigé : désormais aussi dans **Plus d'actions** (WireLotMenu ligne 232, menu ligne 264). |
| Certificats d'analyse du lot | Stocks | **Plus d'actions** ou clic droit › **Certificats d'analyse…**, **Joindre un fichier…**, **Ouvrir** | Oui | ShowCertificates InventoryView.cs:316 ; LotCertificatesDialog PurchasingQualityDialogs.cs:324. **Joindre un fichier…** exige le droit de réception (ligne 359, nouveau). |
| Sortie de stock (FEFO) | Stocks | **Sortie** | PARTIEL | IssueAsync InventoryView.cs:682. La fenêtre ne demande que la quantité (ni automate destinataire ni motif). Corrigé : une sortie supérieure au stock utilisable est refusée avant de toucher aux lots. |
| Mettre au rebut | Stocks | **Plus d'actions › Mise au rebut** | PARTIEL | WriteOffAsync InventoryView.cs:850. Seulement tout le reste du lot ; Administrateur et Responsable qualité seulement. |
| Ajustement partiel, retour en stock d'une sortie | — | — | MANQUANT | Seule la clôture d'un inventaire corrige une quantité. |
| Historique des mouvements d'un article ou d'un lot | — | — | MANQUANT | Les mouvements sont enregistrés mais aucun écran ne les affiche. |
| Alertes de stock (ruptures prévues, péremptions) | Stocks | **Alertes de stock** | Oui | ShowStockAlertsAsync InventoryView.cs:294. |
| Commande automatique au seuil | Stocks (à l'ouverture et après chaque sortie) | message « Seuil atteint : … » | PARTIEL | InventoryView.cs:448. Les articles sans fournisseur préféré approuvé ne sont pas commandés et ne sont pas signalés. Le demandeur est la personne qui a ouvert l'écran : elle ne peut pas approuver (le bouton lui est maintenant grisé avec une explication). |

## Bons de commande

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Créer une commande | Bons de commande | **Nouvelle commande** (Ctrl+N) | Oui | CreateOrderAsync PurchaseOrderView.cs:498 ; droit ligne 197. |
| Ajouter une ligne | Bons de commande | **Ajouter une ligne** | Oui | AddLineAsync PurchaseOrderView.cs:537. |
| Modifier ou supprimer une ligne d'un brouillon | Bons de commande | — | MANQUANT | Il faut annuler toute la commande. |
| Approuver | Bons de commande | **Approuver** | Oui | ApproveAsync PurchaseOrderView.cs:565 ; avertissement AMMPS/DMP. Nouveau : grisé pour l'auteur de la commande, avec l'info-bulle « Vous avez établi cette commande… » (RefreshButtons lignes 482-485). |
| Envoyer (e-mail ou manuel) | Bons de commande | **Envoyer au fournisseur** | Oui | SendAsync PurchaseOrderView.cs:582. |
| Imprimer / enregistrer le PDF | Bons de commande | **Imprimer le bon (PDF)**, **Plus d'actions › Enregistrer le PDF…** | Oui | PrintPdfAsync PurchaseOrderView.cs:636, SavePdfAsync 656 ; actifs à partir du statut Approuvé (ligne 492). |
| Réceptionner sur commande | Bons de commande | **Réceptionner la ligne** | Oui | ReceiveAsync PurchaseOrderView.cs:690 ; droit ligne 199. |
| Annuler une commande | Bons de commande | **Plus d'actions › Annuler la commande** | Oui | CancelAsync PurchaseOrderView.cs:729. Nouveau : le motif est gardé à part et affiché « Annulée : … » dans **Statut** ; les observations ne sont plus modifiées. |

## Demandes de devis

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Créer une demande de devis | Demandes de devis | **Nouvelle demande de devis** (Ctrl+N) | Oui | CreateAsync QuoteRequestView.cs:514 ; droits lignes 188-191. |
| Envoyer la demande | Demandes de devis | **Envoyer par e-mail**, **Plus d'actions › Marquer envoyée** | Oui | SendByEmailAsync QuoteRequestView.cs:539, MarkSentAsync 564. |
| Ajouter un fournisseur consulté | Demandes de devis | **Ajouter un fournisseur** | Oui | AddSupplierAsync QuoteRequestView.cs:573. |
| Saisir une offre et joindre le devis | Demandes de devis | **Saisir la réponse**, **Joindre le devis reçu…** | Oui | RecordOfferAsync QuoteRequestView.cs:589. Nouveau : délai limité à 0–3650 jours. |
| Ouvrir le devis joint | Demandes de devis | **Plus d'actions › Ouvrir le devis joint** | Oui | OpenFileAsync QuoteRequestView.cs:603. |
| Retenir une offre → bon de commande | Demandes de devis | **Retenir cette offre** | PARTIEL | AwardAsync QuoteRequestView.cs:614. Bouton actif aussi pour un fournisseur non approuvé, qui est ensuite refusé (RefreshButtons ligne 494-507). |
| Réattribuer après annulation de la commande | Demandes de devis | **Retenir cette offre** | Oui (nouveau) | QuoteRequestView.cs:507-508 : une demande attribuée dont la commande a été annulée peut être attribuée à nouveau. |
| Annuler une demande | Demandes de devis | **Plus d'actions › Annuler la demande** | Oui | CancelAsync QuoteRequestView.cs:630. |

## Factures fournisseurs

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Filtrer les factures | Factures fournisseurs | pastilles **Factures en cours**, **En retard**, **À payer sous 7 jours**, **À payer sous 15 jours**, **Bloquées**, **Toutes** | Oui (nouveau) | SupplierInvoiceView.cs:101-102, nombre affiché sur chaque pastille (ligne 403) ; remplacent la liste déroulante. |
| Saisir une facture + rapprochement automatique | Factures fournisseurs | **Saisir une facture** (Ctrl+N) | Oui | NewInvoiceAsync SupplierInvoiceView.cs:505. Nouveau : TVA au-delà de 100 % refusée avec un message clair. |
| Refaire le rapprochement | Factures fournisseurs | **Refaire le rapprochement** | Oui | RematchAsync SupplierInvoiceView.cs:542. |
| Débloquer une facture | Factures fournisseurs | **Débloquer pour paiement** | Oui | UnblockAsync SupplierInvoiceView.cs:557. Nouveau : refusé d'emblée à la personne qui a saisi la facture (ligne 565), avant la demande de justification. |
| Enregistrer un règlement | Factures fournisseurs | **Enregistrer un règlement** | Oui | PayAsync SupplierInvoiceView.cs:579. |
| Joindre / voir le scan | Factures fournisseurs | **Plus d'actions › Joindre le scan**, **Voir le scan** | Oui | AttachAsync SupplierInvoiceView.cs:592, ViewScanAsync 634. |
| Annuler une facture | Factures fournisseurs | **Plus d'actions › Annuler la facture** | Oui | CancelAsync SupplierInvoiceView.cs:645. |
| Corriger une facture ou saisir un avoir fournisseur | — | — | MANQUANT | Seulement annuler (si aucun règlement) et ressaisir. |
| Retards de paiement loi 69-21 | Factures fournisseurs | **Plus d'actions › Retards de paiement (loi 69-21)** | Oui | LatePaymentsAsync SupplierInvoiceView.cs:658. |
| Export comptable | Factures fournisseurs | **Plus d'actions › Export comptable** | PARTIEL | ExportAsync SupplierInvoiceView.cs:688 ; droit « export comptable facturation » (ligne 223) : Administrateur, Comptabilité ; le biologiste responsable ne peut pas. |
| Paramètres d'achat | Factures fournisseurs | **Plus d'actions › Paramètres d'achat** | Oui | SettingsAsync SupplierInvoiceView.cs:709 (Administrateur). Nouveau : pourcentages au-delà de 100 refusés. |

## Évaluation, consommation, inventaires

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Évaluation annuelle | Évaluation fournisseurs | **Appliquer la note proposée**, **Rapport annuel PDF**, **Exporter en CSV** | PARTIEL | ApplyAsync SupplierEvaluationView.cs:194, PrintAsync 214, ExportAsync 227 ; droit ligne 63 (y compris **Actualiser**). Réservé à Administrateur et Comptabilité : le responsable qualité (ISO 15189) n'y a pas accès. Pas de note possible ici pour un fournisseur sans livraison. |
| Consommation théorique / réelle | Consommation | **Actualiser**, **Détail…** (Entrée, double-clic), **Imprimer (PDF)**, **Exporter (CSV)** | Oui | ShowDetail ConsumptionView.cs:218, PrintAsync 225, ExportAsync 238 ; Entrée ligne 138 (nouveau). |
| Paramètres de stock | Consommation | **Plus d'actions › Paramètres de stock…** | Oui | EditSettingsAsync ConsumptionView.cs:244 ; droit ligne 71 (Administrateur). |
| Inventaire complet | Inventaires | **Nouvel inventaire…** (Ctrl+N), **Commencer le comptage**, **Quantité comptée**, **Feuille comptée le**, **Clôturer…**, **Plus d'actions › Annuler** | Oui | CreateAsync InventoryCountView.cs:387, StartAsync 402, SaveLineAsync 465, CloseAsync 530, CancelAsync 542 ; droit ligne 99. Nouveau : **Date** de l'inventaire (au plus aujourd'hui, ligne 622-628). |
| Feuilles et rapport | Inventaires | **Feuille de comptage (PDF)**, **Feuille (CSV)**, **Rapport des écarts (PDF)** | Oui | PrintSheetAsync InventoryCountView.cs:556, PrintVariancesAsync 582. |
| Coût de revient (dépend des lots et des tests par coffret) | Coût de revient et marge | — | PARTIEL | Seuls les articles de type **Réactif** avec **Analyses consommatrices** et **Tests par coffret** sont chiffrés ; le prix de repli « dernier prix d'achat » est le prix catalogue avant remise. |

## Boutons sans effet utile ou qui mènent nulle part

- **Retenir cette offre** reste actif pour une offre de fournisseur non approuvé, puis refuse.
- Les deux points relevés précédemment sont corrigés : les libellés de colonnes de **Stocks** sont justes, et **Refuser la livraison** est toujours actif dans l'application installée.

## Manques à corriger (par impact sur le travail quotidien)

1. Aucun historique des mouvements de stock visible (traçabilité des sorties et rebuts).
2. Le biologiste responsable et les biologistes ne peuvent ni enregistrer un **Rappel de lot**, ni faire une **Mise au rebut**, ni **Annuler le refus** (réservé à Administrateur et Responsable qualité).
3. Pas de correction d'un lot reçu (prix, péremption, emplacement, stabilité) ; prix facultatif et sans unité précisée en réception directe.
4. Lignes d'un bon de commande brouillon impossibles à modifier ou supprimer (y compris les brouillons automatiques).
5. Pas d'ajustement partiel ni de retour en stock ; la **Sortie** n'enregistre ni automate ni motif.
6. Fiche article (tests par coffret, analyses consommatrices, facteurs) réservée à Administrateur et Comptabilité ; type d'article libellé « Désignation ».
7. Stabilité après ouverture absente de la réception sur commande.
8. Commande automatique : articles sans fournisseur non signalés ; le demandeur automatique (celui qui a ouvert l'écran) ne peut pas approuver.
9. **Évaluation fournisseurs** inaccessible au responsable qualité.
10. Pas d'avoir ni de correction de facture fournisseur.
11. Verdict CQ d'un lot non visible avant **Libérer**.
12. **Retenir cette offre** actif pour un fournisseur non approuvé.
