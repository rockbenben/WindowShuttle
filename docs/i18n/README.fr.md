# WindowShuttle

> Déplacez les fenêtres d'un écran à l'autre sans les glisser — une par une ou tout l'écran d'un coup

[⬇ Télécharger pour Windows](https://github.com/rockbenben/WindowShuttle/releases/latest)

[English](../../README.md) · [简体中文](../../README.zh.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Deutsch](README.de.md) · [Español](README.es.md) · **Français** · [Italiano](README.it.md) · [Nederlands](README.nl.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [ไทย](README.th.md) · [Bahasa Indonesia](README.id.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #033](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23033-1f6feb)](https://github.com/rockbenben/365opensource)

![Carte des moniteurs connectés selon leur disposition physique réelle](../images/main-en.png)

**Déplacer une fenêtre vers un autre moniteur ne devrait pas nécessiter un pénible glisser-déposer à travers tout le bureau.** Maintenez `Ctrl` et le clic droit, donnez un léger coup de souris vers l'écran souhaité, et la fenêtre sous votre curseur y est déjà. Besoin d'échanger tout l'écran ? Un simple raccourci permute toutes les fenêtres entre votre écran actuel et l'écran principal. WindowShuttle réside dans la barre d'état système et fonctionne immédiatement sans installation.

- **Balayez sans viser.** `Ctrl` + clic droit, lancez la souris vers un écran et relâchez. Vous pointiez déjà vers lui, aucun numéro d'écran à calculer. Mauvais geste ? `Ctrl` + clic du milieu annule immédiatement.
- **Une fenêtre ou tout l'écran, le même outil.** Sept actions adaptées : envoyer la fenêtre active, intervertir deux écrans complets ou rassembler toutes les fenêtres après avoir débranché un écran. Accessibles par raccourci clavier, souris, menu ou ligne de commande.
- **Géométrie recalculée, pas bêtement copiée.** La gestion d'écrans à DPI et résolutions mixtes est la norme : les fenêtres sont redimensionnées pour la cible, le mode maximisé reste maximisé et l'ancrage (Snap) conserve sa moitié ou son quart d'écran.
- **La carte des écrans est votre panneau de contrôle.** Le panneau supérieur affiche vos moniteurs dans leurs dimensions réelles. Glissez une fenêtre d'une carte à l'autre pour la transférer.
- **Aucun engagement, totalement hors ligne.** Un exécutable portable unique, sans installeur ni compte utilisateur. Reste complètement hors ligne sauf si vous vérifiez manuellement les mises à jour. Conçu exclusivement pour Windows.

## Compatibilité

| Domaine | Prise en charge |
|---|---|
| **Système** | Windows 10 ou 11 |
| **Architecture** | x64 uniquement |
| **.NET** | Version autonome sans prérequis ; version légère requiert [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Moniteurs** | 2 moniteurs ou plus |
| **DPI / Résolutions mixtes** | Entièrement pris en charge avec recalcul de géométrie |
| **Fenêtres Administrateur** | Nécessite d'exécuter WindowShuttle avec élévation de privilèges |

## Téléchargement

**[⬇ Obtenir la dernière version](https://github.com/rockbenben/WindowShuttle/releases/latest)** —— Windows x64.

- **`WindowShuttle-<version>-win-x64.zip`** —— Version autonome tout-en-un avec runtime .NET inclus. Décompressez et lancez.
- **`WindowShuttle-<version>-win-x64-needs-dotnet10.zip`** —— Version légère sans runtime, requiert [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- **`WindowShuttle.exe`** —— L'exécutable portable direct.

Sans signature payante, Windows SmartScreen affichera un message au premier lancement : cliquez sur **Informations complémentaires → Exécuter quand même**.

## Les sept actions

| Action | Raccourci clavier | Geste de souris | Description |
|---|---|---|---|
| **Échanger les écrans** | `Win`+`Shift`+`X` | `Ctrl`+clic droit vers la gauche | Permute toutes les fenêtres de l'écran actif avec l'écran principal |
| **Échanger les fenêtres de devant** | `Win`+`Shift`+`Z` | `Ctrl`+clic droit vers la droite | Échange uniquement les fenêtres au premier plan de chaque écran |
| **Envoyer vers le principal** | *(non assigné)* | *(non assigné)* | Déplace la fenêtre active vers l'écran principal |
| **Envoyer vers l'écran suivant** | *(non assigné)* | *(non assigné)* | Déplace la fenêtre vers l'écran de droite (boucle à gauche) |
| **Envoyer par direction** | *(geste seul)* | `Ctrl`+balayage clic droit | Déplace la fenêtre sous le curseur dans la direction balayée |
| **Tout rassembler** | *(non assigné)* | *(non assigné)* | Rapatrie toutes les fenêtres secondaires sur l'écran principal |
| **Annuler** | *(non assigné)* | `Ctrl`+clic milieu | Annule le déplacement précédent et restaure les fenêtres |
| **Numéro d'écran** | *(non assigné)* | *(non assigné)* | Affiche brièvement le numéro d'identification au centre de chaque écran |

## Paramètres

- **Garder en arrière-plan à la fermeture** (Activé) — Fermer la fenêtre la masque dans la zone de notification.
- **Lancer avec Windows** (Activé) — Démarre discrètement dans la zone de notification au démarrage.
- **En administrateur** (Désactivé) — Enregistre une tâche planifiée avec privilèges élevés pour déplacer sans encombre les fenêtres administrateur (UIPI).
- **Récupérer les fenêtres hors écran** (Activé) — Rapatrie automatiquement les fenêtres orphelines lors du débranchement d'un moniteur.
- **Ignorer les applications en plein écran** (Activé) — Évite de perturber les jeux ou vidéos en plein écran.

## Ligne de commande (CLI)

`WindowShuttle.exe list`, `swap`, `swap-top`, `to-primary`, `gather`, `to-next`, `to <n>`, `identify`, `undo`, `--tray`, `--help`.

## À propos du 365 Open Source Plan

Projet **#033** du [365 Open Source Plan](https://github.com/rockbenben/365opensource) — une personne + l'IA, plus de 300 projets open source en un an.

[Proposez votre idée →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
