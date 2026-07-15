# Legal / Licensing TODO

Tracks outstanding work related to copyright and third-party license
compliance. Check items off as they're done; add new items whenever a
licensing question comes up rather than trying to remember it.

## Open items

- [x] **About dialog**: show the full contents of `THIRD-PARTY-NOTICES.txt`
  and the project's own `LICENSE` — done (Host + Client both have an About
  screen; `AboutViewModel` loads both files at runtime, each rendered via
  `ClassIsland.Markdown.Avalonia` inside its own collapsed-by-default
  `Expander` so neither wall of legal text is in the way of the app
  description by default).
- [x] Link `THIRD-PARTY-NOTICES.txt` from the top-level `README.md` — done.
- [x] Add a license badge to the top-level `README.md` — done, alongside a
  CI-status badge (both `shields.io`/GitHub Actions badges pointing at
  `JonasTrampe/RpgTimeTracker`).

## Ongoing: whenever a new NuGet package is added

- [ ] Check its license (NuGet page or repo `LICENSE` file).
- [ ] If it's MIT/Apache/BSD-style (permissive): add an entry to
  `THIRD-PARTY-NOTICES.txt` under "Overview of included components", reuse
  the matching license text already in the appendix, or add a new one if
  it's a license not yet listed there.
- [ ] If it's LGPL/MPL or another weak-copyleft license: same as above, plus
  double-check whether the app links it dynamically (fine) or would need to
  statically embed it (needs extra care re: relinking/source availability).
- [ ] If it's GPL/AGPL (strong copyleft): stop and think before adding it —
  this would likely force the whole app under GPL too. Look for an
  MIT/Apache/LGPL alternative first.

## How to not forget this stuff (see also the note in chat)

This file *is* the answer to "how do I remember this": it's committed to
the repo, so it survives regardless of who's working on the project or
which AI session helped last. Suggested habit: whenever you (or an
assistant) add a `PackageReference` to any `.csproj`, add a line here in
the same commit rather than trusting memory.

- [x] CI check (`third-party-notices.yml`, running
  `scripts/check-third-party-notices.ps1`) that fails if any shipped
  project's `PackageReference` isn't mentioned anywhere in
  `THIRD-PARTY-NOTICES.txt` — catches a forgotten attribution entry instead
  of relying on memory. `RpgTimeTracker.Tests`' dev/test-only packages
  (xunit, coverlet, Avalonia.Headless, ...) are intentionally excluded,
  since they never ship in a built app.
