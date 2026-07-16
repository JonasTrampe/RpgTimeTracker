Characters
==========

Its own "Characters" tab: NPCs/PCs with a portrait, a map token (image,
Bootstrap icon, or initials from the name - whichever is set), any
number of named, ordered GM-only reference note blocks (e.g.
"Motivation"/"Secrets", rendered as Markdown), and any number of named
variants/moods (e.g. "Neutral"/"Angry") that can each override the
portrait, token, player info, and connected sounds - an unset variant
falls back to the Default variant.

Portrait and sounds reference the Media/Sound Libraries by ID rather
than owning copies; deleting a referenced item warns if a character
still uses it. Included in the Sessions' Shared-vs-session-local split
and export/import.
