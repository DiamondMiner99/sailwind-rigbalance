# Reading the Gizmo

Enter a shipyard. There is nothing to press.

## The three markers

| marker | name | what it is |
| --- | --- | --- |
| **magenta** | centre of effort | where the whole sail plan pushes |
| **blue** | centre of lateral resistance | where the hull pushes back |
| **yellow** | centre of mass | the boat's own balance point |

They are drawn on top of the hull, so the two that sit inside the boat are still visible,
and they are unlit, so they hold their colour at night and do not change shade with the
time of day.

## The reading

**The fore and aft gap between magenta and blue, and nothing else.**

The link between them is an elbow, not a straight line: it drops from the centre of effort
straight down to the hull's height, then runs across to the blue marker. That is
deliberate. A sail plan's centre of effort sits many metres above the hull, so a direct
line would be almost vertical and the gap that matters would be an invisible lean. As an
elbow, the horizontal run **is** the gap, at true scale, readable from any camera angle.

| what you see | what it means |
| --- | --- |
| magenta **aft** of blue, link **red** | **weather helm** - she rounds up into the wind |
| magenta **forward** of blue, link **blue** | **lee helm** - she bears away when you let go |
| close together, link **green** | **balanced** |

A touch of weather helm is what you want. It means letting go of the wheel turns her into
the wind and stops her, rather than away from it. Lee helm is the one to avoid.

The green band is 0.3 m either side by default, and configurable.

## Judging a rig

Swap a sail, resize one, raise or lower a spar. The markers rebuild a moment later, before
you confirm the order and before any money changes hands. Adding sail area forward pulls
the centre of effort forward, toward lee helm; adding it aft pushes toward weather helm.

The wind angle used is close hauled, 45 degrees off the bow, because that is where balance
bites. Near a run there is barely any side force to balance and every rig would read as
neutral.

## The yellow marker

The centre of mass behaves differently from the other two, and it is worth knowing why.

It does **not** respond to sails or deck parts. In Sailwind a mast, a sail, a forecastle or
a cabin adds to the boat's mass without moving its centre of mass at all. So you can hang
an entirely different sail plan and the yellow ball will not move.

It **does** respond to a keel or ballast option, where a hull has one. In vanilla that is
the junk medium's deep keel, which drops the centre of mass 0.75 m. With Shipyard Expansion
installed the kakam, the dhow and the cog gain a deep keel too, and the sanbuq a cladding
option.

It also sits below the hull on most boats. That is not a bug; a low centre of mass is how
the game gives a boat a strong righting moment.

Its one use with the other markers is heel: the game takes torque about the centre of mass
and applies the sail force up at the sail, so the height of magenta above yellow is your
heeling lever.

## The buttons

A strip of three round buttons, coloured to match the markers, sits in the top right.
Click one to hide or show that marker. Hover for a name. Drag the strip anywhere and it
stays there, across sessions.

## The red marker

If the magenta marker turns red and a line of text appears, one of the sails on the boat
has a centre of effort that does not follow its own size, so the combined figure is
unreliable. In practice this means one of Shipyard Expansion's three added lug sails. See
[Mod Compatibility](Mod-Compatibility).
