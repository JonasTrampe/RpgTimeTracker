# RpgTimeTracker

Two AvaloniaUI desktop apps (Windows/Linux/macOS) that share a game clock
(timers, alarms, recurring intervals, a custom-calendar engine) at the pen &
paper table: a GM-side control app (Host) and a read-only display app for
players on a second screen, connected over a lightweight JSON-RPC-over-TCP
protocol.

## Development Setup

```bash
# Restore + build everything (Host, PlayerClient, Shared, Tests)
dotnet build RpgTimeTracker.sln -o .build-scratch

# Run the Host (GM control app)
dotnet run --project RpgTimeTracker

# Run the PlayerClient (player display app)
dotnet run --project RpgTimeTracker.PlayerClient

# Tests
dotnet test RpgTimeTracker.Tests/RpgTimeTracker.Tests.csproj -o .build-scratch
```

Always build/test into `.build-scratch` (or another out-of-tree output
folder), never the default `bin`/`obj` — those are also used by an
interactively-running Rider/Visual Studio session, and a competing `dotnet
build` there can corrupt or lock its build output.

## Tech Layers

- **Framework**: Avalonia UI 12 (cross-platform desktop XAML), .NET 10
- **Language**: C# 13 (`LangVersion: latest`), nullable reference types enabled
- **UI pattern**: MVVM via CommunityToolkit.Mvvm (`[ObservableProperty]`,
  `[RelayCommand]`), compiled bindings by default
- **Networking**: hand-rolled length-prefixed JSON-RPC over TCP (see
  `docs/internal/protocol.md`) — no ASP.NET/SignalR/gRPC
- **Media playback**: LibVLCSharp (images/video/audio)
- **Logging**: Serilog, rolling file sink under `%AppData%/RpgTimeTracker/logs`
- **Testing**: xunit 2 + `Avalonia.Headless` (not `Avalonia.Headless.XUnit`,
  which pulls in an incompatible xunit v3 — see `HeadlessDispatch.cs` for the
  hand-rolled headless dispatcher used instead)

## Project Structure

```
RpgTimeTracker.Shared/       # Code/assets used by both apps
├── Models/                  # GameInstant, CalendarDefinition, TimerItem,
│                             # AlarmItem, IntervalEventItem, Rpc/ (wire DTOs)
├── Services/                # GameClockService, CalendarService, Localization
├── ViewModels/               # Shared view models (e.g. CalendarEntryViewModel)
├── PredefinedCalendars/     # Bundled calendar JSON (Gregorian, DSA, Harptos, ...)
└── Localization/            # en.json / de.json resource dictionaries

RpgTimeTracker/               # Host (GM) app
├── ViewModels/               # MainWindowViewModel.*.cs (partial-class split
│                             # by feature area — Clock, SceneLibrary, SendMedia, ...)
├── Models/Persistence/      # Save-format DTOs (AppStateDto, *LibraryDtos)
├── Network/                 # TcpPlayerServerService
├── Services/                 # SoundService, ThemeSettingsService, SessionService
└── Views/                   # MainWindow.axaml + Controls/

RpgTimeTracker.PlayerClient/  # Player display app
├── ViewModels/               # ClientMainWindowViewModel
├── Network/                 # PlayerTcpClientService
└── Views/

RpgTimeTracker.Tests/         # xunit test project (covers Shared + Host logic)

docs/                         # Sphinx-built user docs (published to GitHub Pages)
└── internal/
    ├── protocol.md            # Wire protocol reference
    └── design-decisions.md    # Dated "why is this shaped this way" notes
```

## Code Standards

### General Rules
- Nullable reference types are on — don't silence warnings with `!` unless
  the invariant is actually guaranteed; prefer real null-checks
- MVVM: put behavior in ViewModels, not code-behind; `Views/*.axaml.cs`
  should stay thin (event wiring, window-specific chrome only)
- Split a growing `MainWindowViewModel` into another `MainWindowViewModel.<Area>.cs`
  partial file rather than letting one file balloon — see the existing split
  by feature area (Clock, SceneLibrary, SendMedia, MediaLibraries, ...)
- Reuse the existing Shared-vs-SessionLocal library pattern
  (`LibraryScope`, `SaveLibrarySettings<TItem,TDto>`, `LibraryUsageRegistry`)
  for any new library-like collection instead of inventing a new one
- Default to writing no comments; when one is warranted, explain *why*
  (a non-obvious constraint, invariant, or past bug), never *what* the code
  does — see `docs/internal/design-decisions.md` for the level of "why" expected

### Naming Conventions
- Classes/ViewModels/Views: PascalCase (`SceneLibraryItemViewModel`)
- Private fields: `_camelCase`; `[ObservableProperty] private T _fooBar`
  generates a public `FooBar` property — never hand-write both
