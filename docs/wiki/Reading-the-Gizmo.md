# Reading the Gizmo

Enter a shipyard. There is nothing to press.

## The three markers

| marker | name | what it is |
| --- | --- | --- |
| **magenta** | center of effort | where the sails pull, with each sail weighted by its force at best trim |
| **blue** | center of lateral resistance | where the hull pushes back |
| **yellow** | center of mass | the boat's own balance point |

They are drawn on top of the hull, so the two that sit inside the boat are still visible,
and they are unlit, so they hold their color at night and do not change shade with the
time of day.

The magenta marker is where the sails would be pulling at the wind arrow's bearing, trimmed
for best drive. The sails stay at rest in the shipyard, so with the wind aft the marker can
sit out to one side of the cloth you see, where that sail would be once swung out.

## The reading

**The helm bar**: a colored post rising from the blue marker to the top of the hull, with an arrow
running along the centerline from there.

| what you see | what it means |
| --- | --- |
| arrow pointing **aft**, **blue** | **weather helm** - she rounds up into the wind |
| arrow pointing **forward**, **red** | **lee helm** - she bears away when you let go |
| **green** post, no arrow | **balanced** |
| **gray** post, no arrow | **no verdict** - she is running, or no sails are set or drawing |

A touch of weather helm is what you want. It means letting go of the wheel turns her into
the wind and stops her, rather than away from it. Lee helm is the one to avoid.

The arrow's length is a lever in meters, drawn at true length: the boat's whole turning moment
divided by its whole side force. The color is a gradient, not three flat states. Weather helm runs from green through light
blue to dark blue, and lee helm from green through orange to red. Light blue or orange is
reached at three times the balanced band, and dark blue or red at eight. The balanced band is 2 percent
of the hull's length by default, which is about 0.6 m on a brig, and configurable as
`BalancedThresholdFraction`.

**The bar and the magenta marker can disagree, and the bar is the one to trust.** Three things
turn the boat:

- Side force from each sail, acting at that sail's position along the hull. Sails on slanted
  stays make far more side force than they appear to, because the game measures the angle of a
  force that leans with the stay. A jib still pulls hard sideways on a dead run, and it does so
  from the far end of the boat, so headsails are a strong push toward lee helm.
- Thrust from a sail swung out to one side. On a reach the sail's pull sits well to leeward of
  the centerline, which turns the bow toward the wind. Junks and other sails set to one side
  of their mast do this strongly.
- The wind on the hull itself, which pushes at a point 7 to 12 m aft of the blue marker
  depending on the hull. This always turns the bow toward the wind. It is fixed per hull, and
  a cabin or other fitted part does not change it. On a small rig it can make up around half
  of the helm.

The magenta marker only knows about the sails' positions, so a one-sail junk on a broad reach
can show magenta forward of blue with a bar running aft. She does round up, as the bar says.

Gray means the question does not apply: with the wind well aft there is almost no side force
to divide by, so the gizmo gives no number.

A rig can also be lopsided beyond what the scale measures: sails drawing hard with a lever a
quarter of the hull's length or more, which one oversized sail on a small hull manages easily.
That is not gray, because it is not "no answer". The arrow stops at that length, the bar and the
arc go straight to dark blue or red, and the log calls it off the scale, with the
direction and a rough figure.

## The balance ring

A rose is drawn on the water around the boat, colored the same way as the helm bar. **The top
of the ring is your bow**, and each arc answers one question: if the wind came from that
bearing, what would the helm do? The gray arrow marks the bearing the markers are
currently reporting, and you can drag it around the ring to look at any other. It points
the way the wind blows, in at the boat; set `WindArrowStyle` to `WeatherVane` to have it
point into the wind instead.

Read the ring as a whole. Green and blue all the way round is a boat that behaves on every
point of sail. **Red anywhere is the finding**: she develops lee helm on some heading you
will eventually sail, and a single close-hauled reading would never have shown you it.
Gray arcs near the bow and stern are normal; no boat sails straight into the wind, and
dead downwind there is nothing to balance.

If red and green are hard to tell apart, two settings help. `ColorblindPalette` paints the
helm in blue, white and orange: blue weather helm, white balanced, orange to dark orange lee
helm, on the ring, the helm bar, the reef track, the legend and the ring button alike.
`ShowHelmTicks` puts a short tick at every bearing of the ring that reads without color: out
of the ring for weather helm, into it for lee helm, longer the stronger the helm, and none
where she is balanced or there is no verdict. With ticks on, the legend says which way is
which.

The ring is the same measurement as the markers, taken at 36 bearings, so it rebuilds a
moment after each rig change rather than instantly. The old ring stays up until the new
one is ready.

**Every arc assumes the sails are trimmed for best drive at that bearing.** At each of
the 36 bearings, every sail is swept through its own swing range and judged at the pose
that drives hardest there, within the same swing limits the game enforces on you. Sails
are not left centered, and they are not left wherever they happen to sit. Reef state is
taken as rigged: the ring answers "what would the helm do if I sailed here and trimmed
properly, with the canvas I have set", not "what is it doing right now". It also does not
cheat the trim to flatter the helm: each sail is set for drive, and the balance is
whatever falls out of that, because that is the trim you will actually sail.

## Heel and the waterline

The wind arrow has a knob on its outer end. Drag it out for more wind and in for less; the
number beside it is the speed in knots, up to 100. Point at the arrow or the knob before you
press: the one a press would take turns white and the other fades. When the shipyard opens, the
arrow starts at the wind blowing in port, at its bearing against the way the boat is lying, and
its speed.

