# Vérification de l'interface — Stocks et achats

Légende : **Oui** = bouton présent et relié ; **PARTIEL** = présent mais incomplet ou mal ouvert ; **MANQUANT** = possible dans le logiciel mais aucun bouton ne le déclenche. Les références « fichier:ligne » renvoient au code de l'écran (src/MarocLIS.Desktop/Views).

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Créer / modifier un article (tests par coffret, analyses consommatrices, seuils) | Données de référence › Articles | **Nouvel article**, **Modifier** | PARTIEL | MasterDataView.cs:405-407 → MasterDataEditors.cs:738. Réservé au droit « gérer les commandes » (Administrateur, Comptabilité) : le biologiste responsable et le responsable qualité ne peuvent pas saisir les données techniques (tests par coffret, facteurs, volume mort) dont dépendent le coût de revient et la consommation. |
| Proposer le seuil calculé | Fiche article | **Appliquer** | Oui | MasterDataEditors.cs (bouton apply), proposition calculée à l'ouverture (MasterDataView.cs:1016). |
| Voir le stock et les lots | Stocks | Tableau **Stocks**, **Lots reçus**, **N'afficher que les articles en alerte**, **En quarantaine** | Oui | InventoryView.cs:466, 533. Libellés de colonnes trompeurs : la colonne du type d'article porte l'en-tête « Désignation », celles des jours avant péremption et de la date d'ouverture portent toutes deux « Date » (InventoryView.cs:407, 421, 424). |
| Réceptionner un lot hors commande | Stocks | **Réceptionner un lot** | Oui | InventoryView.cs:618 ; fenêtre ReceiveLotDialog (InventoryView.cs:863). |
| Saisir le prix d'un lot | Stocks › Réceptionner un lot | **Prix** | PARTIEL | InventoryView.cs:961, facultatif, sans indication HT/TTC ni d'unité. Sur commande, le prix vient de la ligne (net de remise), sans champ à la réception. |
| Corriger le prix, la péremption, l'emplacement ou la stabilité d'un lot déjà reçu | — | — | MANQUANT | Aucun écran de modification d'un lot. Un prix oublié ou faux reste faux dans le coût de revient. |
| Stabilité après ouverture à la réception sur commande | Bons de commande › Réceptionner la ligne | **Stabilité après ouverture (jours)** | PARTIEL | Champ présent en réception directe (InventoryView.cs:970), absent de la réception sur commande (PurchaseOrderView.cs:878) : les lots reçus sur commande n'ont jamais de stabilité après ouverture. |
| Scanner un DataMatrix GS1 | Réceptions | **Scanner le DataMatrix GS1** | Oui | PurchasingQualityDialogs.cs:159 (lecture locale ; la fonction de lecture du serveur n'est pas utilisée, sans effet pour l'utilisateur). |
| Contrôles de réception (température, cases) et non-conformité | Réceptions | **Température à l'arrivée (°C)**, **Conforme à la commande**, **Emballage intact**, **Étiquetage en français**, **Certificat d'analyse reçu** | Oui | PurchasingQualityDialogs.cs:118 (Build). |
| Refuser une livraison | Réceptions | **Refuser la livraison** | Oui | InventoryView.cs:1096 ; PurchaseOrderView.cs:935. |
| Libérer un lot | Stocks | **Libérer** | Oui | InventoryView.cs:704. |
| Libérer par dérogation au CQ | Stocks | **Libérer** puis fenêtre **Libération par dérogation** | PARTIEL | InventoryView.cs:722-730. Le message dit « au moins 10 caractères » mais le code en exige 15 (InventoryView.cs:726) : l'utilisateur est renvoyé sans comprendre. |
| Voir le verdict CQ d'un lot avant de cliquer Libérer | Stocks | — | MANQUANT | Le verdict n'apparaît qu'en cas de refus ; la fonction prévue pour l'afficher n'est reliée à aucun écran. |
| Refuser un lot | Stocks | **Refuser** | Oui | InventoryView.cs:745. |
| Annuler un refus | Stocks | **Annuler le refus** | PARTIEL | InventoryView.cs:773. Droit « ajuster le stock » : Administrateur et Responsable qualité seulement ; pas le biologiste responsable. |
| Rappel fabricant + liste des patients | Stocks | **Rappel de lot**, **Résultats produits avec ce lot…**, **Exporter en CSV** | PARTIEL | InventoryView.cs:799, 298 ; PurchasingQualityDialogs.cs:249. **Rappel de lot** réservé à Administrateur et Responsable qualité : ni le biologiste responsable ni les biologistes ne peuvent enregistrer un rappel. |
| Comparer un lot au précédent | Stocks (clic droit) | **Comparer avec le lot précédent…** | Oui | InventoryView.cs:321. Accessible seulement par clic droit (pas de bouton visible). |
| Certificats d'analyse du lot | Stocks (clic droit) | **Certificats d'analyse…**, **Joindre un fichier…**, **Ouvrir** | Oui | InventoryView.cs:332 ; PurchasingQualityDialogs.cs:377, 411. Clic droit seulement. |
| Sortie de stock (FEFO) | Stocks | **Sortie** | PARTIEL | InventoryView.cs:664. Le logiciel sait enregistrer l'automate destinataire et un motif de sortie, mais la fenêtre ne demande que la quantité. |
| Mettre au rebut | Stocks | **Mise au rebut** | PARTIEL | InventoryView.cs:834. Seulement tout le reste du lot ; Administrateur et Responsable qualité seulement (le biologiste responsable ne peut pas jeter un lot périmé). |
| Ajustement partiel, retour en stock d'une sortie | — | — | MANQUANT | Prévu par le logiciel (ajustement positif/négatif, retour) mais aucun bouton ; seule la clôture d'un inventaire corrige une quantité. |
| Historique des mouvements d'un article ou d'un lot | — | — | MANQUANT | Les mouvements (réceptions, sorties, rebuts, ajustements) sont enregistrés mais aucun écran ne les affiche (traçabilité « qui a sorti quoi, quand »). |
| Alertes de stock (ruptures prévues, péremptions) | Stocks | **Alertes de stock** | Oui | InventoryView.cs:310. |
| Commande automatique au seuil | Stocks (à l'ouverture et après chaque sortie) | message « Seuil atteint : … » | PARTIEL | InventoryView.cs:439, 446. Les articles sans fournisseur préféré approuvé ne sont pas commandés et ne sont pas signalés dans ce message (liste calculée mais jamais affichée). Le demandeur de la commande automatique est la personne qui a ouvert l'écran : elle ne pourra pas l'approuver. |
| Créer une commande | Bons de commande | **Nouvelle commande** | Oui | PurchaseOrderView.cs:482. Administrateur, Comptabilité. |
| Ajouter une ligne | Bons de commande | **Ajouter une ligne** | Oui | PurchaseOrderView.cs:521. |
| Modifier ou supprimer une ligne d'un brouillon | Bons de commande | — | MANQUANT | Impossible, y compris sur les brouillons automatiques que l'on est censé « vérifier » : il faut annuler toute la commande. |
| Approuver | Bons de commande | **Approuver** | Oui | PurchaseOrderView.cs:549 ; avertissement AMMPS/DMP. |
| Envoyer (e-mail ou manuel) | Bons de commande | **Envoyer au fournisseur** | Oui | PurchaseOrderView.cs:566. |
| Imprimer / enregistrer le PDF | Bons de commande | **Imprimer le bon (PDF)**, **Enregistrer le PDF…** | Oui | PurchaseOrderView.cs:620, 640. |
| Réceptionner sur commande | Bons de commande | **Réceptionner la ligne** | Oui | PurchaseOrderView.cs:674. |
| Annuler une commande | Bons de commande | **Annuler la commande** | Oui | PurchaseOrderView.cs:713. |
| Créer une demande de devis | Demandes de devis | **Nouvelle demande de devis** | Oui | QuoteRequestView.cs:515. |
| Envoyer la demande | Demandes de devis | **Envoyer par e-mail**, **Marquer envoyée** | Oui | QuoteRequestView.cs:540, 565. |
| Ajouter un fournisseur consulté | Demandes de devis | **Ajouter un fournisseur** | Oui | QuoteRequestView.cs:574. |
| Saisir une offre et joindre le devis | Demandes de devis | **Saisir la réponse**, **Joindre le devis reçu…** | Oui | QuoteRequestView.cs:590, 870. |
| Ouvrir le devis joint | Demandes de devis | **Ouvrir le devis joint** | Oui | QuoteRequestView.cs:604. |
| Retenir une offre → bon de commande | Demandes de devis | **Retenir cette offre** | Oui | QuoteRequestView.cs:615. Bouton actif aussi pour un fournisseur non approuvé, qui est ensuite refusé. |
| Annuler une demande | Demandes de devis | **Annuler la demande** | Oui | QuoteRequestView.cs:631. |
| Saisir une facture + rapprochement automatique | Factures fournisseurs | **Saisir une facture** | Oui | SupplierInvoiceView.cs:471. |
| Refaire le rapprochement | Factures fournisseurs | **Refaire le rapprochement** | Oui | SupplierInvoiceView.cs:508. |
| Débloquer une facture | Factures fournisseurs | **Débloquer pour paiement** | Oui | SupplierInvoiceView.cs:523. |
| Enregistrer un règlement | Factures fournisseurs | **Enregistrer un règlement** | Oui | SupplierInvoiceView.cs:537. |
| Joindre / voir le scan | Factures fournisseurs | **Joindre le scan**, **Voir le scan** | Oui | SupplierInvoiceView.cs:550, 592. |
| Annuler une facture | Factures fournisseurs | **Annuler la facture** | Oui | SupplierInvoiceView.cs:603. |
| Corriger une facture ou saisir un avoir fournisseur | — | — | MANQUANT | Aucun avoir ni correction : seulement annuler (si aucun règlement) et ressaisir. |
| Retards de paiement loi 69-21 | Factures fournisseurs | **Retards de paiement (loi 69-21)** | Oui | SupplierInvoiceView.cs:616. |
| Export comptable | Factures fournisseurs | **Export comptable** | PARTIEL | SupplierInvoiceView.cs:646. Droit « export comptable facturation » : Administrateur, Comptabilité ; le biologiste responsable (qui a l'export comptable général) ne peut pas. |
| Paramètres d'achat | Factures fournisseurs | **Paramètres d'achat** | Oui | SupplierInvoiceView.cs:667 (Administrateur). |
| Évaluation annuelle | Évaluation fournisseurs | **Appliquer la note proposée**, **Rapport annuel PDF**, **Exporter en CSV** | PARTIEL | SupplierEvaluationView.cs:188, 208, 221. Écran réservé à Administrateur et Comptabilité : le responsable qualité (ISO 15189) n'y a pas accès. Pas de note possible ici pour un fournisseur sans livraison. |
| Consommation théorique / réelle | Consommation | **Actualiser**, **Détail…**, **Imprimer (PDF)**, **Exporter (CSV)** | Oui | ConsumptionView.cs:213, 220, 233. |
| Paramètres de stock | Consommation | **Paramètres de stock…** | Oui | ConsumptionView.cs:239 (Administrateur). |
| Inventaire complet | Inventaires | **Nouvel inventaire…**, **Commencer le comptage**, **Quantité comptée**, **Feuille comptée le**, **Clôturer…**, **Annuler** | Oui | InventoryCountView.cs:371, 386, 402, 514, 526. |
| Feuilles et rapport | Inventaires | **Feuille de comptage (PDF)**, **Feuille (CSV)**, **Rapport des écarts (PDF)** | Oui | InventoryCountView.cs:540, 566. |
| Coût de revient (dépend des lots et des tests par coffret) | Coût de revient et marge | — | PARTIEL | Seuls les articles de type **Réactif** avec **Analyses consommatrices** et **Tests par coffret** sont chiffrés ; le prix de repli « dernier prix d'achat » est le prix catalogue avant remise. |

## Boutons sans effet utile ou qui mènent nulle part

- Colonnes « Date » en double et « Désignation » sur la colonne du type d'article dans **Stocks** (libellés faux, pas de fonction cassée).
- **Retenir cette offre** reste actif pour une offre de fournisseur non approuvé, puis refuse.
- **Refuser la livraison** n'est actif que si le contrôle de réception est installé ; dans le cas contraire il est grisé sans explication (à vérifier selon l'installation).

## Manques à corriger (par impact sur le travail quotidien)

1. Aucun historique des mouvements de stock visible (traçabilité des sorties et rebuts).
2. Le biologiste responsable et les biologistes ne peuvent ni enregistrer un **Rappel de lot**, ni faire une **Mise au rebut**, ni **Annuler le refus** (réservé à Administrateur et Responsable qualité).
3. Pas de correction d'un lot reçu (prix, péremption, emplacement, stabilité) ; prix facultatif et sans unité précisée en réception directe : coût de revient faux ou inconnu.
4. Lignes d'un bon de commande brouillon impossibles à modifier ou supprimer (y compris les brouillons automatiques).
5. Pas d'ajustement partiel ni de retour en stock ; la **Sortie** n'enregistre ni automate ni motif.
6. Fiche article (tests par coffret, analyses consommatrices, facteurs) réservée à Administrateur et Comptabilité, pas aux biologistes.
7. Stabilité après ouverture absente de la réception sur commande.
8. Commande automatique : articles sans fournisseur non signalés ; le demandeur automatique (celui qui a ouvert l'écran) ne peut plus approuver.
9. **Évaluation fournisseurs** inaccessible au responsable qualité.
10. Pas d'avoir ni de correction de facture fournisseur.
11. Message de dérogation CQ (10 caractères) contraire à la règle réelle (15).
12. Libellés de colonnes erronés dans **Stocks**.
