Two AvaloniaUI desktop apps (Windows/Linux/macOS) for a shared game clock
(timers, alarms, recurring intervals) at the pen & paper table: a GM-side
control app and a read-only display app for players on a second screen,
including image/video sending and a separate sound library for
background sounds/effects.

- **RpgTimeTracker** (GM/Host) - the control app. Owns the authoritative
  game clock as well as all timers/alarms/intervals, manages the media
  library, and runs the TCP server that any number of player clients can
  connect to simultaneously.
- **RpgTimeTracker.PlayerClient** (player client) - a pure display app.
  Connects to the host via LAN/mDNS discovery or manual IP entry, derives
  its own game clock locally from that, and shows the timeline as well as
  images/videos sent by the host (sounds play without their own display).

Both apps communicate over a lightweight, length-prefixed JSON-RPC
protocol on TCP.
