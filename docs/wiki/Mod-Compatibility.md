# Mod Compatibility

The mod only reads. It applies seven Harmony postfixes to shipyard methods, each of which
does nothing but set a flag, so it cannot break another mod's behavior.

## Shipyard Expansion

Supported, and it was a target from the start rather than an afterthought.

**Resized sails are handled correctly.** Sailwind's own center of effort sits inside the
part Shipyard Expansion scales, so it follows a width change, a height change, or both,
and the sail area that the force model reads is refreshed by SE on every resize. There is
no lookup table of sail sizes anywhere in this mod; every center of effort is measured from
the sail as it stands.

**Rotated spars are handled correctly.** Raising or lowering a spar changes where the
center of effort sits without changing the sail's area, and the marker follows, because it
reads the sail's live geometry.

**Sails added to stays are included.** The mod walks the boat's masts and their sails,
which is the same traversal Shipyard Expansion itself uses to reach every sail on a boat.

**Keel options are included.** Shipyard Expansion adds a deep keel to the kakam, the dhow
and the cog, and a cladding option to the sanbuq. Each of these moves the center of mass,
and the yellow marker follows.

### The one caveat, on Shipyard Expansion v0.10.1 and earlier

Shipyard Expansion adds three lug sails of its own. On those three, up to SE v0.10.1, the
center of effort is attached outside the part that gets scaled, so it stays put when the
sail is resized. The gizmo cannot give an honest combined figure when one of them is on the
boat.

When that happens the magenta marker turns **red** and a line of text says the marker is
unreliable.

Shipyard Expansion v0.11.0 fixed it, so on current SE those three sails read like any
other. The gizmo has no setting for this and does no version check: it measures where each
sail's center of effort is parented, so it stops reporting the problem when the problem is
gone.

That is all it ever did. It does not correct the sail, patch Shipyard Expansion, or work
around it without saying so.

## Other mods

The mod declares soft dependencies on Shipyard Expansion, Sail Collision Fix, NAND Tweaks,
Towable Boats and HMS Leopard. That is purely a load-order request so their patches are
applied before it reads anything. What is actually installed is detected at runtime through
the BepInEx chainloader, never assumed from the dependency list, and logged at startup.

**Drydock** adds keel weight options through the same mechanism Sailwind and Shipyard
Expansion use, so those are picked up too.

**Sail Info** and **Mariner's HUD** show sail numbers while sailing. They do not overlap
with this mod, which only runs in the shipyard.

## Version drift

Everything the mod reaches into is looked up by name at load, and a lookup that fails is
logged and degraded rather than thrown. If a game or Shipyard Expansion update moves
something, expect a warning in the log and a missing feature, not a crash.

The startup log records the game version, the Unity version, the BepInEx version, the path
the plugin loaded from, and which of the seven hooks applied. That is the first thing to
check after any update.
