# Vérification de l'interface — Prise en main (connexion, fenêtre principale, rôles, démonstration, tableau de bord)

Re-vérifié le 2026-10-17 au commit 4e20d0d : chaque ligne a été recontrôlée dans le code des écrans (`Forms/LoginForm.cs`, `Forms/ChangePasswordForm.cs`, `Forms/MainShellForm.cs`, `Forms/HelpForm.cs`, `Forms/ShortcutsDialog.cs`, `Help/HelpService.cs`, `Views/DashboardView.cs`) et des fonctions (`AuthenticationService`, `DashboardQueries`, `ReferenceDataSeeder`) ; les libellés ont été comparés à `fr.json`. Les lignes corrigées depuis la vérification précédente passent à **Oui** avec la nouvelle référence ; des lignes ont été ajoutées pour les nouveautés.

Légende : **Oui** = le bouton ou le champ existe et déclenche la fonction ; **PARTIEL** = il existe mais il manque quelque chose ou il est mal ciblé ; **MANQUANT** = la fonction existe dans l'application mais aucun écran ne permet de la déclencher.
Les références « fichier:ligne » renvoient au code de l'écran (pour l'équipe de développement).

## Connexion et mot de passe

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Se connecter | Connexion | **Identifiant**, **Mot de passe**, **Site**, **Se connecter** (Entrée) | Oui | LoginForm.cs:80 → SignInAsync LoginForm.cs:441 ; textes :286-300 |
| Afficher le mot de passe tapé | Connexion | **Afficher** / **Masquer** | Oui | Nouveau. LoginForm.cs:92 |
| Choisir la langue avant connexion | Connexion | **Langue** (Français / العربية / English) | Oui | LoginForm.cs:96 ; appliquée immédiatement |
| Retenir l'identifiant sur le poste | Connexion | **Se souvenir de l'identifiant** | Oui | LoginForm.cs:466 (profil Windows de l'utilisateur) |
| Avertir des majuscules | Connexion | message « ! Attention : le verrouillage des majuscules est activé. » | Oui | LoginForm.cs:439 |
| Logo du site choisi | Connexion | logo | Oui | LoginForm.cs:97, ShowLogo :377 |
| Liste des sites limitée aux sites du laboratoire | Connexion | **Site** | Oui | Nouveau. Les laboratoires partenaires (sous-traitance) sont exclus de la liste, LoginForm.cs:343 |
| Changement de mot de passe obligatoire (premier accès, réinitialisation, expiration) | Changer le mot de passe | **Nouveau mot de passe**, **Confirmer le mot de passe**, **Enregistrer** | Oui | LoginForm.cs:496-503 ; ChangePasswordForm.cs:82-85 (pas d'Annuler, fenêtre sans bouton de fermeture), règles :149, « Enregistré. » :184 |
| Refuser le changement obligatoire | Changer le mot de passe | fermeture de la fenêtre | Oui | Nouveau comportement : si la fenêtre est fermée sans changement, la session est fermée et l'écran de connexion revient (LoginForm.cs:476) |
| Changer volontairement son mot de passe une fois connecté | Fenêtre principale | — | **MANQUANT** | Toujours : la fenêtre « Changer le mot de passe » n'est ouverte que par la connexion (LoginForm.cs:496-503). Aucun bouton dans la fenêtre principale ; seul l'administrateur peut réinitialiser (Utilisateurs). |
| Changer de site sans se déconnecter | Fenêtre principale | — | **MANQUANT** | Toujours : AuthenticationService.SwitchSiteAsync (AuthenticationService.cs:243) n'est appelée par aucun écran. Contournement : Déconnexion puis reconnexion. |
| Message clair pour un compte désactivé | Connexion | ligne d'état | PARTIEL | Toujours : un compte désactivé reçoit « Identifiant ou mot de passe incorrect. » (AuthenticationService.cs:64-65, raison non divulguée par FailAsync :364-371). Choix de sécurité, à expliquer aux utilisateurs. |
| Verrouillage après échecs | Connexion | message « Compte verrouillé jusqu'à … » | Oui | AuthenticationService.cs:67-78, LockedOutAnswer :265 ; visible seulement avec le bon mot de passe |
| Refus d'un compte de démonstration sur base réelle | Connexion | message « Ce compte de démonstration a été désactivé… » | Oui | AuthenticationService.cs:84-94 |
| Échec au démarrage (base inaccessible) | Démarrage | message d'erreur puis fermeture | Oui | Program.cs:76, InitialiseDatabase :131-146 |

## Fenêtre principale

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Ouvrir un écran autorisé | Menu de gauche | nom de l'écran | Oui | Entrées du menu MainShellForm.cs:366-541 ; écrans non autorisés non affichés ; écrans par défaut DefaultScreenKeys :68 |
| Tableau des modules de la « Présentation de l'application » | manuel | groupes du menu | Oui | Contrôlé le 2026-10-17 (commit 1bbef5b) contre les groupes MainShellForm.cs:366-569 et les titres des chapitres 00 à 10 : **Températures**, **Équipements** et **Contrats automates** sont dans le groupe Stocks du menu mais décrits au chapitre 05 ; **Visa des caisses** est dans le groupe Finances mais décrit au chapitre 04 |
| Replier / déplier un groupe | Menu de gauche | titre du groupe (info-bulle « Replier ce groupe » / « Déplier ce groupe ») | Oui | MainShellForm.cs:634 ; mémorisé par utilisateur et par poste |
| Fil d'Ariane de l'écran | Bandeau | « Groupe › Écran » | Oui | Nouveau. ShowBreadcrumb MainShellForm.cs:316 |
| Ouvrir l'aide de l'écran | Bandeau / clavier | **Aide (F1)**, touche F1 | Oui | Nouveau bouton MainShellForm.cs:228-231 ; F1 défini :908 ; HelpService.ShowCurrent HelpService.cs:36, filtre F1 :84 ; page absente → « Help.NotFound » :49 ; repli navigateur :74 |
| Naviguer dans l'aide | Aide | **Précédent**, **Suivant**, **Sommaire**, **Imprimer**, Échap | Oui | Nouveau. HelpForm.cs:177-180 ; Échap masque la fenêtre :150 |
| Changer la langue en cours de session (droite à gauche en arabe) | Bandeau | liste des langues | PARTIEL | Toujours : MainShellForm.cs:163, :870. Fonctionne, mais le choix n'est pas enregistré dans la fiche : à la connexion suivante la langue préférée de la fiche revient. |
| Imprimer la liste affichée | Bandeau | **Imprimer (Ctrl+P)** | Oui | MainShellForm.cs:224 → PrintCurrentListAsync :838 ; « Cet écran n'affiche aucune liste à imprimer. » :846 |
| Voir l'utilisateur, le site, le poste, la connexion, l'heure | Barre d'état | textes de la barre d'état, horloge | Oui | Nouvelle barre d'état MainShellForm.cs:272-313 ; horloge :49, :868 (mise à jour toutes les 30 s) |
| Raccourcis clavier (F1, Maj+F1, Ctrl+S, Ctrl+F, Ctrl+P, F5, Ctrl+1…Ctrl+0) | Toute la fenêtre | touches | Oui | MainShellForm.cs:908-920, RunShortcut :936. F1 ouvre désormais l'aide ; la liste des raccourcis passe sur Maj+F1. |
| Afficher / personnaliser les raccourcis | Raccourcis clavier | **Raccourcis clavier (Maj+F1)**, **Rétablir les raccourcis par défaut**, **Enregistrer**, **Annuler** | Oui | MainShellForm.cs:690 ; ShortcutsDialog.cs:48-50, :105, RestoreDefaults :172, :187 |
| Se déconnecter | Menu de gauche | **Déconnexion** | PARTIEL | MainShellForm.cs:695 → :1124. Toujours : la confirmation affiche seulement le mot « Déconnexion » comme question (texte peu explicite). |
| Fermeture automatique après inactivité | — | automatique | Oui | MainShellForm.cs:144-150, CheckIdle :1062, SessionExpired :1073 (15 min, réglage) |
| Fermeture si le compte a été modifié | — | automatique | Oui | MainShellForm.cs:53 (contrôle chaque minute), Auth_SessionRevoked :1105 |
| Erreur inattendue | Toute la fenêtre | message traduit | Oui | Program.cs:183-202 (FailureMessages.KeyFor). Le message « connexion perdue » (Message.ConnectionLost) existe mais n'est pas utilisé. |

## Rôles, mode démonstration

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Consulter / modifier les droits d'un rôle | Utilisateurs › onglet **Rôles et droits** | (voir chapitre Administration) | Oui | UserAdministrationView.cs:110, BuildRolesTab :319 |
| Sous-traitance soumise à son propre droit | Menu de gauche | **Sous-traitance** | Oui | Nouveau. Entrée de menu avec le droit Samples.Subcontract (MainShellForm.cs:404) ; donné au Biologiste, retiré au Préleveur (ReferenceDataSeeder.cs:515, :564, :596, :621-630) |
| Réimprimer un compte rendu validé (Secrétariat / accueil, Biologiste) | rôles par défaut | droit « réimprimer un compte rendu validé » | Oui | Corrigé. ReferenceDataSeeder.cs:520, :574, :642 |
| Déclarer une non-conformité | rôles par défaut | droit de déclaration de NC | Oui | Nouveau. ReferenceDataSeeder.cs:523, :576, :604, :656 |
| Activer le mode démonstration | — | réglage d'installation « SeedDemoDataOnFirstRun » | Normal | Pas d'écran : réglage réservé à l'informatique, volontairement. |
| Désactiver les comptes demo.* sur base réelle | — | automatique | Oui | Au démarrage (DatabaseInitializer.cs:88) et à la connexion (AuthenticationService.cs:84) |
| Déclaration épidémiologique (droit « statistics.declaration » donné au Biologiste responsable) | — | — | **MANQUANT** | Toujours : le droit existe et est attribué, mais aucun écran ni fonction ne l'utilise. |

## Tableau de bord

| Scénario | Écran | Bouton / champ (libellé exact) | Présent ? | Remarque |
|---|---|---|---|---|
| Voir les alertes et l'activité du jour | Tableau de bord | **À traiter en priorité**, **Le flux du jour** | Oui | Écran refait. Indicateurs DashboardQueries.cs:78-117, :228-286 ; filtrés par droit (DashboardView.cs:134-157) |
| Saluer l'utilisateur, résumer la journée | Tableau de bord | bandeau « Bonjour … » | Oui | Nouveau. DashboardView.cs:535, textes du bandeau :645, :659 |
| Ouvrir l'écran d'une étape du flux | Tableau de bord | tuile d'étape (**Le flux du jour**) | Oui | Corrigé : le clic ouvre l'écran (DashboardView.cs:801, :805 ; cibles :105-126) |
| Ouvrir l'écran d'une alerte | Tableau de bord | bouton d'action de la ligne d'alerte | Oui | Corrigé. Bouton affiché seulement si l'utilisateur peut ouvrir l'écran cible (DashboardView.cs:935-948) ; apparence par type d'alerte :179-200, KindOf :207 ; tri :819-820 |
| Rien à traiter | Tableau de bord | « Rien à signaler » | Oui | Nouveau. DashboardView.cs:836, :860 |
| Actualiser | Tableau de bord | **Actualiser** (ou F5) | Oui | Corrigé : bouton visible (DashboardView.cs:322) |
| Signaler la configuration incomplète | Tableau de bord | bandeau avec bouton | Oui | Corrigé : texte traduit ; le bouton ouvre le catalogue (DashboardView.cs:297) |
| Libellé de la tuile étalonnage | Tableau de bord | « Étalonnages dépassés » | Oui | Corrigé : le libellé dit maintenant ce que compte la tuile. |
| Tuile « Articles à commander » limitée au site | Tableau de bord | « Articles à commander » | PARTIEL | Toujours : le calcul ne filtre pas sur le site (DashboardQueries.cs:184, toReorder) contrairement aux autres indicateurs. Stock vu pour tout le laboratoire. |

## Manques à corriger (par impact sur le travail quotidien)

1. **Pas de changement volontaire du mot de passe** une fois connecté : ajouter « Changer le mot de passe » dans le menu (la fenêtre existe déjà).
2. **Pas de changement de site** sans déconnexion, alors que la fonction existe : utile aux biologistes multi-sites.
3. Choix de langue du bandeau non mémorisé dans la fiche utilisateur.
4. Tuile « Articles à commander » non limitée au site.
5. Droit « déclaration épidémiologique » sans écran.
6. Confirmation de déconnexion peu explicite (« Déconnexion » seul comme question).
7. Message « connexion perdue » prévu mais jamais affiché (une coupure réseau donne le message d'erreur générique).

Corrigés depuis la vérification précédente : tuiles du Tableau de bord cliquables, bouton **Actualiser**, bandeau de configuration traduit, libellé « Étalonnages dépassés », droit de réimpression des comptes rendus pour Secrétariat / accueil et Biologiste.
