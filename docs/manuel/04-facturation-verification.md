# Vérification de l'interface — Facturation, Caisse, Tiers payant, Visa des caisses

Re-vérifié le 2026-10-17 au commit 4e20d0d, puis complété le même jour pour les changements du cycle 5 (état du code au commit 1bbef5b). Méthode : chaque opération offerte par l'application pour ce module (facturation, caisse, tiers payant, visa, format d'impression des factures) a été rapprochée du bouton ou du champ qui la déclenche, et chaque libellé comparé à `fr.json`. Chaque ligne de la vérification précédente a été recontrôlée : les lignes corrigées passent à **OUI** avec la nouvelle référence ; des lignes ont été ajoutées pour les nouveautés. Les numéros de ligne renvoient aux fichiers d'écran (`BillingView.cs`, `BillingCorrectionDialogs.cs`, `CashSessionView.cs`, `ClaimBatchView.cs`, `CashSessionReviewView.cs`, `SettingsView.InvoicePrint.cs` dans `src/MarocLIS.Desktop/Views/`) et, pour les règles, à `src/MarocLIS.Application/Billing/BillingService.cs`.

Légende : **OUI** = présent et utilisable ; **PARTIEL** = présent mais incomplet ou mal réglé ; **MANQUANT** = possible seulement par le code ; **INUTILE** = présent mais sans effet utile.

