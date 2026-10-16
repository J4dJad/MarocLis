# Vérification de l'interface — Facturation, Caisse, Tiers payant, Visa des caisses

Méthode : chaque opération offerte par l'application pour ce module (facturation, caisse, tiers payant, visa) a été rapprochée du bouton ou du champ qui la déclenche. Les numéros de ligne renvoient aux fichiers d'écran (`BillingView.cs`, `BillingCorrectionDialogs.cs`, `CashSessionView.cs`, `ClaimBatchView.cs`, `CashSessionReviewView.cs` dans `src/MarocLIS.Desktop/Views/`).

Légende : **OUI** = présent et utilisable ; **PARTIEL** = présent mais incomplet ou mal réglé ; **MANQUANT** = possible seulement par le code ; **INUTILE** = présent mais sans effet utile.

## Facturation

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les actes non facturés | Facturation | « Actes non facturés » | OUI | `LoadPendingAsync` BillingView.cs:432. Limité aux 500 plus récents, signalé dans le titre. |
| Lister factures et avoirs | Facturation | « Factures », recherche, filtre, **Actualiser** | PARTIEL | `LoadLedgerAsync` BillingView.cs:500 ne charge que la page 1 (50 lignes). Aucun bouton page suivante : au-delà de 50, on ne voit les factures qu'avec la recherche. |
| Filtrer les créances abandonnées | Facturation | filtre « Créance abandonnée » | INUTILE | BillingView.cs:386. Aucune opération ne met une facture dans ce statut : le filtre renvoie toujours une liste vide. |
| Voir le type de document | Facturation | colonne **Désignation** | PARTIEL | En-tête tiré de « Field.Name » (BillingView.cs:410) : la colonne affiche « Facture / Facture d'avoir » sous le titre « Désignation ». Libellé « Type » attendu. |
| Option « tous les statuts » du filtre | Facturation | « Tout sélectionner » | PARTIEL | BillingView.cs:383 utilise « Action.SelectAll » : libellé trompeur, « Tous » attendu. |
| Voir le statut tiers payant et la part organisme d'une facture | Facturation | — | MANQUANT | La liste des factures n'a ni colonne « Part organisme » ni colonne de statut tiers payant ; on ne sait pas quelles factures sont transmises / rejetées sans passer par **Tiers payant**. |
| Émettre une facture (un ou plusieurs dossiers) | Facturation | **Émettre la facture** + fenêtre **Émettre la facture** | OUI | `IssueAsync` BillingView.cs:572 ; fenêtre `InvoiceIssueDialog` BillingCorrectionDialogs.cs:247. Contrôles patient unique / organisme unique avant la fenêtre. |
| Remise à l'émission | Facturation | **Remise**, **Motif de la remise** | OUI | Visibles seulement avec le droit remise (BillingView.cs:634). |
| Refacturer à l'organisme après avoir | Facturation | question « Adresser cette facture au tiers payant … ? » | OUI | BillingView.cs:621-625. |
| Choisir librement l'organisme destinataire | Facturation | — | MANQUANT | L'application accepte un organisme de remplacement, mais l'écran ne le propose que dans le cas de refacturation ci-dessus. |
| Encaisser un règlement (total ou partiel) | Facturation | **Règlement** → **Mode de règlement**, **Montant**, **Référence**, **Banque**, **Échéance**, **Enregistrer** | OUI | `RecordPaymentAsync` BillingView.cs:981 ; `PaymentDialog` BillingView.cs:1255. Montant pré-rempli, modifiable pour un partiel. Avertissement espèces > 20 000 DH. |
| Note libre sur un règlement | Facturation | — | MANQUANT | L'application garde une note par règlement, la fenêtre envoie toujours vide (BillingView.cs:1506). |
| Paiement mobile | Facturation | **Mode de règlement** = « Paiement mobile » | PARTIEL | Accepté, mais n'est compté dans aucune ligne de la caisse (ni espèces, ni chèque, ni carte, ni virement) ni dans le visa : il échappe au rapport de poste. Pas de référence demandée. |
| Imprimer la facture | Facturation | **Imprimer la facture** | OUI | `PrintAsync` BillingView.cs:1183. Ligne cochée ou ligne courante. |
| Imprimer le reçu de règlement | Facturation | **Imprimer le reçu** | OUI | BillingView.cs:1183. |
| Rembourser un règlement | Facturation | **Rembourser** → choix du règlement → motif | OUI | `CorrectPaymentAsync` BillingView.cs:684. Réservé au droit avoir (Comptabilité, Administrateur) : le Secrétariat qui tient la caisse ne peut pas rembourser. Choix voulu, à confirmer avec le laboratoire. |
| Déclarer un chèque / effet impayé | Facturation | **Déclarer impayé** | OUI | BillingView.cs:684 (mode impayé). |
| Émettre un avoir total | Facturation | **Facture d'avoir** + motif (10 caractères min.) | OUI | `IssueCreditNoteAsync` BillingView.cs:1031. |
| Émettre un avoir partiel (montant choisi) | Facturation | — | MANQUANT | L'application sait faire un avoir d'un montant donné, mais l'écran n'envoie que le motif (BillingView.cs:1079) : toujours avoir du solde complet. Une erreur sur une seule analyse oblige à tout annuler et refacturer. |
| Imprimer un avoir | Facturation | **Imprimer la facture** sur la ligne de l'avoir | OUI | Même bouton ; le document porte le titre d'avoir (à vérifier visuellement). |
| Encaisser un acompte | Facturation | **Encaisser un acompte** | OUI | `TakeDepositAsync` BillingView.cs:808. |
| Imprimer le reçu d'acompte (REC/…) | Facturation | — | MANQUANT | Le numéro REC est seulement affiché dans la barre d'état. Aucun document à remettre au patient qui paie d'avance. |
| Voir le détail des acomptes d'un dossier | Facturation | — | PARTIEL | Seul le total détenu figure (colonne **Acompte**) ; le détail n'apparaît que dans la question de remboursement. |
| Rembourser les acomptes d'un dossier | Facturation | **Rembourser l'acompte** | OUI | `RefundDepositAsync` BillingView.cs:864. Rembourse tous les acomptes détenus du dossier, pas un seul au choix. Droit avoir requis. |
| Transmettre au tiers payant (y compris retransmission d'un rejet) | Facturation | **Transmettre au tiers payant** | OUI | `SubmitClaimAsync` BillingView.cs:1100. Seul endroit pour retransmettre une facture rejetée. Pas de question de confirmation ici (contrairement à **Tiers payant**). |
| Enregistrer le retour d'un organisme sur des factures | Facturation | **Enregistrer le retour** | OUI | `RecordClaimOutcomeAsync` BillingView.cs:756 ; `ClaimOutcomeDialog` BillingCorrectionDialogs.cs:120. |
| Saisir le montant accepté par l'organisme (paiement, part laissée au patient, perte) | Facturation | **Montant accepté par l'organisme** → **Montant accepté**, **Référence du virement ou bordereau**, **Laisser à la charge du patient (reste sur la facture)**, **Passer en perte**, **Motif communiqué par l'organisme** | OUI | `RecordPayerSettlementAsync` BillingView.cs:932 ; `PayerSettlementDialog` BillingCorrectionDialogs.cs:462. Une facture à la fois. |
| Abandonner une créance patient irrécouvrable | Facturation | — | MANQUANT | Seule la part refusée par un organisme peut être passée en perte. Un impayé patient ne peut être soldé que par un avoir (qui dit « charge retirée », pas « créance perdue »). |
| Devis | Facturation | — | MANQUANT | Le type « Devis » existe dans les documents, rien ne permet d'en créer. |

## Caisse

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Ouvrir la caisse | Caisse | **Fond de caisse**, **Ouvrir la caisse** | OUI | `OpenSessionAsync` CashSessionView.cs:345. |
| Voir les encaissements du poste par mode | Caisse | tableau (Fond de caisse, Espèces, Chèque, Carte bancaire, Virement, Remboursements, Prélèvements, Espèces attendues) | PARTIEL | CashSessionView.cs:270-286. Total seulement, pas la liste des règlements du poste ; Effet / traite et Paiement mobile n'apparaissent nulle part. |
| Encaisser depuis la caisse | Caisse | — | PARTIEL | Pas d'encaissement sur cet écran : il faut passer par **Facturation**. Cohérent mais à savoir. |
| Prélèvement de caisse | Caisse | **Prélèvement de caisse** → montant → motif | OUI | `WithdrawAsync` CashSessionView.cs:385. |
| Voir / corriger les prélèvements déjà faits | Caisse | — | MANQUANT | Seul le total est affiché ; un prélèvement saisi par erreur ne peut pas être annulé. |
| Clôturer avec comptage et explication d'écart | Caisse | **Espèces comptées**, **Écart de caisse**, **Explication de l'écart**, **Clôturer la caisse** | OUI | `CloseSessionAsync` CashSessionView.cs:421. Tolérance 10 DH. |
| Imprimer le rapport de clôture (ticket Z) | Caisse | — | MANQUANT | Aucune impression du bilan de poste. |
| Clôturer la caisse d'un collègue absent | Caisse | — | MANQUANT | Seule sa propre caisse ouverte peut être clôturée. Une caisse oubliée ouverte ne peut être fermée que par son titulaire. |

## Tiers payant

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir les factures à transmettre et ce qui les bloque | Tiers payant | « À transmettre », colonne **Ce qui bloque** | OUI | `ActivateAsync` ClaimBatchView.cs:376. |
| Cocher tout ce qui est transmissible | Tiers payant | **Cocher tout ce qui est transmissible** | PARTIEL | `SelectClaimable` ClaimBatchView.cs:601. Coche tous les organismes à la fois : si plusieurs organismes sont en attente, **Transmettre au tiers payant** reste grisé et il faut décocher à la main. Un filtre par organisme manque. |
| Transmettre un bordereau | Tiers payant | **Transmettre au tiers payant** + confirmation | OUI | `SubmitAsync` ClaimBatchView.cs:660. Grisé pour le Secrétariat (droit export). |
| Imprimer / exporter le bordereau à envoyer | Tiers payant | — | MANQUANT | Le numéro TP est attribué mais aucun document ou fichier n'est produit pour l'organisme. |
| Voir les lots et leur contenu | Tiers payant | « Lots transmis », « Contenu du lot » | OUI | ClaimBatchView.cs:417, 491. |
| Enregistrer le retour global d'un lot | Tiers payant | **Enregistrer le retour** | PARTIEL | `RecordOutcomeAsync` ClaimBatchView.cs:730. S'applique à **toutes** les factures du lot : impossible de rejeter une seule facture du lot ici. Le choix « Réglé » met le statut sans saisir d'argent (le reste à payer ne bouge pas) : risque de confusion. |
| Rejeter une seule facture d'un lot | Tiers payant | — | PARTIEL | Possible seulement dans **Facturation** (**Enregistrer le retour** sur la facture cochée, ou **Montant accepté par l'organisme** à 0). |
| Saisir le paiement d'un organisme pour un lot entier (un virement) | Tiers payant | — | MANQUANT | Pas de saisie globale : il faut faire **Montant accepté par l'organisme** facture par facture dans **Facturation**. Très long pour un virement CNSS de 100 factures. |
| Retransmettre une facture rejetée | Tiers payant | — | PARTIEL | Les rejetées ne sont pas proposées dans « À transmettre » ; il faut passer par **Facturation** → **Transmettre au tiers payant**. |
| Statut « Retransmis » | Tiers payant / Facturation | **Retour de l'organisme** = « Retransmis » | INUTILE | Ne crée pas de nouveau bordereau et empêche ensuite la vraie retransmission (seuls « À transmettre » et « Rejeté » peuvent repartir). À retirer de la liste ou à expliquer. |
| Garder trace d'une relance | Tiers payant | — | MANQUANT | Les lots « à relancer » sont signalés, mais aucune date de relance ne peut être notée. |

