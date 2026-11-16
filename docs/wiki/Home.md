# Sailwind Rig Balance

A shipyard tool for Sailwind. It shows where a sail plan pushes and where the hull pushes
back, so you can judge whether a rig will balance before you pay for it.

If you have played Kerbal Space Program, it is the center of gravity and center of lift
markers, for a boat.

## Pages

- **[Installation](Installation)** - getting it running
- **[Reading the Gizmo](Reading-the-Gizmo)** - what the markers mean and how to use them
- **[Configuration](Configuration)** - every setting
- **[Mod Compatibility](Mod-Compatibility)** - Shipyard Expansion and the rest
- **[Known Issues](Known-Issues)** - what is not finished
- **[Troubleshooting](Troubleshooting)** - when nothing appears
- **[Building from Source](Building-from-Source)**

## In one paragraph

Enter any shipyard and three markers appear on your boat: the center of effort (COE)
in magenta, the center of lateral resistance (CLR) in blue, and the center of mass in yellow. A
colored post rises from the CLR with an arrow running along the hull from its top, and
that is your helm balance. A blue arrow pointing aft is weather helm; a red arrow pointing
forward is lee helm; a green post with no arrow is balanced. Change a sail and watch it move
before you confirm the order.

## Where the numbers come from

None of this is a real-world naval architecture formula. The center of effort is computed
by driving Sailwind's own sail force function with a synthetic close-hauled wind, per
sail, at the trim that gives that sail the most drive. Each sail's center comes from live
geometry, so it follows a resize or a raised spar rather than being read from a table.

The center of lateral resistance is the hull origin, because that is where the game
applies the hull's lateral drag, on every hull.

The center of mass is read from the hull's keel data. It is honest about a surprising fact
about this game: masts and sails add weight without moving the balance point, so a
towering rig does not make a boat tender here. It only makes her heavy and slow.

## Safety

The mod only draws. It does not change your boat, your save, or the game's physics. It
reads the game's sail force function to work out the markers and restores every value it
touches.