## Facturation

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les actes non facturés | Facturation | « Actes non facturés » | OUI | `LoadPendingAsync` BillingView.cs:524. Limité aux 500 plus récents, signalé dans le titre. |
| Garder le dossier sélectionné après une action | Facturation | « Actes non facturés », **Actualiser** | OUI | Corrigé (cycle 5, ff979b4) : `RenderPending` BillingView.cs:552, 584-587 ; avant, la liste revenait à la première ligne après un acompte, une facture ou F5 |
| Lister factures et avoirs | Facturation | « Factures », recherche, filtres, **Actualiser** | PARTIEL | Toujours : `LoadLedgerAsync` BillingView.cs:596 ne charge que la page 1 (50 lignes, :602). Aucune page suivante : au-delà, passer par la recherche. |
| Filtrer par statut | Facturation | filtres **Toutes · N**, **Émise · N**, **Partiellement réglée · N**, **Réglée · N**, **Créance abandonnée · N** | OUI | Corrigé : les filtres remplacent la liste qui portait l'option « Tout sélectionner » (BillingView.cs:168-196). |
| Filtrer les créances abandonnées | Facturation | filtre **Créance abandonnée** | INUTILE | Toujours : aucune opération ne met une facture dans ce statut ; le filtre reste à 0. |
| Voir le type de document | Facturation | colonne **Type** | OUI | Corrigé : l'ancienne colonne « Désignation » s'appelle maintenant **Type** ; colonne **Total** ajoutée. |
| Voir le statut tiers payant et la part organisme d'une facture | Facturation | — | MANQUANT | Toujours : ni colonne « Part organisme » ni statut tiers payant dans la liste des factures. |
| Émettre une facture (un ou plusieurs dossiers) | Facturation | **Émettre la facture** + fenêtre **Émettre la facture** | OUI | `IssueAsync` BillingView.cs:694 ; `InvoiceIssueDialog` BillingCorrectionDialogs.cs:269. |
| Remise à l'émission | Facturation | **Remise (DH ou %)**, **Motif de la remise** | OUI | Visibles seulement avec le droit remise. Montant ou pourcentage du brut (« 10 % ») : `EditorValues.ParseDiscount` (f26ed5b) ; la ligne du net affiche la remise en DH pour un pourcentage. |
| Plafonner la remise, approbation au-delà | Facturation | ligne du net « Au-delà du plafond (10 % = 74,70 DH) : approbation requise », **Identifiant de l'approbateur**, **Mot de passe**, **Émettre la facture** | OUI | Nouveau (remise). Plafond lu dans **Paramètres › Factures** (BillingView.cs:779) ; section d'approbation dépliée seulement au-delà (`ShowApproval` BillingCorrectionDialogs.cs:490, message :526, contrôle :580) ; refus d'approbation : la fenêtre revient avec la remise et le motif, mot de passe vidé (BillingView.cs:816, `ApprovalRefused` BillingCorrectionDialogs.cs:314). Contrôle aussi dans le service : `IssueInvoiceAsync` BillingService.cs:385 et :479, approbateur vérifié par `ApproveDiscountAsync` :526 et `AuthenticationService.VerifyApproverAsync` (sans connexion, échecs comptés comme à la connexion), audit `DescribeApproval` :553. |
| Refacturer à l'organisme après avoir | Facturation | question « Adresser cette facture au tiers payant … ? » | OUI | BillingView.cs:755. |
| Choisir librement l'organisme destinataire | Facturation | — | MANQUANT | Toujours : l'organisme de remplacement n'est proposé que dans le cas de refacturation ci-dessus. |
| Encaisser un règlement (total ou partiel) | Facturation | **Règlement** → **Mode de règlement**, **Montant**, **Référence**, **Banque**, **Échéance**, **Enregistrer** | OUI | `RecordPaymentAsync` BillingView.cs:1114 ; `PaymentDialog` :1419 ; champs de référence :1581-1597. Nouveau : message « FAC… — Réglée » avec le numéro (:1130) ; montants saisis avec espace des milliers acceptés (« 1 250,00 »). |
| Note libre sur un règlement | Facturation | — | MANQUANT | Toujours : la fenêtre envoie une note vide (BillingView.cs:~1693). |
| Paiement mobile | Facturation | **Mode de règlement** = « Paiement mobile » | PARTIEL | Corrigé en partie : compté dans le détail de la caisse et dans le visa (CashSessionView.cs:318-319, TreasuryQueries.cs:568). Reste : aucune référence demandée (BillingView.cs:1515). |
| Imprimer la facture | Facturation | **Imprimer la facture** | OUI | `PrintAsync` BillingView.cs:1330 ; format choisi dans **Paramètres › Factures**. |
| Imprimer dans l'autre format | Facturation | **Plus d'actions** › choix du format (format habituel / présentation par cotation B) | OUI | Nouveau. PrintTarget / UpdatePrintButtons BillingView.cs:1300-1323 ; la présentation par cotation est grisée sur un avoir. Mise en page décrite d'après le code, à vérifier visuellement. |
| Facture « Cotation (B) » : acte facturé seulement en DH | Facturation | **Imprimer la facture** (format cotation B, ou format existant avec la colonne cotation) | OUI | Corrigé (cycle 5, ff979b4) : Application/Billing/InvoicePrintFormat.cs:209 et Reporting/QuestPdfInvoiceRenderer.cs:347, une cotation n'est imprimée que si le coefficient est supérieur à 0 ; avant, « 0 » (ou « B 0 ») sans montant sur la ligne. La colonne du prix en DH s'affiche alors (InvoicePrintFormat.cs:216) |
| Régler le format par défaut des factures | Paramètres › Factures | options du format, **Enregistrer** | OUI | Nouveau. SettingsView.InvoicePrint.cs:93 (droit gérer les paramètres), enregistrement :166, message :176. |
| Régler le plafond des remises | Paramètres › Factures | **Pourcentage maximal du brut (%)**, **Montant maximal (DH)**, **Enregistrer** | OUI | Nouveau (remise). SettingsView.InvoicePrint.cs:135 (droit gérer les paramètres), lecture/contrôle :197, enregistrement :264 ; service `SaveDiscountCapAsync` InvoicePrintFormat.cs:160 (droit, bornes, audit). |
| Imprimer le reçu de règlement | Facturation | **Imprimer le reçu** | OUI | BillingView.cs:357-362, :1330. |
| Rembourser un règlement | Facturation | **Plus d'actions** › **Rembourser** → choix du règlement → motif | OUI | `CorrectPaymentAsync` BillingView.cs:808-816 ; `PaymentPickerDialog` BillingCorrectionDialogs.cs:19. Nouveau : seuls les règlements du patient sont remboursables (BillingService.cs:986) ; un remboursement en espèces ne peut pas dépasser les espèces attendues de la caisse (:758, :1000). Réservé au droit avoir. |
| Fenêtre de choix d'un règlement lisible | Facturation | colonnes **Date** … **Créé par** | OUI | Corrigé (cycle 5, ff979b4) : BillingCorrectionDialogs.cs:36, 68 (largeur 880) ; avant, **Créé par** était coupé avec un défilement horizontal. Fenêtre **Règlement** : libellés **Référence**, **Banque**, **Échéance** au style des autres champs (BillingView.cs:1443-1445, 71b58ad) |
| Déclarer un chèque / effet impayé | Facturation | **Plus d'actions** › **Déclarer impayé** | OUI | BillingView.cs:808-816 (mode impayé). |
| Émettre un avoir total | Facturation | **Facture d'avoir** + motif (10 caractères min.) | OUI | `IssueCreditNoteAsync` BillingView.cs:1165. |
| Émettre un avoir partiel (montant choisi) | Facturation | — | MANQUANT | Toujours : l'écran n'envoie que le motif ; toujours avoir du solde complet. |
| Imprimer un avoir | Facturation | **Imprimer la facture** sur la ligne de l'avoir | OUI | Même bouton, format habituel seulement (cotation grisée, :1300-1323). |
| Encaisser un acompte | Facturation | **Encaisser un acompte** | OUI | `TakeDepositAsync` BillingView.cs:941. |
| Imprimer le reçu d'acompte (REC/…) | Facturation | — | MANQUANT | Toujours : numéro REC seulement dans la barre d'état. |
| Voir le détail des acomptes d'un dossier | Facturation | — | PARTIEL | Toujours : seul le total détenu (colonne **Acompte**). |
| Rembourser les acomptes d'un dossier | Facturation | **Rembourser l'acompte** | OUI | `RefundDepositAsync` BillingView.cs:997. Tous les acomptes du dossier à la fois. Droit avoir requis. |
| Transmettre au tiers payant (y compris retransmission d'un rejet) | Facturation | **Plus d'actions** › **Transmettre au tiers payant** + confirmation | OUI | Corrigé : une question de confirmation est posée (`SubmitClaimAsync` BillingView.cs:1234, :1253). |
| Enregistrer le retour d'un organisme sur des factures | Facturation | **Plus d'actions** › **Enregistrer le retour** | OUI | `RecordClaimOutcomeAsync` BillingView.cs:889 ; `ClaimOutcomeDialog` BillingCorrectionDialogs.cs:131. Corrigé : le choix « Réglé » n'est plus proposé (:182-183, BillingService.cs:1095). |
| Saisir le montant accepté par l'organisme | Facturation | **Plus d'actions** › **Montant accepté par l'organisme** → **Montant accepté**, **Référence du virement ou bordereau**, **Laisser à la charge du patient (reste sur la facture)**, **Passer en perte**, **Motif communiqué par l'organisme** | OUI | `RecordPayerSettlementAsync` BillingView.cs:1065 ; `PayerSettlementDialog` BillingCorrectionDialogs.cs:488. Une facture à la fois. Nouveau message « Reste dû par l'organisme ». |
| Abandonner une créance patient irrécouvrable | Facturation | — | MANQUANT | Toujours. |
| Devis | Facturation | — | MANQUANT (ici) | Le devis en direct existe désormais à l'enregistrement (chapitre Accueil), mais aucun document « Devis » ne peut être émis ni imprimé. |

## Caisse

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Ouvrir la caisse | Caisse | **Fond de caisse**, **Ouvrir la caisse** | OUI | `OpenSessionAsync` CashSessionView.cs:394 ; droits CashSessionView.cs:235-236. |
| Voir les encaissements du poste par mode | Caisse | **Détail de la session** (Fond de caisse, Espèces, Chèque, Carte bancaire, Virement, Effet / traite, Paiement mobile, Remboursements, Prélèvements, Espèces attendues) | PARTIEL | Corrigé en partie : Effet / traite et Paiement mobile apparaissent (CashSessionView.cs:318-319). Reste : totaux seulement, pas la liste des règlements du poste. |
| Encaisser depuis la caisse | Caisse | — | PARTIEL | Toujours : encaissement dans **Facturation** seulement. Cohérent mais à savoir. |
| Prélèvement de caisse | Caisse | **Prélèvement de caisse** → montant → motif | OUI | `WithdrawAsync` CashSessionView.cs:438. Nouveau : refusé s'il dépasse les espèces attendues (BillingService.cs:1063). |
| Voir / corriger les prélèvements déjà faits | Caisse | — | MANQUANT | Toujours : total seulement ; pas d'annulation. |
| Clôturer avec comptage et explication d'écart | Caisse | **Espèces comptées**, **Écart de caisse**, **Explication de l'écart**, **Clôturer la caisse** | OUI | `CloseSessionAsync` CashSessionView.cs:471. Tolérance 10 DH. Une clôture refusée n'enregistre pas le comptage. |
| Imprimer le rapport de clôture (ticket Z) | Caisse | — | MANQUANT | Toujours. |
| Clôturer la caisse d'un collègue absent | Caisse | — | MANQUANT | Toujours : seul le titulaire peut clôturer sa caisse. |

## Tiers payant

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir les factures à transmettre et ce qui les bloque | Tiers payant | « À transmettre », colonne **Ce qui bloque** | OUI | `ActivateAsync` ClaimBatchView.cs:341 ; fenêtre de dates :331. |
| Cocher les factures transmissibles d'un organisme | Tiers payant | **Cocher les factures transmissibles de cet organisme** | OUI | Corrigé : coche un seul organisme (`SelectClaimable` ClaimBatchView.cs:610) ; message si la sélection mélange des organismes :647. |
| Garder la facture sélectionnée après Actualiser | Tiers payant | « À transmettre », **Actualiser** | OUI | Corrigé (cycle 5, ff979b4) : ClaimBatchView.cs:370, 397-400 |
| Transmettre un bordereau | Tiers payant | **Transmettre au tiers payant** + confirmation | OUI | `SubmitAsync` ClaimBatchView.cs:676. Grisé sans le droit export (:664-671). |
| Imprimer / exporter le bordereau à envoyer | Tiers payant | — | MANQUANT | Toujours : aucun document ni fichier pour l'organisme. |
| Voir les lots et leur contenu | Tiers payant | « Lots transmis », « Contenu du lot » | OUI | ClaimBatchView.cs |
| Enregistrer le retour global d'un lot | Tiers payant | **Enregistrer le retour** | PARTIEL | `RecordOutcomeAsync` ClaimBatchView.cs:746. Corrigé : le choix trompeur « Réglé » a disparu. Reste : s'applique à **toutes** les factures du lot. |
| Rejeter une seule facture d'un lot | Tiers payant | — | PARTIEL | Toujours : seulement dans **Facturation** (**Enregistrer le retour** ou **Montant accepté par l'organisme** à 0). |
| Saisir le paiement d'un organisme pour un lot entier (un virement) | Tiers payant | — | MANQUANT | Toujours : **Montant accepté par l'organisme** facture par facture dans **Facturation**. |
| Retransmettre une facture rejetée | Tiers payant | — | PARTIEL | Toujours : les rejetées ne sont pas proposées dans « À transmettre » (BillingQueries.cs:637) ; passer par **Facturation**. |
| Statut « Retransmis » | Tiers payant / Facturation | **Retour de l'organisme** = « Retransmis » | INUTILE | Toujours proposé (BillingService.cs:1101) ; ne crée pas de bordereau et bloque la vraie retransmission (Invoice.cs:145). Le manuel le signale. |
| Garder trace d'une relance | Tiers payant | — | MANQUANT | Toujours. |

