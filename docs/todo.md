# TODO

Offene Ideen und Aufgaben für RpgTimeTracker, jenseits akuter Bugs. Kein
Ersatz für Issues auf GitHub, sobald das Repo dort liegt — eher die
Langzeit-Gedächtnisstütze für Dinge, die noch nicht spruchreif genug für ein
Issue sind. Lizenz-/Attribution-Aufgaben stehen separat in
[`legal-todo.md`](legal-todo.md).

## Features

- [ ] Sound-/Popup-Benachrichtigung bei Timer-Ablauf bzw. Wecker-Auslösung
      (auch wenn das Hauptfenster gerade nicht im Fokus ist)
- [ ] Eigener Fantasy-Kalender (andere Monatslängen, Wochentage, Feiertage)
      statt des Standard-`DateTime` — größerer Umbau, betrifft
      `GameClockService` auf beiden Seiten sowie das Sprungmarken-Feature
- [ ] Automatisches Speichern beim Schließen der App / zuletzt genutzte
      Datei beim Start automatisch wieder laden
- [ ] Sitzungs-/Kampagnen-Vorlagen: vorgefertigte Bündel aus
      Timern/Weckern/Theme (z.B. "Reise-Modus", "Dungeon-Crawl") zum
      Einklicken statt jedes Mal neu anlegen
- [x] Erweiterter Undo-Verlauf über den zuletzt gemachten Zeitsprung hinaus —
      erledigt: `MainWindowViewModel` hat jetzt einen echten Undo-*und*
      Redo-Stack (`_jumpUndoStack`/`_jumpRedoStack`), beide beliebig oft
      hintereinander klickbar; auch die manuelle Zeiteingabe
      (`ApplyManualDateTime`) läuft jetzt über denselben Delta-Mechanismus
      und ist damit ebenfalls rückgängig machbar. Ein neuer Sprung leert den
      Redo-Stack (klassisches Editor-Verhalten); Laden eines Spielstands
      leert beide Stacks, da sich eine alte Sprung-Historie nicht mehr auf
      den neu geladenen Timer-/Wecker-Zustand bezieht.

## Netzwerk & Robustheit

- [x] Video-Chunking mit progressiver Wiedergabe schon während der
      Übertragung (nicht erst nach vollständigem Empfang) — erledigt,
      `StartVideo` spielt die noch wachsende Tempdatei sofort ab
- [x] Auto-Reconnect mit Backoff im PlayerClient, wenn die TCP-Verbindung
      unerwartet abbricht — erledigt (`PlayerTcpClientService.ReconnectLoopAsync`,
      2s–20s Backoff, nur bei unerwarteter Trennung, nicht bei manuellem
      Trennen); reconnected Clients bekommen automatisch einen vollen
      `session.snapshot` über den bestehenden Catch-up-Pfad
- [x] Optionaler PIN/Passcode beim Verbindungsaufbau — erledigt, zusammen mit
      der Protokoll-Version in einem gemeinsamen `session.hello`/
      `session.helloRejected`-Handshake (siehe unten); Host-Einstellung
      `MainWindowViewModel.ConnectionPin`, leer = kein PIN nötig
- [x] Protokoll-Version im Handshake — erledigt: `session.hello` (Client→Host,
      direkt nach TCP-Connect, vor `session.snapshot`) trägt `ProtocolInfo.Version`
      + optionalen PIN; `TcpPlayerServerService.PerformHandshakeAsync` prüft
      beides und fügt die Verbindung erst danach zu `_clients` hinzu. Bei
      Timeout/falschem PIN/Versionsmismatch: `session.helloRejected` + Trennen,
      der Client versucht danach KEINEN Auto-Reconnect (siehe
      `PlayerTcpClientService`, `case RpcMethods.SessionHelloRejected`)
- [x] Servername im mDNS-/LAN-Announcement konfigurierbar — erledigt,
      Host-Einstellungen → "Servername"; mehrere Hosts im selben LAN sind
      damit in der Client-Serverliste unterscheidbar
- [x] Aufräumen der lokal zwischengespeicherten Medien-Temp-Dateien beim
      Schließen des Clients, auf Nachfrage — erledigt (`ConfirmCleanupWindow`
      + `DeleteAllTempFiles`, inkl. Sweep verwaister Dateien von einem
      früheren Absturz). Der Host legt selbst keine Temp-Dateien an
      (prüft/sendet Bytes direkt aus der Bibliothek), betrifft ihn also nicht.

