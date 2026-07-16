FAQ
===

Do I need to install anything for video/sound to work?
-------------------------------------------------------

Images always work out of the box. Video/audio playback uses VLC:
bundled automatically on Windows, but on Linux you need it installed
system-wide (``sudo apt install vlc`` or ``sudo dnf install vlc``).
Without it, images still work, but sending video/audio shows an error
instead of playing. See :doc:`media/index`.

Why can't a player connect?
----------------------------

Check the port/PIN in the host's network settings, and that both
machines are on the same LAN (mDNS/broadcast discovery doesn't cross
networks, e.g. across VPNs or guest Wi-Fi isolation) - a manual IP:port
connection always works if discovery doesn't find the host. See
:doc:`networking/index`.

Why did a client get disconnected with "protocol version mismatch"?
---------------------------------------------------------------------

Host and client exchange their protocol version on connect. A mismatch
usually means one side is running an older build - update both to the
same release. See :doc:`networking/index`.

Do I need a "Session" to use the app?
---------------------------------------

No - Sessions are opt-in. Without one open, everything behaves as a
single always-present Shared Library. See :doc:`sessions/index`.

*This page is a starting point - more questions get added as they come
up.*
