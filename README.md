# Sailwind Rig Balance

> **Early alpha.** The gizmo is safe to run: it never writes to your save and never applies
> a force to anything. The one thing it does change is the furl state of a sail, and only
> when you press its furl button or key, which is the same field the shipyard's own furl control
> writes. Working out the balance drives the game's sail force function directly, so it
> borrows each sail's trim for the length of that call and puts it straight back. It has
> been exercised on a small number of hulls so far.

A shipyard tool for [Sailwind](https://store.steampowered.com/app/1764530/Sailwind/) - a
sailing and trading game on Steam.

Three markers float on your boat in the shipyard showing where the sail plan pushes and
where the hull pushes back, so you can tell whether a rig will balance **before you pay for
it**. If you've played Kerbal Space Program, it's the center of gravity and center of lift
markers, for a boat.

## Features

- **Three markers** - center of effort, center of lateral resistance, center of mass
- **A helm verdict at a glance** - a colored bar on the hull: blue for weather helm, red for lee helm, green for balanced, and the color deepens the further out of balance she is
- **A balance ring** - a rose around the boat showing what the helm would do at every point of sail at once, not just the one you are looking at
- **Heel and a waterline** - give the wind arrow a speed and the boat is heeled in that wind from her own buoyancy and weight, the helm is read at that heel, and a line on the hull shows where the sea would meet her. It turns red when water would come aboard
- **Starts at the real wind** - the wind arrow opens at the wind blowing in port, then it is yours to drag
- **Sail shading** - a sail sitting in another sail's wind shadow loses force, the way it does at sea
- **Live** - swap, resize or move a sail and the markers follow, before you confirm the order
- **Furl one sail at a time** - select a sail and press G (or the mod's furl button) to furl or set it, so you can see what each sail does to the balance
- **Sail forces** - a button that draws each sail's pull as an arrow with its force written beside it, in kN or as a share of the rig, so you can see which sail is doing the work
- **Measured, not tabulated** - every center of effort comes from the sail's actual geometry driven through the game's own force function, never a per-sail-type constant
- **Visible through the hull and at night** - two of the three markers sit inside the boat, and unlit markers hold their color after dark
- **Shipyard Expansion support** - resized sails, rotated spars, sails on stays, and SE's own keel options
- **Nothing to press** - enter a shipyard and it's there
- **Hull showcase** - off by default (`ShowHullShowcaseButton`); a main menu button that steps through every boat in the world with its waterline, heel and balance ring drawn on it, before any save is loaded. It only reads: no boat moves and nothing is saved

## Installation

### Step 1: BepInEx (mod loader)

If you already run any Sailwind mod, skip to step 2.

1. Download [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) (the `x64` build).
2. Find your Sailwind folder (Steam, right-click Sailwind, Manage, Browse local files).
3. Extract the BepInEx zip into the Sailwind folder (you should see `winhttp.dll` and a
   `BepInEx` folder next to `Sailwind.exe`).
4. Run the game once and close it - this creates `BepInEx/plugins`.

**Linux / Steam Deck:** set Sailwind's launch options to
`WINEDLLOVERRIDES="winhttp=n,b" %command%`.

### Step 2: the mod

1. Extract the release zip into your Sailwind folder. You should end up with
   `Sailwind/BepInEx/plugins/SailwindRigBalance/SailwindRigBalance.dll`.
2. **When updating, overwrite the old file.** Two copies of the DLL anywhere under
   `BepInEx` and the loader picks one of them, which is how you end up running an old build
   without knowing.

## How to use it

Enter a shipyard. The markers appear on the boat.

| marker | what it is |
|--------|------------|
| **magenta** | center of effort - where the sails pull, with each sail weighted by its force at best trim |
| **blue** | center of lateral resistance - where the hull pushes back |
| **yellow** | center of mass - the boat's own balance point |

The reading is the **helm bar**: a colored post rising from the blue marker to the top of the
hull, with an arrow running along the centerline from there. The arrow's length is how hard she
wants to turn, as a lever in meters. It counts everything that turns the boat, including thrust
from a sail swung out to one side and the wind on the hull itself, so it can point aft while the
magenta marker sits forward of blue.

| what you see | what it means |
|--------------|---------------|
| arrow pointing **aft**, **blue** | weather helm - she rounds up into the wind |
| arrow pointing **forward**, **red** | lee helm - she bears away when you let go |
| **green** post, no arrow | balanced |
| **gray** post, no arrow | no verdict: she is running, or no sails are set or drawing |
| arrow at its full length, **dark blue** or **red** | off the scale - a lever a quarter of the hull's length or more, which one oversized sail on a small hull manages easily |

A touch of weather helm is what you want. Lee helm is the one to avoid. The color is a
gradient: weather helm goes from green through light blue to dark blue, and lee helm goes from
green through orange to red. Light blue or orange is helm the rudder holds easily; dark blue
or red is a lot of helm.

To see a tall rig to the top, hold the right mouse button and move the mouse up or down to
raise or lower the view. Side to side still pans as before, and the scroll wheel zooms out to
80 m instead of 40.

### The balance ring

A rose is drawn on the water around the boat, colored the same way. **The top of the ring
is your bow**, and each arc answers: if the wind came from that bearing, what would the helm
do? The white arrow marks the bearing the markers are currently reporting, and you can drag
it round to look at any other.

Read the ring as a whole. Green and blue all the way round is a boat that behaves on every
point of sail. **Red anywhere is the finding**, because it means she develops lee helm on
some heading you will eventually sail, and a single close-hauled reading would never have
shown you that. Gray means no answer: she is running, so there is no side force to balance.
An arc at dark blue or red may be past the scale entirely; the log says by how much.

If red and green are hard to tell apart, turn on `ColorblindPalette` for blue, white and orange,
and `ShowHelmTicks` for a tick at every bearing: out of the ring for weather helm, into it for lee
helm, longer the stronger the helm.

### Heel and the waterline

The wind arrow has a knob on its outer end. Drag it out for more wind and in for less; the
number beside it is the speed in knots, up to 100. Point at the arrow or the knob before you
press: the one a press would take turns white and the other fades. When the shipyard opens, the
arrow starts at the wind blowing in port: the bearing it comes from against the way the boat is
lying, and its speed.

The mod heels her in that wind, using the game's own buoyancy points and her weight, and reads
the helm at that heel. A heeled boat carries more weather helm, so a rig that balances in a
breeze can read blue in a gale. The heel is worked out from her full weight at the hull's design
center of mass, the point the yellow marker shows, so cargo and where you stand do not change it.

A light blue line on the hull marks where the sea would meet her at that heel, with the heel
angle beside it. The boat itself is never moved, so at a steep heel the line runs low on the
high side and up across the deck on the low side. The line turns red when water would come
aboard through one of the hull's splash points. A plus after the angle means there is no
balance: she would keep going over.

This is a balance in flat water. Waves, cargo and crew moving about all change how she
actually sits.

### The buttons

Five round buttons in the top right hide and show things. The three colored discs match the
three markers. The fourth, a small ring wearing the rose's own colors, hides the balance ring
and the waterline, and turning it off also stops the sweep behind the ring, which is most of
what the mod spends on each rig change. The fifth, a pink arrow, shows each sail's force.
Hover any button for its name. While the ring is shown, a legend of its colors hangs under the
buttons, with the camera's right-drag lift as its last row. The strip is fixed there: nothing else in the shipyard can be dragged, and a panel that
could be would read as belonging to a different program.

When you enter a shipyard the markers wait until the boat has settled into the cradle and the
ring is drawn, with "Calculating..." under the legend meanwhile, so the first thing you see is
the final reading.

**The yellow marker behaves differently.** It does not respond to sails or deck parts - in
this game a mast, a sail or a forecastle adds weight without moving where the boat
balances. It does respond to a keel or ballast option where a hull has one.

## Configuration

`BepInEx/config/com.sailwindrigbalance.mod.cfg`, written on first run.

| setting | default | what it does |
|---------|---------|--------------|
| `ShowCenterOfEffort` | true | magenta marker |
| `ShowCenterOfLateralResistance` | true | blue marker and helm bar |
| `ShowCenterOfMass` | true | yellow marker |
| `ShowReefTrack` | true | where the center of effort would go if you furled the selected sail |
| `ShowBalanceRing` | true | the rose showing every point of sail at once |
| `ShowHelmTicks` | false | a tick at every bearing of the ring, out for weather helm and in for lee helm, to read it without color |
| `ShowSailForces` | false | an arrow for each sail's force at the wind arrow's angle (also a button) |
| `SailForceUnits` | Kilonewtons | whether the arrows' numbers are kN or percent (the button cycles it) |
| `WindArrowStyle` | Downwind | which way the wind arrow points: the way the wind blows, or into it (`WeatherVane`) |
| `ShowPanel` | true | the button strip and the furl button |
| `FurlSelectedSailKey` | G | furls the selected sail, or sets it if it is furled |
| `ShowRingLegend` | true | a legend of the ring's colors under the button strip |
| `ColorblindPalette` | false | helm colors in blue, white and orange instead of blue, green and red |
| `ShowHullShowcaseButton` | false | a Hull showcase button on the main menu, for checking every hull's waterline |
| `ExtendShipyardCamera` | true | hold the right mouse button and move up or down to raise the shipyard view to the top of the rig, and zoom out further |
| `MaxZoomOutMeters` | 80 | how far the shipyard camera zooms out with `ExtendShipyardCamera` on (the game stops at 40); with HMS Leopard installed its own 80 m zoom is used instead |
| `DrawThroughHull` | true | draw markers on top of the boat |
| `MarkerScale` | 1.0 | marker size multiplier |
| `StartFromLocalWind` | true | the arrow starts at the wind blowing in port, its bearing against the boat and its speed |
| `WindAngleDegrees` | 45 | wind angle the rose starts at when `StartFromLocalWind` is off and in the hull showcase; after that the arrow owns it |
| `WindSpeedMetersPerSecond` | 10 | wind speed the arrow starts at when `StartFromLocalWind` is off and in the hull showcase |
| `ModelHeel` | true | heel the boat in the arrow's wind and read the helm at that heel; off reads her upright and hides the waterline |
| `ModelSailShading` | true | sails in another sail's wind shadow lose force, as at sea; off judges every sail in clear air |
| `ShowWaterline` | true | the line where the sea would meet the hull |
| `LogHeelAtSea` | true | while sailing, log the predicted heel against the actual heel once a minute |
| `BalancedThresholdFraction` | 0.02 | how far off center still reads as balanced, as a fraction of hull length |

`DrawThroughHull` is the one worth knowing about: it's what makes the two markers inside the
hull visible at all. Turn it off if it ever conflicts with another mod's rendering.

## Mod compatibility

Shipyard Expansion is supported. Resizing a sail, rotating a spar and adding sails to stays
all work, because the center of effort is read from the sail's live geometry and SE updates
the sail area on every resize. SE's deep keel options move the yellow marker.

MoreSailwindSails is compatible. Its sails are read through the same game force function as
any other, with that mod's own changes to it, and its Fisherman's Stays are not counted as
part of the hull. Its sails place their own center of effort on every force update, which the
mod recognizes, so they do not turn the magenta marker red.

**One caveat, on older Shipyard Expansion:** on SE v0.10.1 and earlier, the three added lug
sails keep their center of effort outside the part that gets scaled, so it doesn't move when
the sail is resized. When one is aboard, the magenta marker turns red and says so. SE v0.11.0
fixed it, so on current SE this doesn't come up. The check reads each sail's own geometry at
runtime rather than the SE version, so it goes quiet by itself once you update. The mod flags
this and nothing else - it does not modify Shipyard Expansion.

## Troubleshooting

**No markers in the shipyard:**
- Check `BepInEx/LogOutput.log` for `[Init] 9 shipyard hook(s) applied`. It names any hook
  that failed.
- `rig model available=False` means a game update moved something the force model reads, and
  the gizmo disabled itself rather than guess.

**Markers hidden inside the boat:**
- `DrawThroughHull` is off, or every Unity layer is already claimed by other mods. The log
  says which.

**Markers don't update when I change the rig:**
- Rebuilds wait a moment for the shipyard to settle. Each one logs a `[Gizmo]` line - if
  that never appears, a hook didn't apply.

**Everything reads as balanced:**
- Check `WindAngleDegrees`. Near a run there's almost no side force to balance.

## Building from source

<details>
<summary>For developers</summary>

### Requirements

- .NET SDK able to target `net472`
- A Sailwind install to reference the game assemblies from

### Build

```bash
dotnet build src/SailwindRigBalance/SailwindRigBalance.csproj -c Release \
  -p:GameDir="C:/Program Files (x86)/Steam/steamapps/common/Sailwind"
```

Output: `src/SailwindRigBalance/bin/Release/net472/SailwindRigBalance.dll`.

### Project structure

```text
src/SailwindRigBalance/
  Plugin.cs          # entry point, patch application, environment log
  Config/            # every configurable value
  Core/              # drives the game's force function, computes the balance
  Shipyard/          # Harmony hooks deciding when to recompute
  UI/                # markers, overlay camera, button strip
  Compat/            # runtime detection of other mods
```

Read [FINDINGS.md](FINDINGS.md) before changing anything in `Core/`. It documents how the
game's sail and hull model actually works with a `file:line` citation for every claim, and
records where an earlier conclusion turned out to be wrong.

</details>

## License

MIT licensed (see [LICENSE](LICENSE)).
