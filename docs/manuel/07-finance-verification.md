# Vérification de l'interface — Finances (chapitre 07)

Méthode : chaque fonction des écrans du groupe Finances a été rapprochée du bouton ou du champ qui la déclenche. Les numéros de ligne renvoient aux fichiers d'écran (src/MarocLIS.Desktop/Views) ; les règles renvoient à src/MarocLIS.Application. Les libellés sont ceux du fichier français. Complété le 2026-10-17 pour le cycle 5 (état du code au commit 1bbef5b) : les fenêtres **Compte bancaire** et **Flux prévisionnel** sont maintenant inversées en arabe, montants toujours alignés à droite (TreasuryDialogs.cs, commit ac2f847) ; les comptes des immobilisations s'arrêtent à 20 chiffres (FixedAssetView.cs, 9f25a6a, déjà décrit). Aucune procédure ni aucun message ne change. Toutes les fonctions de ces écrans sont atteignables depuis l'interface : aucune n'est **MANQUANT**. Les réserves sont classées **PARTIEL**. Revérifié le 2026-10-17 sur le commit 4e20d0d (nouvelle présentation des écrans : en-tête, filtres, cartes, menu « Plus d'actions »).

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Charger les sites de l'export | Export comptable | liste **Sites** (carte « Période et contenu ») | Oui | AccountingExportView.cs:221 (ActivateAsync) : sites accessibles à l'utilisateur, tous cochés (l.232) |
| Contrôler les écritures d'une période | Export comptable | **Contrôler** | Oui | l.66 → CheckAsync l.258 ; récapitulatif et « Points à corriger » l.274-317 |
| Exporter le fichier des écritures | Export comptable | **Exporter** | PARTIEL | l.67 → ExportAsync l.320 ; grisé tant que le contrôle n'est pas fait ou refuse (l.282, 317). Le bouton reste actif si l'on change période, sites ou journaux après **Contrôler**. Le fichier est recontrôlé côté application (refus → nouveau contrôle l.328), donc il est sûr, mais le récapitulatif affiché ne correspond plus au fichier. |
| Choisir le format Sage / générique | Export comptable | **Format du fichier** | Oui | l.191 (libellé), Request l.243 |
| Choisir les journaux | Export comptable | 6 cases « Ventes et avoirs » … « Achats fournisseurs » | Oui | l.193 ; Request l.245 |
| État de TVA | Export comptable | **État de TVA (CSV)**, **État de TVA (PDF)** | Oui | l.68-69 → VatAsync l.342 |
| Signaler l'absence de registre des immobilisations | Export comptable | ligne d'état | Oui | l.314-315 |
| Message « aucune écriture » | Export comptable | ligne d'état | PARTIEL | l.305 affiche « Aucune facture ni aucun règlement fournisseur sur cette période. » (clé des achats) pour l'export général : le texte ne parle que des fournisseurs. Manuel : précisé. |
| Éviter un double export d'une même période | Export comptable | — | PARTIEL | Rien n'avertit qu'une période a déjà été exportée (seuls les mois de dotation et les factures sont mémorisés en interne). Un second import dans Sage doublerait les écritures. |
| Éviter un double export des achats | Export comptable / Factures fournisseurs | **Export comptable** (Factures fournisseurs) et case « Achats fournisseurs » | PARTIEL | Deux chemins exportent les mêmes achats (SupplierInvoiceView.cs:692 et AccountingExportView). Aucun avertissement croisé. |
| Lire les comptes et journaux | Comptes et journaux | (ouverture), **Actualiser** | Oui | AccountingSettingsView.cs:101 ; l.41 |
| Modifier un compte / code journal | Comptes et journaux | colonne **Compte ou code** | Oui | grille modifiable, en-têtes l.94-96 |
| Choisir le mode des comptes patients | Comptes et journaux | ligne **Compte des patients** (liste) | Oui | ActivateAsync l.101 et suivantes |
| Enregistrer les paramètres | Comptes et journaux | **Enregistrer** (Ctrl+S) | PARTIEL | l.42 → SaveAsync l.166, « Enregistré. » l.174. Le message « Le paramètre {0} ne peut pas être vide. » affiche toujours le nom technique (AccountingExportService.cs:360), pas le libellé de la ligne. |
| Abandonner une saisie | Comptes et journaux | **Actualiser** | PARTIEL | l.41 : les saisies non enregistrées sont perdues sans demande de confirmation. |
| Bandeau CGNC | Comptes et journaux | bandeau jaune | Oui | l.93 (Accounting.SettingsNotice) |
| Registre d'un exercice | Immobilisations | **Exercice**, **Actualiser** | Oui | FixedAssetView.cs:204 (rechargement au changement d'année), 203 |
| Résumé de l'exercice | Immobilisations | ligne grise sous le titre | Oui | l.348 (Assets.Heading) |
| Nouvelle immobilisation | Immobilisations | **Nouvelle immobilisation** (en-tête) | Oui | l.217 → EditAsync l.475 ; fiche FixedAssetDialog l.611 |
| Créer depuis un équipement | Immobilisations | **Depuis un équipement** → **Sélectionner** | Oui | l.218 → FromInstrumentAsync l.506 ; dialogue l.870, double-clic l.899 |
| Lier une facture fournisseur | Fiche immobilisation | **Fournisseur**, **Facture fournisseur**, **N° de facture** | Oui | l.712-713 (LoadInvoicesAsync l.780, ApplyInvoice) |
| Modifier une immobilisation | Immobilisations | **Modifier** (barre sous le registre) | Oui | l.219 |
| Durée par défaut selon la catégorie | Fiche immobilisation | **Durée (ans)** | PARTIEL | La durée par défaut (5 ou 10 ans) n'est posée qu'à l'ouverture (l.653-655). Changer la **Catégorie** ensuite ne la met pas à jour. |
| Plan d'amortissement | Immobilisations | **Plan d'amortissement**, double-clic | Oui | l.220, 211 → ShowScheduleAsync l.524 ; **Export CSV** du plan l.997 |
| Sortie d'un bien | Immobilisations | **Sortie** → **Enregistrer** | Oui | l.221 → DisposalAsync l.540 ; dialogue l.1021 |
| Annuler une sortie | Immobilisations | **Annuler la sortie** | Oui | même bouton, libellé changé l.456 ; confirmation l.546 |
| Tableau annuel | Immobilisations | **Plus d'actions** > **Tableau (PDF)**, **Export CSV** | Oui | l.222 → PrintAsync l.568 ; l.223 → ExportAsync l.587. Les colonnes fournisseur et facture sont masquées à l'écran (l.189-190) mais présentes dans le CSV. |
| Comptes par catégorie | Immobilisations | **Plus d'actions** > **Comptes** → **Enregistrer** | Oui | l.224 → EditAccountsAsync l.595 ; dialogue l.1070 |
| Longueur des comptes | Fenêtre **Comptes** | **Immobilisation**, **Amortissement**, **Dotation** | Oui | FixedAssetView.cs:1097-1099 (commit 9f25a6a) : saisie arrêtée à 20 chiffres ; contrôle FixedAssetSettings.cs:79 (4 à 20 chiffres) |
| Équipement lié, maintenance et pannes | Immobilisations | carte « Équipement lié — maintenance et pannes » | Oui | l.379, 409 (lecture seule) |
| Droits sur les boutons | Immobilisations | — | Oui | l.226 : Nouvelle, Depuis un équipement, Modifier, Sortie, Comptes réservés à « gérer » ; consultation, plan et exports ouverts à « consulter » |
| Supprimer une immobilisation saisie par erreur | Immobilisations | — | Absent (non prévu) | Aucune fonction de suppression, ni dans l'application ni à l'écran. Il faut corriger ou « sortir » le bien. À décider. |
| Liste des comptes bancaires | Rapprochement bancaire | **Compte bancaire**, **Comptes bancaires** | Oui | BankReconciliationView.cs:242 ; l.77 → l.368 → TreasuryDialogs.cs:11 |
| Créer / modifier / clôturer un compte | Comptes bancaires | **Nouveau**, **Modifier**, double-clic, **Actif** | Oui | TreasuryDialogs.cs:64-68. **Modifier** est maintenant grisé sans ligne choisie (l.67, 90). Contrôle du RIB l.156. |
| Importer un relevé CSV | Rapprochement bancaire | **Importer un relevé…** → **Aperçu** → **Importer** | Oui | BankReconciliationView.cs:78 → l.376 ; TreasuryDialogs.cs:238 (PreviewAsync l.390), 239 (ImportAsync l.442) |
| Paramétrer les colonnes du relevé | Import de relevé | **Séparateur**, **Lignes à ignorer**, **Format de date**, **Décimales**, **Ligne d'en-têtes**, **Col. date**, **Col. date valeur**, **Col. libellé**, **Col. référence**, **Col. montant**, **Col. débit**, **Col. crédit** | Oui | TreasuryDialogs.cs:241-244 ; aperçu périmé l.386 |
| Montant démesuré dans le relevé | Import de relevé | aperçu | Oui | BankStatementCsv (MaximumAmount) : montant refusé comme illisible |
| Annuler un import | Rapprochement bancaire | **Plus d'actions** > **Annuler l'import** | Oui | l.83 → WithdrawImportAsync l.478 |
| Rapprochement automatique | Rapprochement bancaire | **Rapprochement automatique** (barre du bas) | Oui | l.79 → l.417 |
| Rapprochement manuel (1 ligne ↔ n mouvements) | Rapprochement bancaire | **Rapprocher la sélection** | Oui | l.80 → MatchAsync l.429 ; raison de l'écart l.449 ; actif seulement si ligne non rapprochée et mouvement choisi (l.360) |
| Justifier une ligne | Rapprochement bancaire | **Justifier…** | Oui | l.81 → l.460 |
| Annuler un rapprochement | Rapprochement bancaire | **Annuler le rapprochement** | Oui | l.82 → l.493 |
| Filtrer le reste à faire | Rapprochement bancaire | pastille **Lignes non rapprochées seulement · N** | Oui | l.108, RenderChip l.166 |
| Changer la période | Rapprochement bancaire | **Du** / **Au** | PARTIEL | Toujours aucun rechargement au changement de date (seuls le compte l.74 et **Actualiser** l.73 rechargent). L'écran peut montrer l'ancienne période alors que **Rapprochement automatique** utilise la nouvelle. |
| Voir quels mouvements une ligne a rapprochés | Rapprochement bancaire | colonne **Statut** | PARTIEL | Seules la nature et le commentaire (au survol, l.328) sont visibles. Le détail des mouvements rapprochés n'est pas affiché. |
| Prévision de trésorerie | Prévision de trésorerie | (ouverture), **Actualiser** | Oui | CashForecastView.cs:139 ; l.52 |
| Solde le plus bas | Prévision de trésorerie | ligne sous le titre | Oui | l.172 (Treasury.LowestClosing), en rouge si négatif |
| Options de calcul | Prévision de trésorerie | **Encaissements patients (moyenne)**, **Bordereaux en retard en semaine 1** | Oui | l.53-54 (recalcul immédiat) |
| Jour de paie | Prévision de trésorerie | **Jour de paie (0 = fin de mois)** | PARTIEL | Toujours aucun recalcul au changement : il faut cliquer **Actualiser**. Incohérent avec les deux cases voisines. La valeur n'est pas mémorisée. |
| Flux saisis à la main | Prévision de trésorerie | **Autres flux prévus…** → **Nouveau** / **Modifier** | Oui | l.55 → ItemsAsync l.204 ; TreasuryDialogs.cs:514 (grisés sans droit « gérer »), **Modifier** grisé sans ligne choisie l.525, 550 |
| Supprimer un flux prévu | Autres flux prévus | — | Absent (non prévu) | On le désactive (**Actif**) ou on met une **Date de fin**. Pas de suppression. |
| Export de la prévision | Prévision de trésorerie | **CSV** | Oui | l.56 → l.212 |
| Coût et marge d'une période | Coût de revient et marge | pastilles de période, **Du** / **Au**, **Calculer** | Oui | RelationsAndSteeringShared.cs:107-108 (date → « Période libre ») ; CostMarginView.cs:81 → LoadAsync l.147 |
| Tableaux par analyse / site / automate | Coût de revient et marge | onglets **Par analyse**, **Par site**, **Par automate** | Oui | l.132-134 |
| Export d'un tableau | Coût de revient et marge | **Exporter (CSV)** | Oui | l.82 (onglet affiché seulement) ; tableau vide → « Aucune donnée » (RelationsAndSteeringShared.cs:230) |
| Quote-part des frais | Coût de revient et marge | **Quote-part des frais…** → **Quote-part par analyse (MAD)** → **Enregistrer** | Oui | l.84 → EditAllocationAsync l.206 ; bouton masqué sans droit (l.73) |
| Comprendre « Sans coût connu » | Coût de revient et marge | colonne **Sans coût connu** | PARTIEL | Le nombre est donné, mais pas le réactif en cause ni ce qui manque (prix, tests par coffret, analyse non déclarée). |
| Accès du rôle Comptabilité | Coût de revient et marge | entrée de menu | Choix | Le rôle Comptabilité n'a toujours pas « Pilotage — consulter » (src/MarocLIS.Infrastructure/Persistence/ReferenceDataSeeder.cs, jeu de droits COMPTABILITE). À décider. |

