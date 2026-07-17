Map Ping
========

A generic, un-attributed ripple that briefly marks a point on the map -
useful for pointing something out without needing to describe where it is.
Two independent, one-way channels:

- A player double-clicking their own map view sends a ping visible only
  to the GM (on the main map canvas and its live "Vorschau" preview).
- The GM sends a ping visible to every connected player by first
  selecting the **Ping** tool (next to the fog **Aufdecken/Verstecken**
  toggle), then double-clicking the map. A single click while the Ping
  tool is selected does nothing - switch back to the paint tool to keep
  editing fog.

The ripple fades out on its own after a couple of seconds; it isn't
persisted and isn't replayed to a client that connects after it has
already faded.