The boat is heeled in that wind from the game's own buoyancy points and her weight, and every
reading, the markers, the helm bar and the ring, is taken at that heel. A heeled boat carries
more weather helm, because her drive acts off to leeward, so the same rig can read balanced in
a breeze and blue in a gale. The heel is worked out from her full weight at the hull's design
center of mass, the point the yellow marker shows, so cargo and where you stand do not change
it.

A light blue line on the hull marks where the sea would meet her at that heel, with the heel
angle beside it. The boat is never moved, so the line is drawn on her as she sits in the
cradle: at a steep heel it runs low on the high side and up across the deck on the low side.
It turns red when water would come aboard through one of the hull's splash points, the same
points the game floods her through. A plus after the angle means no balance was found before
the hull stops righting her: she would keep going over.

It is a balance in flat water. Waves, gusts, cargo and crew moving about all change how she
actually sits. With `LogHeelAtSea` on, the log compares the predicted heel and ride height with
the real ones once a minute while you sail.

## Judging a rig

Swap a sail, resize one, raise or lower a spar. The markers and the ring rebuild a moment
later, before you confirm the order and before any money changes hands. Adding sail area
forward pushes the helm toward lee; adding it aft pushes it toward weather.

The markers start at close hauled, 45 degrees off the bow, because that is where balance
bites. After that the arrow owns the angle: drag it and the markers re-read at whatever
bearing you leave it on. The magenta marker can jump to the other side of the boat near
a dead run, at the bearing where a sail's best trim changes sides.

## The reef track

Select a sail and a thin second line appears, running from the magenta marker to where it
would go if that one sail were reefed away, with every other sail left alone. Its color is
the helm you would have at that end, so it answers "what does this sail do to her helm"
before you touch anything.

## Furling one sail

The gizmo reads each sail as it is set right now, so a furled sail adds almost nothing to the
balance, and with none set at all the bar goes gray. Sails can come in furled, for example after
loading a save, so set the ones you want counted. Select a sail and press G to furl it, or to set it if it is furled. The key can be
changed or turned off with `FurlSelectedSailKey`, and it is also off while `ShowPanel` is off. The same toggle is a button: with Shipyard
Expansion installed it is "Furl this sail", under SE's "Furl Sails" button, and without SE
it is a round button that appears at the end of the strip while a sail is selected.

## Sail forces

The pink arrow button draws an arrow at each sail's center of effort, showing the force that
sail makes at the wind arrow's angle, trimmed for best drive. Only the horizontal part is
drawn, the side force and the drive, because that is the part that decides the helm. The
sail pulling hardest gets the longest arrow and the rest are in proportion.

Each arrow has that sail's force written beside its head. Read the numbers rather than the
lengths when comparing sails, because the arrows are drawn in 3D and perspective distorts
them: an arrow pointing toward you looks shorter than it is.

The button cycles through three states: off, arrows with the force in kilonewtons, and arrows
with the force as a percentage. The unit is printed on every label, so you can always tell
which you are looking at.

- **Kilonewtons** is about what a real sail of that area would pull in a 10 m/s (about 19
  knot) apparent wind, the wind the mod evaluates every rig in. The game decides how hard each
  sail draws at its angle, and real air density sets the size of the number. In a stronger or
  lighter wind every sail's force goes up or down together, so the comparison between sails
  holds at any wind.
- **Percent** is that sail's share of all the sails' force added together, ignoring which way
  each one pulls, so the numbers on a rig add up to 100.

Neither number is how much a sail drives the boat forward, since a sail pulling mostly
sideways counts as much as one pulling ahead, and neither is how much it moves the helm bar,
which also depends on where along the boat the sail sits. Sails under 3 percent keep their
arrow but get no number, to keep a big rig readable.

The arrows also explain the magenta marker. It is the center of the arrows' starting points,
weighted by their size, so one big sail pulling hard drags it toward itself. In percent mode, each
label is that sail's weight in the marker's position. Swing the wind
arrow and watch the arrows change length to see why the marker moves.

## The yellow marker

The center of mass behaves differently from the other two, and it is worth knowing why.

It does **not** respond to sails or deck parts. In Sailwind a mast, a sail, a forecastle or
a cabin adds to the boat's mass without moving its center of mass at all. So you can hang
an entirely different sail plan and the yellow ball will not move.

It **does** respond to a keel or ballast option, where a hull has one. In vanilla that is
the junk medium's deep keel, which drops the center of mass 0.75 m. With Shipyard Expansion
installed the kakam, the dhow and the cog gain a deep keel too, and the sanbuq a cladding
option.

It also sits below the hull on most boats. That is not a bug; a low center of mass is how
the game gives a boat a strong righting moment.

Its one use with the other markers is heel: the game takes torque about the center of mass
and applies the sail force up at the sail, so the height of magenta above yellow is your
heeling lever.

## The buttons

Five round buttons sit in the top right. The three colored discs match the three markers;
click one to hide or show that marker, and the choice is remembered across sessions. The
fourth, a small ring wearing the rose's own colors, hides the balance ring and the waterline, and turning it
off also skips the sweep behind it, which is most of what the mod spends on each rig
change. The fifth, a pink arrow, shows the sail forces. Hover any button for its name. The
strip itself is fixed in place.

## The red marker

If the magenta marker turns red and a line of text appears, one of the sails on the boat
has a center of effort that does not follow its own size, so the combined figure is
unreliable. In practice this meant one of Shipyard Expansion's three added lug sails, which
Shipyard Expansion fixed in v0.11.0. On SE v0.10.1 and earlier you will see it. Sails from
mods that place their own center of effort on every force update, such as MoreSailwindSails,
are recognized and do not turn it red. See [Mod Compatibility](Mod-Compatibility).
