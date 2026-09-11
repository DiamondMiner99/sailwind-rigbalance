# Known Issues

Honest list. This is an early alpha and the gaps are worth stating plainly.

## Tested on a small number of hulls

The gizmo has been exercised on a brig and a schooner, in vanilla and with Shipyard
Expansion installed. It has not been driven across every hull in the game. The model is
hull-agnostic by construction, but that is an argument, not a test.

## Shipyard Expansion's three lug sails (fixed in SE v0.11.0)

Sails 156, 157 and 158, added by Shipyard Expansion, kept their center of effort outside
the part that gets scaled, so it did not move when the sail was resized. The gizmo detects
this, turns the magenta marker red and says the reading is unreliable.

Shipyard Expansion v0.11.0 fixed it. On SE v0.10.1 and earlier the red marker still appears
on those three sails, and updating Shipyard Expansion clears it.

**It was not fixed here, and would not have been.** It was a property of another author's
mod, and correcting another mod's data from this one is not something this mod does. The
detection stays in place, because it measures each sail's own geometry rather than an SE
version number, so it reports the same condition wherever it turns up next.

## The center of mass ignores cargo

The yellow marker reads the hull's keel data, which is the design center of mass. It does
not include the shifting weight of cargo in the hold or a player standing on deck, both of
which the game does account for while sailing.

This is deliberate: the marker is there to judge a hull and its keel options, not today's
loading. It also has to be this way, because the game only maintains the loaded value for
the boat you sailed in on, not for a boat you are looking at before buying.

## The overlay camera

Drawing the markers on top of the hull uses a second camera on an otherwise unused Unity
layer. It is the least conventional thing the mod does. If another mod ever conflicts with
it, set `DrawThroughHull = false`, which leaves a working gizmo with the inner markers
occluded rather than a broken screen.

If every layer on your install is already named by other mods, the mod cannot claim one and
falls back automatically. It logs when this happens.