## Mobile / Android (siehe auch Chat-Diskussion)

- [ ] Machbarkeits-Spike: `Avalonia.Android`-Head-Projekt gegen
      `RpgTimeTracker.Shared` bauen, um zu sehen, wie viel vom PlayerClient
      sich wirklich ohne Änderung wiederverwenden lässt
- [ ] LibVLC-Videowiedergabe auf Android prüfen (`VideoLAN.LibVLC.Android` +
      `LibVLCSharp.Android.AWindowModern`) — anderer Rendering-Pfad als
      Desktop, eigener Test nötig
- [ ] mDNS-Discovery auf Android: braucht einen `WifiManager.MulticastLock`,
      sonst kommen UDP-Multicast-Pakete evtl. gar nicht an
- [ ] Verhalten bei gesperrtem Bildschirm/Hintergrund (Doze/App-Standby)
      klären — eine "zweiter Bildschirm"-App, die im Hintergrund die
      TCP-Verbindung verliert, ist für den Zweck nutzlos
- [ ] QR-Code-Pairing als Alternative zur manuellen IP-Eingabe (Host zeigt
      QR mit `host:port`, Handy scannt) — auf Mobile deutlich angenehmer
      als Tippen, besonders praktisch, wenn PIN/Passcode (siehe oben)
      mit reinkommt

## Qualität / Tooling

- [ ] Unit-Tests für `GameClockService` (Zeitsprünge vor/zurück,
      Geschwindigkeitswechsel) und für die Client-seitige
      Delta-Rekonstruktion der Timer/Wecker — je mehr Client-Plattformen
      dazukommen, desto wichtiger, dass sich das Protokollverhalten nicht
      unbemerkt ändert
- [x] CI-Build (GitHub Actions) für Windows/Linux bei jedem Push — erledigt,
      [`.github/workflows/build.yml`](../.github/workflows/build.yml),
      Matrix-Build auf `windows-latest`/`ubuntu-latest`, reiner Kompilier-
      Check (kein Publish-Artefakt)
- [ ] Siehe [`legal-todo.md`](legal-todo.md) für den vorgeschlagenen
      CI-Check, der neue NuGet-Pakete gegen `THIRD-PARTY-NOTICES.txt`
      abgleicht

## Lokalisierung

- [ ] UI-Texte aus dem AXAML/Code-Behind in Ressourcen auslagern (z.B.
      `.resx` oder Avalonia-`ResourceDictionary` pro Sprache) statt fest
      codiertem Deutsch — betrifft Host und PlayerClient gleichermaßen,
      inklusive der gemeinsamen Views in `RpgTimeTracker.Shared/Views`
- [ ] Englisch als erste zusätzliche Sprache — naheliegend, sobald das Repo
      öffentlich auf GitHub liegt und internationale Nutzer dazukommen
- [ ] Sprachumschaltung (mindestens beim Start, idealerweise auch zur
      Laufzeit) getrennt pro Installation — Host und PlayerClient müssen
      nicht dieselbe Sprache verwenden, da SL-eigene Bezeichnungen (Timer-/
      Wecker-Namen, Sprungmarken) ohnehin frei vom SL getextet und über das
      Protokoll übertragen werden, nicht Teil der UI-Übersetzung sind
- [ ] Kultur-/Formatabhängige Werte beachten: Datums-/Uhrzeitformat,
      Dezimaltrennzeichen beim Geschwindigkeitsfaktor (0,1×–300×)
- [ ] Eingebaute Bezeichnungen wie die Standardtöne "Pling"/"Glocke"/
      "Digital" und die 8 mitgelieferten Design-Namen mit übersetzen, sonst
      wirkt eine sonst übersetzte UI an diesen Stellen inkonsistent

## UI

- [ ] Komplettes UI-Redesign/Aufräumen (separates, größeres Vorhaben —
      hier nur als Platzhalter, damit es nicht aus dem Blick gerät). Einiges
      an Aufräumen ist bereits eingeflossen (Tab-Leiste als Unterstreichungs-
      Optik statt Buttons, gemeinsame Statusleiste, geteilte
      `LibraryPanelView` für Medien-/Sound-Bibliothek, zweispaltige Tabs) -
      das hier ist trotzdem noch offen, weil keines davon ein durchgängiges
      Redesign war, eher lokale Verbesserungen an bestehender Struktur.