- DTOs used for persistence/wire format live in `Models/Persistence` or
  `Shared/Models/Rpc` and end in `Dto`

### File Organization
- One class per file, filename matches the class name
- Localization keys follow `Area.Sub.Name` (e.g. `MainWindow.Scenes.AddButton`)
  and must be added to **both** `RpgTimeTracker.Shared/Localization/en.json`
  and `de.json` together — never add a key to only one language

## Important Patterns

### Adding a new library-like feature (e.g. a new kind of GM asset)
Follow the existing Media/Sound/Music/Map/NPC/Scene precedent:
1. A DTO in `Models/Persistence/*Dtos.cs`
2. A ViewModel with `Id`, `Scope` (`LibraryScope`), and `To*Dto`/`From*Dto`
   conversion methods on `MainWindowViewModel`
3. Register cross-references into `LibraryUsageRegistry` so deleting a
   referenced item goes through the existing 3-way confirm-delete flow
4. Wire Shared-vs-SessionLocal storage via `SaveLibrarySettings`
5. Add it to full-session/per-session `.rtt-session` export/import
   (`MainWindowViewModel.SessionExport.cs`) with fresh-Guid remapping on import

### Changing the wire protocol (`RpcMethods.cs` / `RpcParams.cs`)
Any change to the JSON-RPC message shapes must bump
`RpgTimeTracker.Shared/Models/Rpc/ProtocolInfo.Version` in the **same**
commit — a CI check (`protocol-version.yml`) diffs those two files against
the PR base and fails if the version wasn't bumped. A host rejects a
mismatched-version client with a clear error instead of silently
misbehaving.

### Changing the save format (`AppStateDto` and anything it references)
`RpgTimeTracker.Tests/SaveFormatVersionGuardTests.cs` reflects over the full
`AppStateDto` object graph and fails on any shape change unless
`AppStateDto.Version` and the test's own `ExpectedVersion`/`ExpectedShape`
are updated together — this forces a deliberate decision about whether a
field change is safe-to-ignore-when-missing (purely additive, update the
test's shape string) or a genuine breaking change (bump the version; old
saves must then fail to load cleanly, never deserialize with silently wrong
data).

### Game time
Game time is `GameInstant` (calendar-agnostic elapsed seconds), not
`DateTime` — display/parsing always goes through `CalendarService.Active`
(the campaign's configurable `CalendarDefinition`), never
`DateTime`/`TimeSpan` formatting directly.

## Testing Guidelines

- New Shared-project logic (calendar math, clock ticking, save-format
  shape) gets a plain xunit test — no Avalonia dispatcher needed
- Anything exercising Avalonia's `DispatcherTimer` or UI dispatch needs the
  headless test harness (`HeadlessDispatch.RunAsync`, see
  `GameClockServiceTests.cs`) — a plain xunit test has no running dispatcher
  to pump ticks
- `MapFogIntegrationTests` are real TCP integration tests and are prone to
  occasional timing flakiness under load; a solitary failure there that
  doesn't reproduce when rerun in isolation is not a regression signal by
  itself — rerun before treating it as one
- Don't adapt a failing test to make it pass — investigate the root cause
  first; write new tests for new code, but a red test on existing code is a
  signal to fix the code (or the test's wrong assumption), not to loosen it

## Common Pitfalls to Avoid

- DON'T: build/test into the default `bin`/`obj` folders — always use an
  out-of-tree output dir (`.build-scratch`) to avoid clobbering a running IDE session
- DON'T: bump `ProtocolInfo.Version` or the save-format `Version` for a
  change that isn't actually breaking (see the patterns above) — but also
  don't skip the bump when it genuinely is one
- DON'T: add a localization key to only one of `en.json`/`de.json`
- DON'T: use `git rebase -i`, `git add -i`, or other interactive git flows
  in this repo — history cleanup here uses cherry-pick + `reset --soft` instead
- DON'T: force-push over already-published history without calling it out
  explicitly first
- DO: check `docs/internal/design-decisions.md` before "fixing" something that looks
  wrong — it likely exists because an earlier, more obvious approach broke
  something specific
- DO: keep `CHANGELOG.md` updated for user-visible changes (Added/Fixed
  sections, Keep a Changelog format)

## Deployment

- No CI/CD deployment pipeline — this ships as a GitHub release, not a
  hosted service
- GitHub Actions run `build.yml` and `tests.yml` on PRs (see the badges in
  `README.md`) plus `protocol-version.yml`, which enforces the protocol
  version bump rule above
- Secrets/environment variables aren't applicable — both apps are local
  desktop executables with no external service dependencies

## Additional Resources

- Wire protocol reference: `docs/internal/protocol.md`
- Design rationale ("why is this shaped this way"): `docs/internal/design-decisions.md`
- User-facing feature history: `CHANGELOG.md`
- Full feature list and screenshots: `README.md`
