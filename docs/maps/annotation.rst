Map Annotation
==============

A freehand stroke a player can draw on their own map view to point
something out in more detail than a single ping - hold Shift and
left-drag across the map. The stroke draws live as you drag, then fades
out on its own after a couple of seconds on both your own screen and the
GM's (main map canvas and its live "Vorschau" preview).

Same one-way visibility as a player's :doc:`ping <ping>`: only the GM
sees it, it's never rebroadcast to other players, and it isn't persisted
or replayed to a client that connects after it has already faded. There
is currently no equivalent drawing tool for the GM - the GM's Ping tool
covers the GM-to-players direction instead.