## Visa des caisses

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Lister les caisses clôturées | Visa des caisses | **Du**, **Au**, **À viser seulement**, **Actualiser** | OUI | `ActivateAsync` CashSessionReviewView.cs:131. |
| Viser une caisse avec commentaire | Visa des caisses | **Viser la caisse…** | PARTIEL | `ReconcileAsync` CashSessionReviewView.cs:188. Le bouton reste actif sur la caisse que l'utilisateur a lui-même tenue ; le refus n'arrive qu'après saisie du commentaire. |
| Voir le détail des prélèvements d'une caisse | Visa des caisses | — | PARTIEL | Seul le total **Prélèvements** est affiché ; chaque dépôt est pourtant gardé à part (pour le rapprochement bancaire). |
| Voir les caisses encore ouvertes | Visa des caisses | — | MANQUANT | Une caisse oubliée ouverte n'apparaît nulle part pour le responsable. |
| Annuler un visa | Visa des caisses | — | MANQUANT | Aucun moyen (peut être voulu). |

## Manques à corriger (par impact sur le travail quotidien)

1. **Paiement d'un organisme par lot** : pas de saisie globale d'un virement CNSS/CNOPS ; il faut saisir facture par facture dans **Facturation**. Le bouton « Réglé » de **Enregistrer le retour** n'enregistre aucun argent et trompe l'utilisateur.
2. **Reçu d'acompte non imprimable** : le patient qui paie d'avance ne reçoit aucun document (numéro REC visible seulement à l'écran).
3. **Liste des factures limitée à 50 lignes**, sans pages suivantes, et **sans colonnes Part organisme / statut tiers payant**.
4. **Avoir partiel absent** : corriger une seule analyse oblige à annuler toute la facture et à refacturer.
5. **Paiement mobile et effets** absents des totaux de caisse et du visa : ces encaissements échappent au contrôle de poste.
6. **Pas de rapport de clôture imprimable** ni de liste des règlements du poste dans **Caisse**.
7. **Retransmission d'un rejet** seulement depuis **Facturation** ; le choix « Retransmis » bloque la vraie retransmission ; pas de rejet facture par facture dans **Tiers payant** ; pas de filtre par organisme pour **Cocher tout ce qui est transmissible**.
8. **Bordereau TP non imprimable / non exportable** pour l'envoi à l'organisme.
9. **Caisse oubliée ouverte** : invisible au responsable et impossible à clôturer par un autre ; prélèvement saisi par erreur non annulable.
10. **Visa des caisses** : bouton actif sur sa propre caisse (refus tardif) ; détail des prélèvements non affiché.
11. Défauts de libellés : colonne « Désignation » pour le type, option « Tout sélectionner » dans le filtre, filtre « Créance abandonnée » toujours vide ; pas de note libre sur un règlement ; pas d'abandon de créance patient ; pas de devis.
