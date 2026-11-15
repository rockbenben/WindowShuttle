# WindowShuttle

> Fenster ohne Ziehen zwischen Bildschirmen bewegen — einzeln oder ganze Bildschirme auf einmal

[⬇ Für Windows herunterladen](https://github.com/rockbenben/WindowShuttle/releases/latest)

[English](../../README.md) · [简体中文](../../README.zh.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · **Deutsch** · [Español](README.es.md) · [Français](README.fr.md) · [Italiano](README.it.md) · [Nederlands](README.nl.md) · [Português](README.pt.md) · [Русский](README.ru.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [ไทย](README.th.md) · [Bahasa Indonesia](README.id.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #033](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23033-1f6feb)](https://github.com/rockbenben/365opensource)

![Bildschirmkarte der verbundenen Monitore nach relativer Schreibtischposition: Jede Karte listet ihre Fenster als Chips auf; der Hauptmonitor ist markiert, der Bildschirm mit dem Cursor blau umrandet](../images/main-en.png)

**Ein Fenster auf einen anderen Monitor zu bringen, sollte kein anstrengendes Ziehen über den Schreibtisch sein.** Halten Sie `Ctrl` und die rechte Maustaste gedrückt, schnippen Sie kurz in Richtung des Zielbildschirms, und das Fenster unter Ihrem Mauszeiger ist bereits dort. Möchten Sie den gesamten Bildschirm tauschen? Ein einziger Tastendruck tauscht alle Fenster zwischen Ihrem aktuellen Bildschirm und dem Hauptmonitor. WindowShuttle sitzt unauffällig im Infobereich (System Tray) und funktioniert sofort ohne Installation.

- **Schnippen statt Zielen.** `Ctrl` + rechte Maustaste, Mauszeiger in Richtung Bildschirm werfen, loslassen. Da Sie bereits darauf deuten, müssen Sie nicht erst Monitornummern umrechnen. Falsch gezielt? `Ctrl` + Klick mit mittlerer Maustaste macht es rückgängig.
- **Einzelnes Fenster oder ganzer Bildschirm, dasselbe Tool.** Sieben Aktionen auf einer Skala: aktuelles Fenster senden, zwei Bildschirme komplett tauschen oder alle Fenster nach dem Trennen eines Monitors einsammeln. Bedienbar über Tastatur, Mausgesten, Tray-Menü oder CLI.
- **Geometrie wird neu berechnet, nicht blind kopiert.** Gemischte DPI und Auflösungen sind der Standardfall: Fenstergrößen werden für das Ziel skaliert, Maximiert bleibt maximiert, minimierte Fenster werden sauber wiederhergestellt und angedockte Fenster (Snap) behalten ihre Hälfte oder ihr Viertel.
- **Die Übersichtskarte ist das Bedienfeld.** Das obere Panel zeichnet Ihre Monitore maßstabsgetreu nach ihrer realen Schreibtischposition. Ziehen Sie ein Fenster von einer Karte auf eine andere, um es zu verschieben. Die Karte aktualisiert sich nach jeder Bewegung automatisch.
- **Keine Bindung, vollkommen offline.** Eine einzige portable Exe-Datei, kein Installer, kein Konto. Es bleibt vollständig offline, es sei denn, Sie aktivieren die Update-Prüfung. Nur für Windows.

## Kompatibilität

| Bereich | Unterstützung |
|---|---|
| **Betriebssystem** | Windows 10 oder 11 |
| **Architektur** | x64 (nur x64-Builds werden veröffentlicht, kein natives ARM64) |
| **.NET** | Standalone-Version benötigt nichts; die schlankere Version benötigt die [.NET 10 Desktop-Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| **Monitore** | Mindestens 2 Monitore empfohlen. Aktionen zwischen Bildschirmen erfordern mindestens zwei Bildschirme |
| **Gemischte DPI / Auflösungen** | Voll unterstützt: Geometrie wird für den Zielbildschirm neu berechnet |
| **Administratorfenster** | Erfordern, dass WindowShuttle selbst mit erhöhten Rechten läuft (siehe [Bekannte Einschränkungen](#bekannte-einschränkungen)) |

## Download

**[⬇ Neueste Version herunterladen](https://github.com/rockbenben/WindowShuttle/releases/latest)** —— Windows x64.

- **`WindowShuttle-<version>-win-x64.zip`** —— Standalone-Version mit integrierter .NET-Runtime; entpacken und sofort starten.
- **`WindowShuttle-<version>-win-x64-needs-dotnet10.zip`** —— Schlanke Version ohne Runtime; erfordert die installierte [.NET 10 Desktop-Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- **`WindowShuttle.exe`** —— Die gleiche Anwendung als reine Einzeldatei ohne ZIP.

Kein Installer erforderlich. Ohne Codesignatur warnt Windows SmartScreen beim ersten Start: Klicken Sie auf **Weitere Informationen → Trotzdem ausführen**.

**Fehlerprotokoll.** Tritt ein Fehler auf, stürzt die App nicht ab, sondern fängt ihn ab und zeigt eine Benachrichtigung. Details werden in `%APPDATA%\WindowShuttle\crash.log` protokolliert.

**Deinstallation.** Einfach die EXE löschen. Falls *Als Administrator* aktiviert war, deaktivieren Sie zuerst *Mit Windows starten* in den Einstellungen, um die geplante Aufgabe sauber zu entfernen.

## Die sieben Aktionen

| Aktion | Tastenkürzel | Mausgeste | Beschreibung |
|---|---|---|---|
| **Bildschirme tauschen** | `Win`+`Shift`+`X` | `Ctrl`+Rechtsklick nach links | Tauscht alle Fenster zwischen aktuellem Bildschirm und Hauptmonitor |
| **Vorderste Fenster tauschen** | `Win`+`Shift`+`Z` | `Ctrl`+Rechtsklick nach rechts | Tauscht nur die beiden jeweils vordersten Fenster |
| **Zum Hauptbildschirm senden** | *(nicht belegt)* | *(nicht belegt)* | Sendet das aktive Fenster zum Hauptbildschirm |
| **Zum nächsten Bildschirm senden** | *(nicht belegt)* | *(nicht belegt)* | Sendet das Fenster zum nächsten rechten Monitor |
| **Per Richtung senden** | *(nur Geste)* | `Ctrl`+Rechtsklick wischen | In Richtung des Zielbildschirms wischen, um das Fenster dorthin zu bewegen |
| **Alle einsammeln** | *(nicht belegt)* | *(nicht belegt)* | Sammelt alle Fenster von externen Monitoren auf dem Hauptbildschirm |
| **Rückgängig** | *(nicht belegt)* | `Ctrl`+Mittelklick | Macht den letzten Verschiebevorgang rückgängig |
| **Bildschirmnummer** | *(nicht belegt)* | *(nicht belegt)* | Zeigt kurz die Monitornummer in der Bildschirmmitte an |

## Einstellungen

- **Beim Schließen im Infobereich minimieren** (Standard: Ein) — Schließen-Button minimiert in den System Tray.
- **Beim Start nach Updates suchen** (Standard: Aus) — Prüft beim Start einmalig GitHub auf neuere Versionen.
- **Mit Windows starten** (Standard: **Ein**) — Startet WindowShuttle bei der Anmeldung mit `--tray`.
- **Als Administrator** (Standard: Aus) — Ersetzt den Registry-Autostart durch eine geplante Aufgabe mit höchsten Rechten (einmalige UAC-Bestätigung). Löst alle UIPI-Probleme mit Administratorfenstern.
- **Fenster retten, wenn ein Bildschirm getrennt wird** (Standard: Ein) — Holt außerhalb des Bildschirms verlorene Fenster automatisch zurück.
- **Vollbild-Apps überspringen** (Standard: Ein) — Verhindert, dass Spiele oder Vollbildvideos unbeabsichtigt verschoben werden.

## Befehlszeile (CLI)

| Befehl | Beschreibung |
|---|---|
| `WindowShuttle.exe list` | Gibt Monitor- und Fensterlayout im JSON-Format aus |
| `WindowShuttle.exe swap` | Tauscht Fenster des aktuellen Bildschirms mit dem Hauptmonitor |
| `WindowShuttle.exe swap-top` | Tauscht nur die vordersten Fenster |
| `WindowShuttle.exe to-primary` | Sendet das aktive Fenster zum Hauptmonitor |
| `WindowShuttle.exe gather` | Holt alle Fenster auf den Hauptmonitor zurück |
| `WindowShuttle.exe to-next` | Sendet das aktive Fenster zum nächsten Monitor |
| `WindowShuttle.exe to <n>` | Sendet das aktive Fenster zu Monitor n |
| `WindowShuttle.exe identify` | Zeigt Identifikationsnummern auf allen Bildschirmen |
| `WindowShuttle.exe undo` | Macht die letzte Verschiebung rückgängig |
| `WindowShuttle.exe --tray` | Startet minimiert im Tray |
| `WindowShuttle.exe --help` | Zeigt die Hilfe an |

## Bekannte Einschränkungen

- **Administratorfenster**: Ohne erhöhte Rechte kann WindowShuttle keine Fenster von Administratorprogrammen (Task-Manager etc.) verschieben (Windows UIPI-Sicherheitsbarriere). Lösung: In den Einstellungen *Als Administrator* aktivieren.
- **Gesten bei aktivem Administratorfenster**: Ist ein Admin-Fenster im Vordergrund, blockiert Windows globale Maushooks. Ein erhöhter Start behebt dies.
- **Vollbild-Apps**: Werden standardmäßig übersprungen, um laufende Spiele nicht zu stören.
- **Virtuelle Desktops**: Fenster auf anderen Desktops werden absichtlich ignoriert.

## Sprachen

Unterstützt 18 Sprachen (English, 简体中文, 繁體中文, 日本語, 한국어, Español, Français, Deutsch, Italiano, Português, Русский, العربية, हिन्दी, Bahasa Indonesia, Tiếng Việt, ไทย, Türkçe, Nederlands).

## Aus Quellcode kompilieren

Erfordert das .NET 10 SDK (nur Windows).

```bash
dotnet build WindowShuttle.sln -c Debug
dotnet test tests/WindowShuttle.Core.Tests
WindowShuttle.exe --smoke
```

## Über den 365 Open Source Plan

Projekt **#033** des [365 Open Source Plan](https://github.com/rockbenben/365opensource) — eine Person + KI, über 300 Open-Source-Projekte in einem Jahr.

[Reiche deine Idee ein →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)
