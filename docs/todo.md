# TODO

Open ideas and tasks for RpgTimeTracker, beyond acute bugs. Not a
replacement for GitHub Issues once the repo lives there — more of a
long-term memory aid for things that aren't yet ripe enough for an
Issue. License/attribution tasks live separately in
[`legal-todo.md`](legal-todo.md).

## Features

- [ ] Sound/popup notification when a timer expires or an alarm fires
      (even when the main window isn't currently focused)
- [ ] Custom fantasy calendar (different month lengths, weekdays, holidays)
      instead of the standard `DateTime` — bigger rework, affects
      `GameClockService` on both sides as well as the jump-marker feature
- [ ] Auto-save when closing the app / automatically reload the
      most recently used file on startup
- [ ] Session/campaign templates: pre-built bundles of
      timers/alarms/theme (e.g. "Travel mode", "Dungeon crawl") to
      apply with one click instead of setting them up from scratch every time
- [x] Extended undo history beyond the most recent time jump —
      done: `MainWindowViewModel` now has a real undo *and*
      redo stack (`_jumpUndoStack`/`_jumpRedoStack`), both clickable any
      number of times in a row; manual time entry
      (`ApplyManualDateTime`) now also goes through the same delta mechanism
      and is therefore undoable as well. A new jump clears the
      redo stack (classic editor behavior); loading a save game
      clears both stacks, since an old jump history no longer applies to
      the newly loaded timer/alarm state.

## Network & robustness

- [x] Video chunking with progressive playback already during
      transfer (not only after full receipt) — done,
      `StartVideo` plays the still-growing temp file immediately
- [x] Auto-reconnect with backoff in the PlayerClient if the TCP connection
      drops unexpectedly — done (`PlayerTcpClientService.ReconnectLoopAsync`,
      2s-20s backoff, only on unexpected disconnects, not on manual
      disconnects); reconnected clients automatically get a full
      `session.snapshot` via the existing catch-up path
- [x] Optional PIN/passcode when establishing a connection — done, together
      with the protocol version in a shared `session.hello`/
      `session.helloRejected` handshake (see below); Host setting
      `MainWindowViewModel.ConnectionPin`, empty = no PIN required
- [x] Protocol version in the handshake — done: `session.hello` (client to
      Host, right after the TCP connect, before `session.snapshot`) carries
      `ProtocolInfo.Version` plus an optional PIN; `TcpPlayerServerService.PerformHandshakeAsync`
      checks both and only then adds the connection to `_clients`. On
      timeout/wrong PIN/version mismatch: `session.helloRejected` plus disconnect,
      after which the client does NOT attempt auto-reconnect (see
      `PlayerTcpClientService`, `case RpcMethods.SessionHelloRejected`)
- [x] Configurable server name in the mDNS/LAN announcement — done,
      Host settings -> "Server name"; multiple hosts on the same LAN are
      thus distinguishable in the client's server list
- [x] Clean up locally cached media temp files when the client closes,
      on request — done (`ConfirmCleanupWindow`
      + `DeleteAllTempFiles`, including a sweep of orphaned files from a
      previous crash). The Host itself never creates temp files
      (it checks/sends bytes directly from the library), so this doesn't affect it.

## Mobile / Android (see also chat discussion)

- [ ] Feasibility spike: build an `Avalonia.Android` head project against
      `RpgTimeTracker.Shared` to see how much of the PlayerClient
      can really be reused without changes
- [ ] Check LibVLC video playback on Android (`VideoLAN.LibVLC.Android` +
      `LibVLCSharp.Android.AWindowModern`) — different rendering path than
      desktop, needs its own testing
- [ ] mDNS discovery on Android: needs a `WifiManager.MulticastLock`,
      otherwise UDP multicast packets may not arrive at all
- [ ] Clarify behavior with a locked screen/in the background (Doze/App
      Standby) — a "second screen" app that loses its
      TCP connection in the background is useless for its purpose
- [ ] QR code pairing as an alternative to manual IP entry (Host shows a
      QR code with `host:port`, phone scans it) — much more pleasant
      on mobile than typing, especially handy once a PIN/passcode (see
      above) is added to the mix

## Quality / tooling

- [ ] Unit tests for `GameClockService` (time jumps forward/backward,
      speed changes) and for the client-side
      delta reconstruction of timers/alarms — the more client platforms
      get added, the more important it is that protocol behavior doesn't
      change unnoticed
- [x] CI build (GitHub Actions) for Windows/Linux on every push — done,
      [`.github/workflows/build.yml`](../.github/workflows/build.yml),
      matrix build on `windows-latest`/`ubuntu-latest`, a pure compile
      check (no publish artifact)
- [ ] See [`legal-todo.md`](legal-todo.md) for the proposed
      CI check that compares new NuGet packages against `THIRD-PARTY-NOTICES.txt`

## Localization

- [x] Move UI text out of AXAML/code-behind into resources (an Avalonia
      `ResourceDictionary` built from flat JSON files per language, see
      `RpgTimeTracker.Shared/Services/Localization/LocalizationService.cs` and
      `RpgTimeTracker.Shared/Localization/{en,de}.json`) instead of
      hard-coded German — covers Host and PlayerClient equally,
      including the shared views in `RpgTimeTracker.Shared/Views`
- [x] English as the first additional language — done; English is now the
      default/fallback language, with German as the selectable alternative
- [x] Language switching (at least on startup, also at runtime) per
      installation — Host and PlayerClient each persist their own `Language`
      setting independently and switch immediately via `{DynamicResource}`,
      since the GM's own names (timer/alarm names, jump markers) are freely
      authored by the GM anyway and sent over the protocol, and aren't part
      of the UI translation
- [ ] Account for culture/format-dependent values: date/time format,
      decimal separator for the speed factor (0.1x-300x)
- [ ] Translate built-in labels too, such as the default tones "Pling"/"Glocke"/
      "Digital" and the 8 bundled theme names, otherwise an
      otherwise-translated UI looks inconsistent at these spots

## UI

- [ ] Complete UI redesign/cleanup (a separate, larger undertaking —
      listed here only as a placeholder so it doesn't get lost). Some
      cleanup has already landed (tab bar as an underline
      look instead of buttons, shared status bar, shared
      `LibraryPanelView` for the media/sound library, two-column tabs) -
      this item is still open regardless, because none of that was a full
      redesign, more local improvements to the existing structure.
