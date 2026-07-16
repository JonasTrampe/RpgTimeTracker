Sending Images and Video
========================

Selecting an image or video (up to ~1900 MB) opens a dedicated media
window on all connected clients (images inline, video via embedded VLC),
and locally for the GM if no player is connected. The GM closes it for
everyone via "Remove"; fullscreen vs. normal window is set independently
per side, and the GM's "player fullscreen" toggle switches all clients
and the GM's own window at once - each can shrink back locally via
Escape (or a button for the GM) without affecting the other side.

The client reports real video start/end (actual VLC duration, not an
estimate) for things like an automatic clock pause during playback.
