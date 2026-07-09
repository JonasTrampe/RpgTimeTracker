# RPG Zeitverfolgung

[![Build](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml/badge.svg)](https://github.com/JonasTrampe/RpgTimeTracker/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/github/license/JonasTrampe/RpgTimeTracker)](LICENSE)

> **Status: 1.0.0-alpha.** Funktioniert, wird aktiv genutzt, aber API/
> Protokoll/Speicherformat können sich zwischen Alpha-Versionen noch ändern.

Zwei AvaloniaUI-Desktop-Apps (Windows/Linux/macOS) für eine gemeinsame Spieluhr
(Timer, Wecker, wiederkehrende Intervalle) am Pen&Paper-Tisch: eine
SL-seitige Steuer-App und eine schreibgeschützte Anzeige-App für die
Spieler auf einem zweiten Bildschirm, inklusive Bild-/Video-Versand und einer
komplett getrennten Sound-Bibliothek für Hintergrundsounds/Effekte.

## Die beiden Apps

- **`RpgTimeTracker`** (SL/Host) — die Steuer-App. Besitzt die
  maßgebliche Spieluhr sowie alle Timer/Wecker/Intervalle, verwaltet die
  Medienbibliothek und betreibt den TCP-Server, an den sich beliebig viele
  Spieler-Clients gleichzeitig verbinden können.
- **`RpgTimeTracker.PlayerClient`** (Spieler-Client) — reine Anzeige-App.
  Verbindet sich per LAN/mDNS-Suche oder manueller IP-Eingabe mit dem Host,
  leitet daraus lokal ihre eigene Spieluhr ab und zeigt die Zeitleiste sowie
  vom Host gesendete Bilder/Videos an (Sounds spielen ohne eigene Anzeige).

Beide Apps kommunizieren über ein schlankes, längenpräfigiertes
JSON-RPC-Protokoll auf TCP; Details dazu (Frame-Format, komplette
Methodenliste, Medien-Streaming) stehen in [`docs/protocol.md`](docs/protocol.md).

## Funktionen

- **Spieluhr**: Datum & Uhrzeit frei setzbar, Start/Pause, läuft danach
  automatisch weiter.
- **Geschwindigkeit**: Zeitfaktor von 0,1× bis 300× (Presets + Slider) –
  z.B. "1 Sekunde real = 1 Stunde Spielzeit" während einer Rast.
- **Zeitsprung vor & zurück**: freies Sprung-Textfeld (`hh:mm:ss` bzw.
  `t.hh:mm:ss` für Tage, mit `-` für rückwärts) sowie Schnellauswahl-Buttons
  (+1 Std, +8 Std, +1 Tag, -1 Std, -1 Tag). Der jeweils letzte Zeitsprung
  (auch über Schnellauswahl oder Sprungmarken) lässt sich per Button wieder
  rückgängig machen.
- **Konfigurierbare Sprungmarken**: z.B. "Morgengrauen" (06:00), "Mittag"
  (12:00), "Abend" (18:00), "Mitternacht" – frei benennbar, editierbar und
  löschbar. Pro Marke springt ▶ zum nächsten und ◀ zum vorherigen Auftreten
  dieser Uhrzeit.
- **Timer**: beliebig viele Countdown-Timer mit Fortschrittsbalken und
  Restzeitanzeige, laufen an der Spielzeit (nicht Echtzeit) und reagieren
  korrekt auf Zeitsprünge in beide Richtungen (nur solange sie aktiv laufen).
  Optional lässt sich pro Timer ein Bild/Video hinterlegen, das beim Ablauf
  automatisch an alle Spieler gesendet wird (siehe Trigger-Medien unten);
  ein manuelles Zurücksetzen des Timers schließt dieses Medium wieder, falls
  es noch angezeigt wird.
- **Wecker**: beliebig viele Wecker mit fester Zielzeit, optional
  wiederkehrend (z.B. alle 24 Spielstunden). Springt man zeitlich vor die
  Zielzeit zurück, "entschärft" sich ein bereits ausgelöster Wecker wieder.
- **Speichern & Laden**: kompletter Zustand (Spielzeit, Geschwindigkeit,
  alle Timer, Wecker und Sprungmarken) lässt sich über die 💾/📂-Buttons
  oben links als JSON-Datei exportieren bzw. wieder importieren.
- **Netzwerk-Spieleranzeige**: der SL startet in der Steuer-App einen
  TCP-Server auf einem frei wählbaren Port mit einem frei wählbaren
  Servernamen (wird im mDNS-/LAN-Announcement mitgeschickt und erscheint so
  in der Serverliste der Clients – nützlich, sobald mehrere Hosts im
  selben Netzwerk laufen); Spieler-Clients finden ihn automatisch per mDNS-
  und LAN-Broadcast-Suche (beide Quellen werden zu einem Eintrag pro Server
  zusammengeführt, mit Hinweis, über welchen Weg er gefunden wurde) oder
  verbinden sich manuell per IP:Port. Verbundene Clients werden in der
  Steuer-App mit Verbindungszeitpunkt aufgelistet und lassen sich einzeln
  per Klick trennen. Bricht die Verbindung unerwartet ab (WLAN-Aussetzer,
  Host-Neustart), versucht der Client automatisch mit wachsendem Abstand
  erneut zu verbinden und bekommt bei Erfolg denselben vollständigen
  Zustand wie ein frisch verbundener Client – ein manuelles "Trennen"
  hingegen bleibt getrennt. Vor dem Schließen fragt der Client, sofern noch
  zwischengespeicherte Bilder/Videos vorhanden sind, ob diese Temp-Dateien
  gelöscht werden sollen. Optional lässt sich in den Netzwerk-Einstellungen
  ein PIN festlegen, den ein Client beim Verbinden kennen muss (leer =
  kein PIN nötig) – reine LAN-Zugangskontrolle gegen versehentliches
  Mitverbinden fremder Geräte, keine echte Verschlüsselung/Authentifizierung.
  Client und Host tauschen beim Verbindungsaufbau außerdem ihre
  Protokoll-Version aus; passt sie nicht zusammen, lehnt der Host die
  Verbindung mit einer klaren Fehlermeldung statt undefiniertem Verhalten ab.
- **Bild/Video an Spieler senden**: in der Medienbibliothek ein Bild oder
  Video auswählen (bis ca. 1900 MB) – es öffnet sofort ein eigenes
  Medien-Fenster bei allen verbundenen Spieler-Clients (Bilder inline,
  Videos über die eingebettete VLC-Wiedergabe), optional auch lokal beim SL
  (nur wenn gerade kein Spieler verbunden ist, um doppelte Anzeige zu
  vermeiden). Das Fenster lässt sich vom SL aus per "Entfernen" bei allen
  Beteiligten schließen; ob es im Vollbild oder als normales Fenster
  angezeigt wird, stellt jede Seite (SL und jeder Spieler) unabhängig und
  lokal ein. Der "Spieler-Vollbild"-Schalter beim SL schaltet sowohl alle
  verbundenen Spieler-Clients als auch das eigene Spielerfenster fullscreen;
  jedes Fenster (SL und Client) lässt sich jederzeit rein lokal per
  Escape-Taste (oder beim SL zusätzlich per Button) wieder verkleinern, ohne
  den Vollbild-Zustand der anderen Seite zu beeinflussen.
  Der Spieler-Client meldet dem Host zudem zurück, wann ein
  Video wirklich zu spielen beginnt und endet (echte VLC-Dauer statt
  Schätzung), worauf z.B. eine automatische Uhr-Pause während des Videos
  basiert.
- **Medienbibliothek**: Bilder/Videos vorab zur Bibliothek hinzufügen
  (bleibt über Neustarts erhalten, inkl. Vorschaubild bei Bildern und
  freier Umbenennung) – ein Doppelklick auf einen Eintrag zeigt ihn sofort
  bei SL und allen verbundenen Spielern an. Lässt sich über "Exportieren"/
  "Importieren" (Reiter "Bibliothek") als eigener, frei wählbarer Ordner
  sichern bzw. auf einem anderen Rechner wieder einlesen.
- **Diashow/Galerie (Bild/Video)**: jedes gesendete Bild/Video bleibt
  erhalten, statt vom nächsten ersetzt zu werden – SL und jeder Spieler
  blättern unabhängig voneinander per Pfeiltaste oder Klick im
  Spielerfenster durch alles, was schon gezeigt wurde (rein lokal, ohne den
  Host zu fragen). Der SL kann ein Element bei allen "hervorheben" (ein
  Sprung-Vorschlag, kein Zwang) oder ganz aus der Galerie entfernen, und
  eine optionale Sekunden-pro-Bild-Einstellung schaltet bei allen
  automatisch weiter (Videos werden davon nie unterbrochen). Ereignis-
  Medien (Timer/Wecker/Intervall) haben Vorrang: sie unterbrechen die
  Galerie-Anzeige kurz, ohne selbst Teil davon zu werden, und die Galerie
  läuft erst nach dem Zurücksetzen des auslösenden Elements weiter.
- **Trigger-Medien**: Timern, Weckern und Intervall-Ereignissen lässt sich
  ein Bild/Video (oder ein Sound, direkt von der Platte gewählt) zuweisen,
  das automatisch beim Auslösen gesendet wird, wahlweise mit Vollbild und
  Endlosschleife statt automatischem Schließen. Für Video zusätzlich mit
  Uhr-Pause während der Wiedergabe (pausiert die Uhr exakt bis zum
  tatsächlichen Ende, nicht nur bis zu einer geschätzten Dauer, auch wenn
  das Video nur lokal beim SL läuft).
- **Sound-Bibliothek (eigener Reiter "Sounds", komplett getrennt von
  Bild/Video)**: Sounds per Einfachklick an alle Spieler senden (und
  lokal beim SL, wenn gerade kein Spieler verbunden ist) – ganz ohne
  eigenes Anzeigefenster, ohne ein gerade gezeigtes Bild/Video zu ersetzen
  und ohne selbst durch ein neues Bild/Video oder einen weiteren Sound
  gestoppt zu werden. Beliebig viele Sounds laufen gleichzeitig. Jede
  Bibliothekskachel hat ein ⚙-Symbol mit den Einstellungen (Lautstärke,
  Endlosschleife oder feste Wiederholungszahl, Zurechtschneiden – Start/Ende
  in Sekunden – und ein "SL-Test"-Button zum lokalen Vorhören ohne zu
  senden); das öffnet sich als Overlay, ohne die Kachel-Anordnung zu
  verschieben. Ein Sound, der über das "Sound"-Feld eines Timers/Weckers/
  Intervalls ausgelöst wird (statt der eingebauten Töne Pling/Glocke/
  Digital), wird ebenfalls an die Spieler gesendet und endet automatisch,
  wenn das auslösende Element zurückgesetzt wird. Auch die Sound-Bibliothek
  lässt sich exportieren/importieren wie die Medienbibliothek.
- **Statusleiste (unten, bei jedem Reiter sichtbar)**: Serverstatus
  (läuft/gestoppt, Adresse:Port, Anzahl verbundener Spieler), das gerade
  gezeigte Bild/Video mit Schließen-Button, ein Sound-Zähler, dessen
  Flyout alle gerade laufenden Sounds mit Live-Lautstärkeregler auflistet
  und erlaubt, jeden einzeln oder alle zusammen zu stoppen (auch bei allen
  verbundenen Spieler-Clients), eine Glocke, deren Flyout die zuletzt
  ausgelösten Aktions-Meldungen ("... gesendet" usw.) als Verlauf zeigt,
  sowie zwei Vollbild-Schalter (eigene SL-Vorschau lokal, Spieler-Anzeige
  remote). Bewusst außerhalb der Reiter-Inhalte platziert, damit nichts
  "springt", während im Hintergrund Sounds beginnen/enden oder ein Medium
  wechselt.
- **Ingame-Kalender (eigener Host-Reiter "Kalender")**: frei definierbare
  Termine auf Ingame-Datum/Uhrzeit mit Titel, Beschreibung, Icon, Farbe,
  Wiederholung (täglich/wöchentlich/monatlich/jährlich, optional mit
  Enddatum) und optionalem Bild-/Video-/Sound-Auslöser (löst wie ein
  Timer/Wecker das übliche Event-Medium aus). Im gemeinsamen Kopfbereich von
  Host-Spielerfenster und Client erscheint ein kleiner Umschalter
  "Zeitliste ↔ Kalender" – aber nur, wenn mindestens ein für Spieler
  sichtbarer Termin existiert; sonst bleibt es bei der reinen Zeitliste,
  keine leere Kalender-Option. Persistiert im normalen Spielstand und
  zusätzlich separat als JSON exportierbar/importierbar; Änderungen werden
  automatisch an alle verbundenen Spieler gesendet, neu verbundene Clients
  erhalten den vollständigen Kalender beim Verbinden.
- **Über-Dialog (Host und Client, "ℹ"-Button)**: Titel/Beschreibung aus einer
  pro App eigenen Markdown-Datei (`About/AboutContent.md`, per
  ClassIsland.Markdown.Avalonia gerendert), Versionsnummer sowie die eigene
  Projektlizenz und die Lizenzhinweise aller verwendeten
  Drittanbieter-Bibliotheken (`LICENSE`/`THIRD-PARTY-NOTICES.txt`, dieselben
  Dateien, aus denen auch dieses Repo seine Lizenzangaben bezieht – nicht
  dupliziert getippt), jeweils in einem zugeklappten Abschnitt.
- **Medienfenster (Host-Vorschau und Client)**: zeigen zusätzlich zum Bild/
  Video eine kleine Uhr-Anzeige (aktuelle Spielzeit bleibt auch bei
  vollflächigem Medium sichtbar) sowie einen eigenen Minimieren-Button neben
  dem Schließen-Button, da im Vollbild/randlosen Modus kein Betriebssystem-
  Fenstertitel mit nativem Minimieren zur Verfügung steht.
- **Sound je Element**: Timer/Wecker/Intervalle spielen beim Auslösen einen
  wählbaren Sound ab - entweder einen der eingebauten Standardtöne (Pling/
  Glocke/Digital, rein lokal beim SL) oder einen Sound aus der
  Sound-Bibliothek (wird an Spieler gesendet, siehe oben). Bei den
  eingebauten Tönen wahlweise mehrfach hintereinander (einstellbare
  Wiederholung, 0 = endlos – stoppt dann erst, wenn das jeweilige Element
  zurückgesetzt bzw. quittiert oder gelöscht wird); ein Bibliotheks-Sound
  mit Wiederholung 0 loopt entsprechend endlos, bis das Element
  zurückgesetzt wird.
- **Vorwarnung (nur für SL sichtbar)**: optional ein Hinweis-Banner ein
  konfigurierbares Zeitfenster (Minuten Spielzeit) bevor ein Timer/Wecker/
  Intervall auslöst – wird nie an Spieler gesendet.
- **Sitzungsprotokoll**: optional aufzuzeichnende Liste ausgelöster Ereignisse
  (Zeitsprünge, Auslösungen, gesendete Medien) mit Spielzeit-Stempel; Export
  als Textdatei an einen frei wählbaren Speicherort.
- **Stile (JSON-Designs)**: alle 8 mitgelieferten Stile (Shadowrun, Mittelalter,
  Warhammer 40k, Alien, Steampunk, Postapokalypse, Eldritch Horror, Gothic
  Vampire) sowie beliebig viele eigene liegen als `theme.json` (Farben,
  Verlaufsfarben, Schriften, benannte Hintergrund-/Buttonbilder) vor –
  die mitgelieferten unter `SampleThemes/`, eigene SL-Designs zusätzlich in
  `%AppData%/RpgTimeTracker/Themes/`, wo sie automatisch in der Stil-Auswahl
  erscheinen. Wählt der SL ein Design, wird es auch an verbundene
  Spieler-Clients übertragen (per Design-Id) – der Client zeigt es korrekt
  an, wenn er dieselbe `theme.json` lokal besitzt (mitgeliefert für alle 8
  Standard-Stile; ein komplett neues SL-Design muss aktuell manuell auch auf
  die Spieler-Rechner kopiert werden).
- **Ambiente-Automatik (optional)**: wechselt bei einem Design mit mehreren
  benannten Hintergründen automatisch zwischen Tag- und Nacht-Hintergrund,
  je nach Spielzeit. Bei Designs mit nur einem Hintergrund (aktuell alle
  bleibt sie ohne sichtbare Wirkung.

Alle Timer und Wecker werden bei jedem Tick der Spieluhr *und* bei jedem
manuellen Zeitsprung mit dem entsprechenden Spielzeit-Delta aktualisiert –
wird die Uhr beschleunigt, verlangsamt oder springt vor/zurück, ändert sich
automatisch auch das Verhalten aller Timer/Wecker. Der Spieler-Client
rekonstruiert dasselbe Verhalten lokal aus den vom Host gesendeten Deltas,
ohne bei jedem Tick eine Netzwerknachricht zu benötigen.

## Voraussetzungen

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (siehe `global.json`;
  alle drei Projekte zielen auf `net10.0`)
- Internetzugriff beim ersten Build (NuGet-Pakete: Avalonia, CommunityToolkit.Mvvm,
  LibVLCSharp, Serilog)
- Für Video-/Audio-Wiedergabe (Bild funktioniert immer ohne Zusatzsoftware):
  - **Windows**: keine zusätzliche Installation nötig – die native VLC-Bibliothek
    wird über das NuGet-Paket `VideoLAN.LibVLC.Windows` mitgeliefert.
  - **Linux**: VLC muss systemweit installiert sein, z.B.
    `sudo apt install vlc` (Debian/Ubuntu) oder `sudo dnf install vlc` (Fedora).
    Ohne installiertes VLC funktioniert die Bildanzeige weiterhin, Video-/
    Audio-Versand zeigt aber eine Fehlermeldung statt abzuspielen.

## Bauen & Starten

```bash
dotnet restore RpgTimeTracker.sln

# SL/Host-App:
dotnet run --project RpgTimeTracker/RpgTimeTracker.csproj

# Spieler-Client:
dotnet run --project RpgTimeTracker.PlayerClient/RpgTimeTracker.PlayerClient.csproj
```

Für eine eigenständige Windows-Exe (jeweils für Host bzw. Client):

```bash
dotnet publish RpgTimeTracker/RpgTimeTracker.csproj -c Release -r win-x64 --self-contained true
dotnet publish RpgTimeTracker.PlayerClient/RpgTimeTracker.PlayerClient.csproj -c Release -r win-x64 --self-contained true
```

Für Linux entsprechend `-r linux-x64`.

## Projektstruktur

```
RpgTimeTracker.Shared/          # von beiden Apps referenziert, nichts dupliziert
├── Network/    # NetworkFrame, MediaLimits, DTOs
├── Rpc/        # RpcMessage/RpcNotification<T> (JSON-RPC-Umschlag)
├── Models/     # TimerItem, AlarmItem, IntervalEventItem, MediaKind
├── Services/   # GameClockService, MediaTypeHelper, VlcMediaService
├── Visuals/    # VisualItemHelper (Bootstrap-Icons)
├── Logging/    # AppLogging (Serilog-Bootstrap für beide Apps)
└── Styles/     # AppStyles.axaml - gemeinsames Fenster-Chrome

RpgTimeTracker/                 # SL/Host-App
├── Models/         # TriggerMediaConfig u.a. host-only Modelle
├── Network/        # TcpPlayerServerService, PlayerMdnsAnnouncer, LanDiscoveryResponder
├── Persistence/    # AppStateDto (Speichern/Laden-Schema)
├── ViewModels/      # MainWindowViewModel, TimerItemViewModel, ConnectedClientItemViewModel, ...
├── Views/           # MainWindow.axaml(.cs)
├── App.axaml / App.axaml.cs
└── Program.cs

RpgTimeTracker.PlayerClient/    # Spieler-Client-App
├── Network/        # PlayerTcpClientService, MdnsDiscoveryService
├── Services/       # ClientSettingsService, ClientThemeService
├── ViewModels/      # ClientMainWindowViewModel
├── Views/           # ClientMainWindow.axaml(.cs), MediaWindow.axaml(.cs)
├── App.axaml / App.axaml.cs
└── Program.cs
```

## Dokumentation

Ausführlichere technische Dokumentation liegt in [`docs/`](docs/):

- [`docs/architecture.md`](docs/architecture.md) — Systemüberblick: die drei
  Projekte, ihre Zuständigkeiten, das Threading-Modell und der Datenfluss.
- [`docs/protocol.md`](docs/protocol.md) — Frame-Format und die komplette
  JSON-RPC-Methodenliste (beide Richtungen), Medien-Streaming, Uhr-Sync.
- [`docs/design-decisions.md`](docs/design-decisions.md) — warum bestimmte
  Dinge so gebaut sind, wie sie sind, inklusive einiger Entscheidungen, die
  frühere Lösungen bewusst wieder rückgängig gemacht haben.

## Mögliche Erweiterungen

- Sound/Popup-Benachrichtigung bei Timer-Ablauf bzw. Wecker-Auslösung
- Eigener Fantasy-Kalender (andere Monatslängen, Wochentage, Feiertage)
  statt des Standard-`DateTime`
- Mehrere parallele "Sitzungen"/Kampagnen mit eigener Spielzeit
- Automatisches Speichern beim Schließen der App / zuletzt genutzte Datei merken
- Video-Chunking mit progressiver Wiedergabe schon während der Übertragung
