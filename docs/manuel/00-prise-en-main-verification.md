# Vérification de l'interface — Prise en main (connexion, fenêtre principale, rôles, démonstration, tableau de bord)

Légende : **Oui** = le bouton ou le champ existe et déclenche la fonction ; **PARTIEL** = il existe mais il manque quelque chose ou il est mal ciblé ; **MANQUANT** = la fonction existe dans l'application mais aucun écran ne permet de la déclencher.
Les références « fichier:ligne » renvoient au code de l'écran (pour l'équipe de développement).

## Connexion et mot de passe

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Se connecter | Connexion | **Identifiant**, **Mot de passe**, **Site**, **Se connecter** (Entrée) | Oui | LoginForm.cs:64 → SignInAsync LoginForm.cs:291 |
| Choisir la langue avant connexion | Connexion | **Langue** (Français / العربية / English) | Oui | LoginForm.cs:72, 270 ; appliquée immédiatement |
| Retenir l'identifiant sur le poste | Connexion | **Se souvenir de l'identifiant** | Oui | LoginForm.cs:316 (profil Windows de l'utilisateur) |
| Avertir des majuscules | Connexion | message « Attention : le verrouillage des majuscules est activé. » | Oui | LoginForm.cs:68-69, 282 |
| Logo du site choisi | Connexion | logo | Oui | LoginForm.cs:73, 235 |
| Changement de mot de passe obligatoire (premier accès, réinitialisation, expiration) | Changer le mot de passe | **Nouveau mot de passe**, **Confirmer le mot de passe**, **Enregistrer** | Oui | LoginForm.cs:322, 344 ; ChangePasswordForm.cs:60 → 139 ; fermeture refusée ChangePasswordForm.cs:182 |
| Changer volontairement son mot de passe une fois connecté | Fenêtre principale | — | **MANQUANT** | La fenêtre « Changer le mot de passe » n'est ouverte que par la connexion (LoginForm.cs:349). Aucun bouton dans la fenêtre principale ; seul l'administrateur peut réinitialiser (Utilisateurs). |
| Changer de site sans se déconnecter | Fenêtre principale | — | **MANQUANT** | La fonction de changement de site existe (AuthenticationService.SwitchSiteAsync) mais n'est appelée par aucun écran. Contournement : Déconnexion puis reconnexion. |
| Message clair pour un compte désactivé | Connexion | ligne d'état | PARTIEL | Un compte désactivé reçoit « Identifiant ou mot de passe incorrect. » (choix de sécurité, AuthenticationService.cs:64). Le message « Ce compte est désactivé. » existe mais n'est jamais affiché. À expliquer aux utilisateurs. |
| Verrouillage après échecs | Connexion | message « Compte verrouillé jusqu'à … » | Oui | AuthenticationService.cs:66-77 ; visible seulement avec le bon mot de passe |
| Refus d'un compte de démonstration sur base réelle | Connexion | message « Ce compte de démonstration a été désactivé… » | Oui | AuthenticationService.cs:83-93 |

## Fenêtre principale

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Ouvrir un écran autorisé | Menu de gauche | nom de l'écran | Oui | MainShellForm.cs:559 → ActivateAsync 630 ; écrans non autorisés non affichés (531) |
| Replier / déplier un groupe | Menu de gauche | titre du groupe (info-bulle « Replier ce groupe » / « Déplier ce groupe ») | Oui | MainShellForm.cs:514 ; mémorisé par utilisateur et par poste |
| Changer la langue en cours de session (droite à gauche en arabe) | Bandeau | liste des langues | PARTIEL | MainShellForm.cs:141, 731. Fonctionne, mais le choix n'est pas enregistré dans la fiche : à la connexion suivante la langue préférée de la fiche revient. |
| Imprimer la liste affichée | Bandeau | **Imprimer (Ctrl+P)** | Oui | MainShellForm.cs:193 → 699 ; « Cet écran n'affiche aucune liste à imprimer. » si pas de liste |
| Voir l'utilisateur, le site, le poste, l'heure | Bandeau | textes « Site : … · Poste : … », horloge | Oui | MainShellForm.cs:676-679, 729 (mise à jour toutes les 30 s) |
| Raccourcis clavier (F1, Ctrl+S, Ctrl+F, Ctrl+P, F5, Ctrl+1…Ctrl+0) | Toute la fenêtre | touches | Oui | MainShellForm.cs:760-770, 793 |
| Afficher / personnaliser les raccourcis | Raccourcis clavier | **Raccourcis clavier (F1)**, **Rétablir les raccourcis par défaut**, **Enregistrer**, **Annuler** | Oui | MainShellForm.cs:576 → 868 ; ShortcutsDialog.cs:46-48, 117, 163, 170 |
| Se déconnecter | Menu de gauche | **Déconnexion** | Oui | MainShellForm.cs:585 → 972 ; la confirmation affiche seulement le mot « Déconnexion » comme question (texte peu explicite) |
| Fermeture automatique après inactivité | — | automatique | Oui | MainShellForm.cs:127, 912 (15 min, réglage) |
| Fermeture si le compte a été modifié | — | automatique | Oui | MainShellForm.cs:132, 931 (contrôle chaque minute) |

## Rôles, mode démonstration

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Consulter / modifier les droits d'un rôle | Utilisateurs › onglet **Rôles et droits** | (voir chapitre Administration) | Oui | UserAdministrationView.cs:421, 429 |
| Activer le mode démonstration | — | réglage d'installation « SeedDemoDataOnFirstRun » | Normal | Pas d'écran : réglage réservé à l'informatique, volontairement. |
| Désactiver les comptes demo.* sur base réelle | — | automatique | Oui | Au démarrage et à la connexion (DatabaseInitializer, AuthenticationService.cs:83) |
| Déclaration épidémiologique (droit « statistics.declaration » donné au Biologiste responsable) | — | — | **MANQUANT** | Le droit existe et est attribué, mais aucun écran ni fonction ne l'utilise. |

## Tableau de bord

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir les alertes et l'activité du jour | Tableau de bord | tuiles **À traiter en priorité**, **Activité du jour** | Oui | DashboardView.cs:124 ; tuiles filtrées par droit (153-173, 183) |
| Ouvrir l'écran d'une alerte en cliquant sa tuile | Tableau de bord | tuile | **PARTIEL** | La tuile non nulle prend un curseur « main » (DashboardView.cs:234-238) et la donnée prévoit l'écran cible (« validation », « inventory », « temperatures », « supplierinvoices », « orders »), mais **aucun clic n'est branché** : le clic ne fait rien. Bouton qui ne mène nulle part. |
| Actualiser | Tableau de bord | — (F5 seulement) | PARTIEL | Pas de bouton à l'écran ; F5 ou rouvrir l'écran. La ligne du bas « Actualiser — HH:MM:SS » ressemble à un bouton mais n'est qu'un texte. |
| Signaler la configuration incomplète | Tableau de bord | bandeau orange | Oui | DashboardView.cs:247 ; texte en français même en arabe/anglais (écrit en dur dans la requête) |
| Libellé de la tuile étalonnage | Tableau de bord | « Prochain étalonnage » | PARTIEL | Compte les étalonnages **dépassés** ; le libellé laisse croire à une échéance à venir. |
| Tuile « Articles à commander » limitée au site | Tableau de bord | « Articles à commander » | à vérifier | Le calcul ne filtre pas sur le site (DashboardQueries.cs, toReorder) contrairement aux autres tuiles. |

## Manques à corriger (par impact sur le travail quotidien)

1. **Tuiles du Tableau de bord non cliquables** alors qu'elles l'annoncent (curseur main) : brancher l'ouverture de l'écran cible (Validation biologique, Stocks, Températures, Factures fournisseurs, Dossiers).
2. **Pas de changement volontaire du mot de passe** une fois connecté : ajouter « Changer le mot de passe » dans le menu (la fenêtre existe déjà).
3. **Pas de changement de site** sans déconnexion, alors que la fonction existe : utile aux biologistes multi-sites.
4. Choix de langue du bandeau non mémorisé dans la fiche utilisateur.
5. Libellé « Prochain étalonnage » trompeur (compte les dépassements) ; tuile « Articles à commander » à vérifier par site ; bandeau de configuration non traduit.
6. Pas de bouton « Actualiser » visible sur le Tableau de bord (F5 seulement).
7. Droit « déclaration épidémiologique » sans écran.
8. Confirmation de déconnexion peu explicite (« Déconnexion » seul comme question).