Boutons présents qui n'apportent rien ou mènent nulle part : aucun. Remarques secondaires :
- **Actualiser** de « Comptes et journaux » est gaté par le droit d'export (AccountingSettingsView.cs:44), alors que l'écran entier l'exige déjà : sans effet pratique.
- Dans « Comptes bancaires », **Nouveau** n'est pas gaté lui-même (TreasuryDialogs.cs:64). La fenêtre n'est atteignable que par un bouton réservé au droit « gérer » : sans risque aujourd'hui.

## Manques à corriger (par impact sur le travail quotidien)

1. **Double export comptable possible** : rien n'avertit qu'une période, ou les achats déjà exportés depuis « Factures fournisseurs », sont déjà partis chez le comptable.
2. **Export comptable** : regriser **Exporter** dès que la période, les sites, les journaux ou le format changent après **Contrôler**. Donner un message « aucune écriture » qui ne parle pas que des fournisseurs.
3. **Rapprochement bancaire** : recharger à la modification de **Du** / **Au**, ou griser les actions tant qu'on n'a pas cliqué **Actualiser**.
4. **Coût de revient et marge** : détailler « Sans coût connu » (réactif et donnée manquante).
5. **Comptes et journaux** : afficher le libellé de la ligne (et non la clé technique) dans le message « ne peut pas être vide ». Demander confirmation avant **Actualiser** si des saisies ne sont pas enregistrées.
6. **Prévision de trésorerie** : recalculer au changement du **Jour de paie** et mémoriser la valeur.
7. **Rapprochement bancaire** : montrer les mouvements rattachés à une ligne déjà rapprochée.
8. **Immobilisations** : remettre la durée par défaut au changement de catégorie. Décider d'une suppression d'immobilisation saisie par erreur.
9. **Droits** : décider si le rôle Comptabilité doit voir « Coût de revient et marge ».
