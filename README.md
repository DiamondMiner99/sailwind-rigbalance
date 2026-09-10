# Sailwind Rig Balance

> **Early alpha.** The gizmo is safe to run: it never writes to your save and never applies
> a force to anything. The one thing it does change is the furl state of a sail, and only
> when you press its furl button, which is the same field the shipyard's own furl control
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
- **A helm verdict at a glance** - a colored link between two of them: blue for weather helm, red for lee helm, green for balanced, and the color deepens the further out of balance she is
- **A balance ring** - a rose around the boat showing what the helm would do at every point of sail at once, not just the one you are looking at
- **Live** - swap, resize or move a sail and the markers follow, before you confirm the order
- **Measured, not tabulated** - every center of effort comes from the sail's actual geometry driven through the game's own force function, never a per-sail-type constant
- **Visible through the hull and at night** - two of the three markers sit inside the boat, and unlit markers hold their color after dark
- **Shipyard Expansion support** - resized sails, rotated spars, sails on stays, and SE's own keel options
- **Nothing to press** - enter a shipyard and it's there

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
| **magenta** | center of effort - where the whole sail plan pushes |
| **blue** | center of lateral resistance - where the hull pushes back |
| **yellow** | center of mass - the boat's own balance point |

The reading is the **fore and aft gap between magenta and blue**, and nothing else. The
link between them drops from the sail plan down to the hull's height and then runs across,
so that gap is the horizontal leg, at true scale, from any camera angle.

| what you see | what it means |
|--------------|---------------|
| magenta **aft** of the blue marker, link **blue** | weather helm - she rounds up into the wind |
| magenta **forward** of the blue marker, link **red** | lee helm - she bears away when you let go |
| the two close together, link **green** | balanced |
| link **gray** | no verdict: she is running, or the sails are not drawing |
| link at its **deepest** blue or red | off the scale - the center of effort is a quarter of the hull's length or more from the pivot, which one oversized sail on a small hull manages easily |

A touch of weather helm is what you want. Lee helm is the one to avoid. The color deepens
the further out of balance she is, so a faint blue is a touch of weather helm and a strong
one is a fistful.

### The balance ring

A rose is drawn on the water around the boat, colored the same way. **The top of the ring
is your bow**, and each arc answers: if the wind came from that bearing, what would the helm
do? The white arrow marks the bearing the markers are currently reporting, and you can drag
it round to look at any other.

Read the ring as a whole. Green and blue all the way round is a boat that behaves on every
point of sail. **Red anywhere is the finding**, because it means she develops lee helm on
some heading you will eventually sail, and a single close-hauled reading would never have
shown you that. Gray means no answer: she is running, so there is no side force to balance.
An arc at the deepest blue or red may be past the scale entirely; the log says by how much.

Four round buttons in the top right hide and show things. The three colored discs match
the three markers; the fourth, a small ring wearing the rose's own colors, is the balance
ring itself, and turning it off also stops the sweep behind it, which is most of what the
mod spends on each rig change. Hover any button for a name. The strip is fixed there:
nothing else in the shipyard can be dragged, and a panel that could be would read as
belonging to a different program.

**The yellow marker behaves differently.** It does not respond to sails or deck parts - in
this game a mast, a sail or a forecastle adds weight without moving where the boat
balances. It does respond to a keel or ballast option where a hull has one.

## Configuration

`BepInEx/config/com.sailwindrigbalance.mod.cfg`, written on first run.

| setting | default | what it does |
|---------|---------|--------------|
| `ShowCenterOfEffort` | true | magenta marker |
| `ShowCenterOfLateralResistance` | true | blue marker |
| `ShowCenterOfMass` | true | yellow marker |
| `ShowReefTrack` | true | where the center of effort would go if you furled the selected sail |
| `ShowBalanceRing` | true | the rose showing every point of sail at once |
| `ShowPanel` | true | the button strip |
| `DrawThroughHull` | true | draw markers on top of the boat |
| `MarkerScale` | 1.0 | marker size multiplier |
| `WindAngleDegrees` | 45 | wind angle the rose starts at; after that the arrow owns it |
| `BalancedThresholdFraction` | 0.02 | how far off center still reads as balanced, as a fraction of hull length |

`DrawThroughHull` is the one worth knowing about: it's what makes the two markers inside the
hull visible at all. Turn it off if it ever conflicts with another mod's rendering.

## Mod compatibility

Shipyard Expansion is supported. Resizing a sail, rotating a spar and adding sails to stays
all work, because the center of effort is read from the sail's live geometry and SE updates
the sail area on every resize. SE's deep keel options move the yellow marker.

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
