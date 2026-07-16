UI & Themes
===========

**Status bar** (bottom, visible on every tab): server status
(running/stopped, address:port, connected players), the current
image/video with a close button, a sound counter whose flyout lists
playing sounds with live volume control and per-item/all-together stop
(across all clients), a bell showing recent action messages, and two
fullscreen toggles (GM preview, remote player display). Placed outside
the tab content so nothing "jumps" as sounds or media change in the
background.

**Advance warning** (GM-only): an optional notice banner a configurable
number of game-minutes before a timer/alarm/interval triggers - never
sent to players.

**Session log**: an optionally recordable list of triggered events (time
jumps, triggers, sent media) with a game-time stamp, exportable as text.

**Themes**: 8 bundled themes (Shadowrun, Medieval, Warhammer 40k, Alien,
Steampunk, Post-Apocalypse, Eldritch Horror, Gothic Vampire) plus any
custom ones as ``theme.json`` files. A selected theme is transmitted to
clients by ID; a client renders it correctly if it has the same
``theme.json`` locally.

**Ambient automation** (optional): for a theme with multiple named
backgrounds, switches day/night background by game time.

**About dialog** (host and client): title/description, version number,
the app's project license, and third-party license notices, each in a
collapsed section.

**Media windows** (host preview and client): alongside the image/video,
a small clock display and their own minimize button next to close, since
fullscreen/borderless mode has no OS title bar.
