Networked Player Display
=========================

The GM starts a TCP server on a freely selectable port/name (included in
the mDNS/LAN announcement - useful with several hosts on one network);
clients find it via mDNS/LAN discovery (merged into one entry per
server) or manual IP:port.

Connected clients are listed with connection time and can be
disconnected individually. On an unexpected drop, the client retries
with backoff and, on success, gets the same full state as a fresh
connection - a manual disconnect stays disconnected.

An optional PIN gates connections (LAN access control, not real auth);
client/host also exchange protocol version on connect, rejecting a
mismatch clearly instead of misbehaving.

**Per-client routing**: for each connected client (and the Host's own
preview), the GM can independently route Music/Sound/Image/Video/Map -
e.g. a second-monitor client for maps/images, a tablet for Music only.
The preference persists across reconnects; re-enabling a kind catches
the client up immediately, disabling it clears the display.
