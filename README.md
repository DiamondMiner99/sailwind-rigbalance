# Sailwind Rig Balance

> **Early alpha.** The gizmo works and is safe to run - it only draws, and never touches
> your boat, your save, or the game's physics. It has been exercised on a small number of
> hulls so far.

A shipyard tool for [Sailwind](https://store.steampowered.com/app/1764530/Sailwind/) - a
sailing and trading game on Steam.

Three markers float on your boat in the shipyard showing where the sail plan pushes and
where the hull pushes back, so you can tell whether a rig will balance **before you pay for
it**. If you've played Kerbal Space Program, it's the centre of gravity and centre of lift
markers, for a boat.

## Features

- **Three markers** - centre of effort, centre of lateral resistance, centre of mass
- **A helm verdict at a glance** - a coloured link between two of them, red for weather helm, blue for lee helm, green for balanced
- **Live** - swap, resize or move a sail and the markers follow, before you confirm the order
- **Measured, not tabulated** - every centre of effort comes from the sail's actual geometry driven through the game's own force function, never a per-sail-type constant
- **Visible through the hull and at night** - two of the three markers sit inside the boat, and unlit markers hold their colour after dark
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
| **magenta** | centre of effort - where the whole sail plan pushes |
| **blue** | centre of lateral resistance - where the hull pushes back |
| **yellow** | centre of mass - the boat's own balance point |

The reading is the **fore and aft gap between magenta and blue**, and nothing else. The
link between them drops from the sail plan down to the hull's height and then runs across,
so that gap is the horizontal leg, at true scale, from any camera angle.

| what you see | what it means |
|--------------|---------------|
| magenta **aft** of blue, link **red** | weather helm - she rounds up into the wind |
| magenta **forward** of blue, link **blue** | lee helm - she bears away when you let go |
| close together, link **green** | balanced |

A touch of weather helm is what you want. Lee helm is the one to avoid.

Three round buttons in the top right, coloured to match, hide and show each marker. Hover
one for a name. Drag the strip anywhere and it stays put.

**The yellow marker behaves differently.** It does not respond to sails or deck parts - in
this game a mast, a sail or a forecastle adds weight without moving where the boat
balances. It does respond to a keel or ballast option where a hull has one.

## Configuration

`BepInEx/config/com.sailwindrigbalance.mod.cfg`, written on first run.

| setting | default | what it does |
|---------|---------|--------------|
| `ShowCentreOfEffort` | true | magenta marker |
| `ShowCentreOfLateralResistance` | true | blue marker |
| `ShowCentreOfMass` | true | yellow marker |
| `ShowPanel` | true | the button strip |
| `DrawThroughHull` | true | draw markers on top of the boat |
| `MarkerScale` | 1.0 | marker size multiplier |
| `WindAngleDegrees` | 45 | wind angle used to judge the rig |
| `BalancedThresholdMetres` | 0.3 | how far off centre still reads as balanced |
| `PanelPosition` | unset | remembered after you drag the strip |

`DrawThroughHull` is the one worth knowing about: it's what makes the two markers inside the
hull visible at all. Turn it off if it ever conflicts with another mod's rendering.

## Mod compatibility

Shipyard Expansion is supported. Resizing a sail, rotating a spar and adding sails to stays
all work, because the centre of effort is read from the sail's live geometry and SE updates
the sail area on every resize. SE's deep keel options move the yellow marker.

**One caveat:** SE's three added lug sails keep their centre of effort outside the part that
gets scaled, so it doesn't move when the sail is resized. When one is aboard, the magenta
marker turns red and says so. The mod flags this and nothing else - it does not modify
Shipyard Expansion.

## Troubleshooting

**No markers in the shipyard:**
- Check `BepInEx/LogOutput.log` for `[Init] 7 shipyard hook(s) applied`. It names any hook
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
