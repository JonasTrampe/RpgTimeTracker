Fog of War
==========

A library of maps, each made of one or more floors (image + grid cell
size). A floor's fog (brush reveal/hide, zoom, resizable cell size) is
prepared offline in a "Prepare" window - never visible to players -
separate from the "Show" window, which paints and broadcasts the *live*
fog and can reset it to the prepared state. Both windows have a live,
player-accurate preview and can be open side by side.

A map can override fog color/opacity/blur, falling back to the global
default (Settings tab). Floors are reorderable, thumbnailed layers whose
order survives export/import. Only one map shows to players at a time,
and not while its Prepare window is open. Exportable individually
(``.rtt-map``) or as part of a full session bundle.
