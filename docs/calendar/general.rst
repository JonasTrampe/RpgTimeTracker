Ingame Calendar
===============

Its own host tab, "Calendar": freely definable events on an ingame
date/time with title, description, icon, color, recurrence
(daily/weekly/monthly/yearly, optional end date), and an optional
image/video/sound trigger (same event-media path as a timer/alarm - see
:doc:`/media/trigger-media`).

A "Timeline ↔ Calendar" toggle appears in the shared header once a
player-visible event exists; otherwise it stays a plain timeline.
Persisted in the save state and exportable/importable separately as
JSON; changes sync live to connected players, and new connections get
the full calendar.
