Sessions
========

Optional, folder-scoped campaigns. "New/Open/Close Session" in the
status bar creates or opens a folder holding one campaign's own
Media/Sound/Map/Character libraries, alongside the always-present Shared
Library - purely opt-in, no session behaves as before.

New items ask Shared Library or session-only, changeable later via
"Move to Shared Library"/"Move to this Session". Closing a session hides
its items from library lists (files stay on disk).

**Full session export/import**: Settings' "Export/Import session..."
pair. With a session open, it bundles that session's state and local
Media/Sound/Map/Character entries plus copies of any referenced
Shared-sourced assets, so it's self-contained elsewhere; import always
creates a fresh session with new IDs, never merging into the target's
Shared Library. With no session open, it exports the game state plus
the entire Shared Library as one ``.rtt-session`` file - a full backup
or one-step machine move. (Music/playlists use their own Export/Import
in the Music tab.)
