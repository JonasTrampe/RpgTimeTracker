Game Clock
==========

- **Date & time**: freely settable, start/pause, keeps running
  automatically afterward.
- **Speed**: time factor from 0.1x to 300x (presets + slider) - e.g.
  "1 real second = 1 hour of game time" during a rest.
- **Configurable bookmarks**: e.g. "Dawn" (06:00), "Noon" (12:00),
  "Evening" (18:00), "Midnight" - freely nameable, editable, deletable.
  Jump to the next/previous occurrence of that time of day with one
  click.
- **Save & load**: the complete state (game time, speed, timers, alarms,
  bookmarks) exports as a ``.rtt-save`` file, or imports again. Older
  plain-JSON saves still load and are upgraded automatically on next
  save.

All timers and alarms are updated with the corresponding game-time delta
on every tick of the clock *and* on every manual time jump - speeding up,
slowing down, or jumping the clock automatically changes their behavior
accordingly. The player client reconstructs the same behavior locally
from the deltas sent by the host, without needing a network message on
every tick.
