# Reading the Gizmo

Enter a shipyard. There is nothing to press.

## The three markers

| marker | name | what it is |
| --- | --- | --- |
| **magenta** | center of effort | where the whole sail plan pushes |
| **blue** | center of lateral resistance | where the hull pushes back |
| **yellow** | center of mass | the boat's own balance point |

They are drawn on top of the hull, so the two that sit inside the boat are still visible,
and they are unlit, so they hold their color at night and do not change shade with the
time of day.

## The reading

**The fore and aft gap between magenta and blue, and nothing else.**

The link between them is an elbow, not a straight line: it drops from the center of effort
straight down to the hull's height, then runs across to the blue marker. That is
deliberate. A sail plan's center of effort sits many meters above the hull, so a direct
line would be almost vertical and the gap that matters would be an invisible lean. As an
elbow, the horizontal run **is** the gap, at true scale, readable from any camera angle.

| what you see | what it means |
| --- | --- |
| magenta **aft** of the blue marker, link **blue** | **weather helm** - she rounds up into the wind |
| magenta **forward** of the blue marker, link **red** | **lee helm** - she bears away when you let go |
| close together, link **green** | **balanced** |
| link **gray** | **no verdict** - she is running, or the sails are not drawing |

A touch of weather helm is what you want. It means letting go of the wheel turns her into
the wind and stops her, rather than away from it. Lee helm is the one to avoid.

The color is a gradient, not three flat states: a faint blue is a touch of weather helm
and a deep blue is a fistful, and the same goes for red. The balanced band is 2 percent
of the hull's length either side of the blue marker by default, which is about 0.6 m on a
brig, and configurable as `BalancedThresholdFraction`.

Gray means the question does not apply: with the wind well aft there is almost no side
force, and a center of effort measured against a center of lateral resistance is a
statement about side force. The gizmo declines to answer rather than printing a confident
number that means nothing.

A rig can also be lopsided beyond what the scale measures: sails drawing hard with the
center of effort a quarter of the hull's length or more from the pivot, which one
oversized sail on a small hull manages easily. That is not gray, because it is not "no
answer". The link and the arc go straight to the deepest blue or red, and the log calls
it off the scale, with the direction and a rough figure. The magenta marker stays on the
sail plan itself rather than at the lever point, because a lever that size does not fit
on the drawing.

## The balance ring

A rose is drawn on the water around the boat, colored the same way as the link. **The top
of the ring is your bow**, and each arc answers one question: if the wind came from that
bearing, what would the helm do? The white arrow marks the bearing the markers are
currently reporting, and you can drag it around the ring to look at any other.

Read the ring as a whole. Green and blue all the way round is a boat that behaves on every
point of sail. **Red anywhere is the finding**: she develops lee helm on some heading you
will eventually sail, and a single close-hauled reading would never have shown you it.
Gray arcs near the bow and stern are normal; no boat sails straight into the wind, and
dead downwind there is nothing to balance.

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

## Judging a rig

Swap a sail, resize one, raise or lower a spar. The markers and the ring rebuild a moment
later, before you confirm the order and before any money changes hands. Adding sail area
forward pulls the center of effort forward, toward lee helm; adding it aft pushes toward
weather helm.

The markers start at close hauled, 45 degrees off the bow, because that is where balance
bites. After that the arrow owns the angle: drag it and the markers re-read at whatever
bearing you leave it on.

## The reef track

Select a sail and a thin second line appears: where the center of effort would go if that
one sail were reefed away, with every other sail left alone. It is the answer to "is this
sail pushing her bow up or down", read before touching anything. It is not drawn when
reefing that sail would swing the reading through a sign change, where a straight line
would lie about the path.

## Ghost markers

While an order is pending, faded copies of the magenta and yellow markers stay where they
stood on the boat you own, a thin magenta line runs from the old center of effort to the
new one, and a line under the button strip gives the old helm reading at the arrow's angle.
Confirm or cancel the order and the ghosts go, because the boat you own and the boat on the
cradle are the same boat again.

The ring gets a ghost too: a thinner, fainter ring just inside the live one, painted with
the verdicts of the boat you own. Read the two against each other to see which points of
sail the order improves and which it makes worse. It only appears when the order changes
something the ring can show.

"The boat you own" is the game's own record, the one Cancel puts back. It is taken when the
boat comes into the yard and again on every confirm and cancel. Until your first change
after that, the ghost keeps following the boat, so furling or unfurling sails to look at
the rig is fine. From your first change on, it holds still.

The magenta ghost follows the wind arrow: swing the arrow and it shows where the old rig's
center of effort was at that angle, not at the angle you happened to start from. It is
read from the balance ring, so it needs the ring switched on. The yellow ghost only moves
for keel options, since the yellow marker is the design center of mass and nothing else
in an order changes it.

Furling a sail after you have started an order moves the live marker and not the ghost,
because furling is not part of the order but the ring reads sails as rigged. If the ghost
seems to show a change you did not order, that is usually why.

## Sail forces

The pink arrow button draws an arrow at each sail's center of effort, showing the force that
sail makes at the wind arrow's angle, trimmed for best drive. Only the horizontal part is
drawn, the side force and the drive, because that is the part that decides the helm. The
sail pulling hardest gets the longest arrow and the rest are in proportion, so the arrows
compare sails with each other rather than giving a number of newtons.

This is the answer to "why is the magenta marker there". The center of effort is where the
sails' forces balance out, not the middle of the sail area, so one big sail pulling hard
drags it toward itself. A large jib on a reach, for example, can pull the center of effort
well forward of where the canvas alone would put it. Swing the wind arrow and watch the
arrows change length to see why the marker moves.

## Wind streaks

For a few seconds after you arrive and whenever you move the wind arrow, faint streaks drift
across the ring in the arrow's wind, from the side the wind comes from to the side it blows
toward. The arrow sits on the ring where the wind comes from and points the way the air
moves; the streaks show the same thing in motion. They are the arrow's wind, not the weather
outside, and they fade out on their own. `ShowWindStreaks` turns them off.

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
fourth, a small ring wearing the rose's own colors, hides the balance ring, and turning it
off also skips the sweep behind it, which is most of what the mod spends on each rig
change. The fifth, a pink arrow, shows the sail forces. Hover any button for its name. The
strip itself is fixed in place.

## The red marker

If the magenta marker turns red and a line of text appears, one of the sails on the boat
has a center of effort that does not follow its own size, so the combined figure is
unreliable. In practice this meant one of Shipyard Expansion's three added lug sails, which
Shipyard Expansion fixed in v0.11.0. On SE v0.10.1 and earlier you will still see it. See
[Mod Compatibility](Mod-Compatibility).
