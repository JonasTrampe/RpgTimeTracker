Music
=====

A "Music" tab with its own library, playlists, and networked playback:

- **Library**: add tracks ahead of time, like Sound/Media.
- **Playlists**: named, ordered track lists with shuffle/loop.
- **Now Playing transport**: play/stop/next/previous, sent to every
  connected client independently of Sound/Image/Video routing.
- **Per-client routing**: each connected client (and the Host's own local
  playback) can have Music turned on or off independently - e.g. a
  second-monitor client dedicated to maps/images while a tablet client
  only gets Music.

Music and Sound share the same underlying "Layer" concept on the wire
(distinct from Image/Video), so a track and a sound effect never
interrupt each other.
