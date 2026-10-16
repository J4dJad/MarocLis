# Vérification de l'interface — Finances (chapitre 07)

Méthode : chaque fonction proposée par l'application pour les écrans du groupe Finances a été rapprochée du bouton ou du champ qui la déclenche. Les numéros de ligne renvoient aux fichiers d'écran (src/MarocLIS.Desktop/Views). Les libellés sont ceux du fichier français. Toutes les fonctions de ces écrans sont atteignables depuis l'interface : aucune n'est **MANQUANT**. Les réserves sont classées **PARTIEL**.

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Charger les sites de l'export | Export comptable | liste **Sites** | Oui | AccountingExportView.cs:191 (ActivateAsync), sites accessibles à l'utilisateur, tous cochés |
| Contrôler les écritures d'une période | Export comptable | **Contrôler** | Oui | AccountingExportView.cs:63 → CheckAsync l.226 |
| Exporter le fichier des écritures | Export comptable | **Exporter** | PARTIEL | l.64 → ExportAsync l.288. Le bouton reste actif si l'on change période, sites ou journaux après **Contrôler** (l.285 seulement). Le fichier est recontrôlé côté application, donc il est sûr, mais le récapitulatif affiché ne correspond plus au fichier. Il faudrait regriser **Exporter** à tout changement. |
| Choisir le format Sage / générique | Export comptable | **Format du fichier** | Oui | l.174-178 |
| Choisir les journaux | Export comptable | 6 cases « Ventes et avoirs » … « Achats fournisseurs » | Oui | l.109-121 |
| État de TVA | Export comptable | **État de TVA (CSV)**, **État de TVA (PDF)** | Oui | l.65-66 → VatAsync l.310 |
| Signaler l'absence de registre des immobilisations | Export comptable | ligne d'état | Oui | l.282-283 |
| Éviter un double export d'une même période | Export comptable | — | PARTIEL | Rien n'avertit qu'une période a déjà été exportée (sauf la mémorisation interne des dotations et des factures). Un second import dans Sage doublerait les écritures. |
| Éviter un double export des achats | Export comptable / Factures fournisseurs | **Export comptable** (Factures fournisseurs) et case « Achats fournisseurs » | PARTIEL | Deux chemins exportent les mêmes achats (SupplierInvoiceView.cs:654 et AccountingExportView). Aucun avertissement croisé. |
| Lire les comptes et journaux | Comptes et journaux | (ouverture), **Actualiser** | Oui | AccountingSettingsView.cs:98 ; l.41 |
| Modifier un compte / code journal | Comptes et journaux | colonne **Compte ou code** | Oui | grille modifiable l.69-77 |
| Choisir le mode des comptes patients | Comptes et journaux | ligne **Compte des patients** (liste) | Oui | l.131-144 |
| Enregistrer les paramètres | Comptes et journaux | **Enregistrer** | PARTIEL | l.42 → SaveAsync l.157. Le message « Le paramètre {0} ne peut pas être vide. » affiche le nom technique (ex. Accounting.Account.…), pas le libellé de la ligne. |
| Abandonner une saisie | Comptes et journaux | **Actualiser** | PARTIEL | l.41 : les saisies non enregistrées sont perdues sans demande de confirmation. |
| Registre d'un exercice | Immobilisations | **Exercice**, **Actualiser** | Oui | FixedAssetView.cs:195, 194 |
| Nouvelle immobilisation | Immobilisations | **Nouvelle immobilisation** | Oui | l.208 → EditAsync l.457 ; fiche FixedAssetDialog Accept l.801 |
| Créer depuis un équipement | Immobilisations | **Depuis un équipement** → **Sélectionner** | Oui | l.209 → FromInstrumentAsync l.488 ; dialogue l.846 |
| Lier une facture fournisseur | Fiche immobilisation | **Fournisseur**, **Facture fournisseur**, **N° de facture** | Oui | l.693-694, ApplyInvoice l.788 |
| Modifier une immobilisation | Immobilisations | **Modifier** | Oui | l.210 |
| Durée par défaut selon la catégorie | Fiche immobilisation | **Durée (ans)** | PARTIEL | La durée par défaut (5 ou 10 ans) n'est posée qu'à l'ouverture (l.635-637). Changer la **Catégorie** ensuite ne la met pas à jour. |
| Plan d'amortissement | Immobilisations | **Plan d'amortissement**, double-clic | Oui | l.211, 202 → ShowScheduleAsync l.506 ; **Export CSV** du plan l.967 |
| Sortie d'un bien | Immobilisations | **Sortie** → **Enregistrer** | Oui | l.212 → DisposalAsync l.537-543 ; dialogue l.985 |
| Annuler une sortie | Immobilisations | **Annuler la sortie** | Oui | l.526-534 (même bouton, libellé changé l.438) |
| Tableau annuel | Immobilisations | **Tableau (PDF)**, **Export CSV** | Oui | l.213 → l.550 ; l.214 → l.569 |
| Comptes par catégorie | Immobilisations | **Comptes** → **Enregistrer** | Oui | l.215 → EditAccountsAsync l.577 ; dialogue l.1028 |
| Équipement lié, maintenance et pannes | Immobilisations | panneau du bas | Oui | l.361 (lecture seule) |
| Droits sur les boutons | Immobilisations | — | Oui | l.217 : Nouvelle, Depuis un équipement, Modifier, Sortie, Comptes réservés à « gérer » ; consultation, plan et exports ouverts à « consulter » |
| Supprimer une immobilisation saisie par erreur | Immobilisations | — | Absent (non prévu) | Aucune fonction de suppression, ni dans l'application ni à l'écran. Il faut corriger ou « sortir » le bien. À décider. |
| Liste des comptes bancaires | Rapprochement bancaire | **Compte bancaire**, **Comptes bancaires** | Oui | BankReconciliationView.cs:224 ; l.67 → TreasuryDialogs.cs:11 |
| Créer / modifier / clôturer un compte | Comptes bancaires | **Nouveau**, **Modifier**, double-clic, **Actif** | Oui | TreasuryDialogs.cs:62-64 ; éditeur Accept l.140 (contrôle du RIB) |
| Importer un relevé CSV | Rapprochement bancaire | **Importer un relevé…** → **Aperçu** → **Importer** | Oui | BankReconciliationView.cs:68 → l.332 ; TreasuryDialogs.cs:227 (PreviewAsync l.371), 228 (ImportAsync l.423) |
| Paramétrer les colonnes du relevé | Import de relevé | **Séparateur**, **Lignes à ignorer**, **Format de date**, **Décimales**, **Ligne d'en-têtes**, **Col. date**, **Col. date valeur**, **Col. libellé**, **Col. référence**, **Col. montant**, **Col. débit**, **Col. crédit** | Oui | TreasuryDialogs.cs:256-271 ; aperçu périmé l.361 |
| Annuler un import | Rapprochement bancaire | **Annuler l'import** | Oui | l.73 → WithdrawImportAsync l.426 |
| Rapprochement automatique | Rapprochement bancaire | **Rapprochement automatique** | Oui | l.69 → l.373 |
| Rapprochement manuel (1 ligne ↔ n mouvements) | Rapprochement bancaire | **Rapprocher la sélection** | Oui | l.70 → MatchAsync l.385 ; sélection multiple l.132 ; raison de l'écart l.395-400 |
| Justifier une ligne | Rapprochement bancaire | **Justifier…** | Oui | l.71 → l.408 |
| Annuler un rapprochement | Rapprochement bancaire | **Annuler le rapprochement** | Oui | l.72 → l.441 |
| Filtrer le reste à faire | Rapprochement bancaire | **Lignes non rapprochées seulement** | Oui | l.66 |
| Changer la période | Rapprochement bancaire | **Du** / **Au** | PARTIEL | Les dates n'ont pas de recalcul automatique (seuls le compte l.65 et **Actualiser** l.64 rechargent). L'écran peut montrer l'ancienne période alors que **Rapprochement automatique** utilise la nouvelle (l.377). |
| Voir quels mouvements une ligne a rapprochés | Rapprochement bancaire | colonne **Statut** | PARTIEL | Seules la nature et le commentaire (au survol, l.285) sont visibles. Le détail des mouvements rapprochés (montants, références) n'est pas affiché. |
| Prévision de trésorerie | Prévision de trésorerie | (ouverture), **Actualiser** | Oui | CashForecastView.cs:131 ; l.49 |
| Options de calcul | Prévision de trésorerie | **Encaissements patients (moyenne)**, **Bordereaux en retard en semaine 1** | Oui | l.50-51 (recalcul immédiat) |
| Jour de paie | Prévision de trésorerie | **Jour de paie (0 = fin de mois)** | PARTIEL | Aucun recalcul au changement : il faut cliquer **Actualiser**. Incohérent avec les deux cases voisines. La valeur n'est pas mémorisée (retour à 0 à chaque ouverture). |
| Flux saisis à la main | Prévision de trésorerie | **Autres flux prévus…** → **Nouveau** / **Modifier** | Oui | l.52 → l.191 ; TreasuryDialogs.cs:501-502, grisés sans droit « gérer » (l.495) ; éditeur Accept l.579 |
| Supprimer un flux prévu | Autres flux prévus | — | Absent (non prévu) | On le désactive (**Actif**) ou on met une **Date de fin**. Pas de suppression. |
| Export de la prévision | Prévision de trésorerie | **CSV** | Oui | l.53 → l.199 |
| Coût et marge d'une période | Coût de revient et marge | liste de période, **Du** / **Au**, **Calculer** | Oui | CostMarginView.cs:79 → LoadAsync l.107 |
| Tableaux par analyse / site / automate | Coût de revient et marge | onglets **Par analyse**, **Par site**, **Par automate** | Oui | l.67-68 |
| Export d'un tableau | Coût de revient et marge | **Exporter (CSV)** | Oui | l.80 (onglet affiché seulement) |
| Quote-part des frais | Coût de revient et marge | **Quote-part des frais…** → **Quote-part par analyse (MAD)** → **Enregistrer** | Oui | l.83 → EditAllocationAsync l.164 ; bouton masqué sans droit (l.71) |
| Comprendre « Sans coût connu » | Coût de revient et marge | colonne **Sans coût connu** | PARTIEL | Le nombre est donné, mais pas le réactif en cause ni ce qui manque (prix, tests par coffret, analyse non déclarée). L'utilisateur doit chercher fiche par fiche dans **Données de référence**. Quand un seul réactif manque, le coût des autres est compté : la marge est surestimée sans indication chiffrée. |
| Accès du rôle Comptabilité | Coût de revient et marge | entrée de menu | Choix | Le rôle Comptabilité n'a pas « Pilotage — consulter » (ReferenceDataSeeder.cs:626-651). C'est voulu par le code, mais le propriétaire a demandé que le comptable explique ces chiffres : à décider. |