## Visa des caisses

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les caisses clôturées | Visa des caisses | **Du**, **Au**, filtres **À viser** / **Toutes**, **Actualiser** | OUI | `ActivateAsync` CashSessionReviewView.cs:242 ; filtres :82-83, :206-214 ; droit :64. |
| Voir le détail d'une caisse | Visa des caisses | carte de détail à côté de la liste | OUI | Nouveau ; totaux avec Effet / traite et Paiement mobile (TreasuryQueries.cs:568). |
| Viser une caisse avec commentaire | Visa des caisses | **Viser la caisse…** | OUI | Corrigé : grisé sur la caisse que l'utilisateur a lui-même tenue (CashSessionReviewView.cs:294-296) ; `ReconcileAsync` :341. |
| Voir le détail des prélèvements d'une caisse | Visa des caisses | — | PARTIEL | Toujours : total **Prélèvements** seulement. |
| Voir les caisses encore ouvertes | Visa des caisses | — | MANQUANT | Toujours. |
| Annuler un visa | Visa des caisses | — | MANQUANT | Toujours (peut être voulu). |

## Manques à corriger (par impact sur le travail quotidien)

1. **Paiement d'un organisme par lot** : pas de saisie globale d'un virement CNSS/CNOPS ; il faut saisir facture par facture dans **Facturation**.
2. **Reçu d'acompte non imprimable** : le patient qui paie d'avance ne reçoit aucun document.
3. **Liste des factures limitée à 50 lignes**, sans pages suivantes, et **sans colonnes Part organisme / statut tiers payant**.
4. **Avoir partiel absent** : corriger une seule analyse oblige à annuler toute la facture et à refacturer.
5. **Pas de rapport de clôture imprimable** ni de liste des règlements du poste dans **Caisse**.
6. **Retransmission d'un rejet** seulement depuis **Facturation** ; « Retransmis » bloque la vraie retransmission ; pas de rejet facture par facture dans **Tiers payant**.
7. **Bordereau TP non imprimable / non exportable**.
8. **Caisse oubliée ouverte** : invisible au responsable et impossible à clôturer par un autre ; prélèvement saisi par erreur non annulable ; détail des prélèvements absent du visa.
9. Points secondaires : filtre « Créance abandonnée » toujours vide ; pas de note libre sur un règlement ; pas de référence pour le paiement mobile ; pas d'abandon de créance patient ; pas de devis imprimable ; organisme destinataire non modifiable à l'émission ; détail des acomptes non visible.

Corrigés depuis la vérification précédente : colonne **Type**, filtres de statut (fin de « Tout sélectionner »), confirmation de **Transmettre au tiers payant** dans **Facturation**, Paiement mobile et Effet / traite comptés dans la caisse et le visa, choix « Réglé » retiré du retour d'organisme, **Cocher les factures transmissibles de cet organisme** limité à un organisme, **Viser la caisse…** grisé sur sa propre caisse.
