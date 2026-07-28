# Sailwind Rig Balance

> **Early alpha.** The gizmo works and is safe to run, but it has been exercised on a
> small number of hulls. It only ever draws; it does not change your boat, your save,
> or the game's physics.

A shipyard tool for [Sailwind](https://store.steampowered.com/app/1764530/Sailwind/).
It shows you where a sail plan pushes and where the hull pushes back, so you can judge
whether a rig will balance **before you pay for it**.

Think of the centre of gravity and centre of lift markers in Kerbal Space Program, but
for a boat.

## What it shows

Three markers, floating in the shipyard, visible through the hull and at night.

| marker | what it is |
| --- | --- |
| **magenta** | centre of effort: where the whole sail plan pushes |
| **blue** | centre of lateral resistance: where the hull pushes back |
| **yellow** | centre of mass: the boat's own balance point |

A coloured link runs between magenta and blue. It drops straight down from the centre
of effort to the hull's height, then across. **That horizontal run is the answer.**

- Magenta **aft** of blue: the rig pushes behind the pivot, the bow swings up into the
  wind. **Weather helm**, and the link turns red.
- Magenta **forward** of blue: **lee helm**, and the link turns blue. This is the one to
  avoid; she bears away when you let go of the wheel.
- Stacked within 0.3 m: **balanced**, and the link turns green. A touch of weather helm
  is what you actually want.

Swap a sail, resize one, raise a spar, and watch it move before you confirm the order.

## What the numbers come from

Nothing here is a naval architecture formula. Every value is taken from the game's own
model.

The centre of effort is computed by driving Sailwind's own sail force function with a
synthetic close-hauled wind, one sail at a time, at whichever trim gives that sail the
most drive, then combining the results weighted by how hard each is pulling. Each sail's
centre comes from live geometry, so it follows a resize or a raised spar. Nothing is
read from a per-sail-type constant.

The centre of lateral resistance is the hull origin. Sailwind has no CLR as a hull
property, but its lateral drag is applied at the rigidbody's own position with a zero
height offset on every hull, so in steady sailing that is where resistance acts. It is
derived from the sim, not assumed.

The centre of mass is read from the hull's keel data. It does **not** respond to sails or
deck parts: in this game a mast or a sail adds weight without moving the balance point.
It does respond to a keel or ballast option where the hull has one, which in vanilla is
the junk medium's deep keel, and with Shipyard Expansion installed also the kakam, the
dhow, the cog and the sanbuq.

## Installation

### Step 1: BepInEx (mod loader)

If you already run any Sailwind mod you have this. If not, install BepInEx 5.4.x for
Windows x64 into your Sailwind folder and run the game once so it creates its
directories.

### Step 2: the mod

Extract the release zip into your Sailwind folder. You should end up with:

```
Sailwind/BepInEx/plugins/SailwindRigBalance/SailwindRigBalance.dll
```

Launch the game and enter any shipyard.

## Using it

There is nothing to press. Enter a shipyard and the markers appear.

A small strip of three round buttons sits in the top right corner, coloured to match the
markers. Click one to hide or show that marker; hover it for a name. Drag the strip
anywhere you like and it stays there, across sessions.

## Configuration

Settings live in `BepInEx/config/com.sailwindrigbalance.mod.cfg`, written the first time
you run the mod.

| setting | default | what it does |
| --- | --- | --- |
| `ShowCentreOfEffort` | true | magenta marker |
| `ShowCentreOfLateralResistance` | true | blue marker |
| `ShowCentreOfMass` | true | yellow marker |
| `ShowPanel` | true | the button strip; turn off for markers with no interface |
| `DrawThroughHull` | true | draws markers on top of the boat, using an overlay camera |
| `MarkerScale` | 1.0 | marker size multiplier |
| `WindAngleDegrees` | 45 | apparent wind angle used to judge the rig |
| `BalancedThresholdMetres` | 0.3 | how far off centre still reads as balanced |
| `PanelPosition` | unset | remembered after you drag the strip |

Two of these are worth knowing about. `DrawThroughHull` is what makes the markers inside
the hull visible at all; if it ever conflicts with another mod's rendering, turning it
off leaves a working but occluded gizmo instead of a broken screen. `WindAngleDegrees`
defaults to 45 because balance matters close hauled; near a run there is almost no side
force to balance and every rig reads as neutral.

## Mod compatibility

Shipyard Expansion is supported. Resized sails are handled correctly: Sailwind's own
centre of effort sits inside the part SE scales, so it tracks width, height and spar
changes, and the sail area the force model reads is updated by SE on every resize.

**One caveat.** SE's own three added lug sails keep their centre of effort outside the
part that gets scaled, so it does not move when the sail is resized. When one of those is
on the boat, the magenta marker turns red and a line of text says so. The mod flags this
and nothing more; it does not modify Shipyard Expansion.

Everything else is left alone. The mod declares soft dependencies on Shipyard Expansion,
Sail Collision Fix, NAND Tweaks, Towable Boats and HMS Leopard purely so it loads after
them, and detects what is actually present at runtime.

## Troubleshooting

**No markers in the shipyard.** Check `BepInEx/LogOutput.log` for lines beginning
`[Gizmo]` or `[Init]`. `[Init] N shipyard hook(s) applied` should report 7 with none
failed. If a hook failed the log names it.

**Markers hidden inside the boat.** `DrawThroughHull` is off, or every layer on your
install is already named so the mod could not claim a private one. The log says which.

**Markers do not move when I change the rig.** The rebuild waits a moment for the
shipyard to settle. If they never update, `[Gizmo]` lines in the log will show whether a
rebuild ran.

## Building from source

### Requirements

- .NET SDK with `net472` targeting support
- A Sailwind install to reference the game assemblies from

### Build

```
dotnet build src/SailwindRigBalance/SailwindRigBalance.csproj -c Release -p:GameDir="C:\Path\To\Sailwind"
```

The output DLL lands in `src/SailwindRigBalance/bin/Release/net472/`. Copy it into
`BepInEx/plugins/SailwindRigBalance/`.

Referenced game assemblies are never copied to the output folder, so the build directory
is safe to zip wholesale.

### Project structure

```
src/SailwindRigBalance/
  Plugin.cs              entry point, patch application, environment log
  Config/Settings.cs     every configurable value
  Core/RigModel.cs       drives the game's force function, computes the balance
  Shipyard/              Harmony hooks that decide when to recompute
  UI/ShipyardGizmo.cs    markers, overlay camera, button strip
  Compat/ModDetect.cs    runtime detection of other mods
```

`FINDINGS.md` documents how the game's sail and hull model actually works, with a
citation for every claim. It is worth reading before changing anything in `Core`.

## Credits & license

Sailwind is by Joel Rosenqvist. Shipyard Expansion is by NANDBrew.

Released under the MIT license.