Boutons présents qui n'apportent rien ou mènent nulle part : aucun bouton vide n'a été trouvé. Deux remarques secondaires :
- **Actualiser** de « Comptes et journaux » est gaté par le droit d'export (AccountingSettingsView.cs:44) alors que l'écran entier l'exige déjà : sans effet pratique.
- Dans « Comptes bancaires », **Nouveau** / **Modifier** ne sont pas gatés eux-mêmes (TreasuryDialogs.cs:62-63). La fenêtre n'est atteignable que par un bouton réservé au droit « gérer » : sans risque aujourd'hui.

## Manques à corriger (par impact sur le travail quotidien)

1. **Double export comptable possible** : rien n'avertit qu'une période, ou les achats déjà exportés depuis « Factures fournisseurs », sont déjà partis chez le comptable. Il faudrait un historique des exports et un avertissement.
2. **Export comptable** : regriser **Exporter** dès que la période, les sites, les journaux ou le format changent après **Contrôler**.
3. **Rapprochement bancaire** : recharger à la modification de **Du** / **Au**, ou griser les actions tant qu'on n'a pas cliqué **Actualiser**. Aujourd'hui l'affichage et le **Rapprochement automatique** peuvent porter sur deux périodes différentes.
4. **Coût de revient et marge** : détailler « Sans coût connu » (réactif et donnée manquante) pour qu'on puisse compléter les fiches articles.
5. **Comptes et journaux** : afficher le libellé de la ligne (et non la clé technique) dans le message « ne peut pas être vide ». Demander confirmation avant **Actualiser** si des saisies ne sont pas enregistrées.
6. **Prévision de trésorerie** : recalculer au changement du **Jour de paie** et mémoriser la valeur.
7. **Rapprochement bancaire** : montrer les mouvements rattachés à une ligne déjà rapprochée.
8. **Immobilisations** : remettre la durée par défaut au changement de catégorie (fiche neuve). Décider d'une suppression d'immobilisation saisie par erreur (aujourd'hui : sortie seulement).
9. **Droits** : décider si le rôle Comptabilité doit voir « Coût de revient et marge ».
