# Building from Source

## Requirements

- .NET SDK able to target `net472`
- A Sailwind install, to reference the game assemblies from

No copy of the game's assemblies is committed to this repository, and none is needed
beyond the one in your own install.

## Build

```
dotnet build src/SailwindRigBalance/SailwindRigBalance.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sailwind"
```

The DLL lands in `src/SailwindRigBalance/bin/Release/net472/`. Copy it to
`BepInEx/plugins/SailwindRigBalance/`.

Referenced assemblies are never copied to the output directory. Without that, the build
folder fills with `Assembly-CSharp.dll` and friends, and anyone who copies the whole folder
into `BepInEx/plugins` ships the game's own assemblies back into the loader.

## Project structure

```
src/SailwindRigBalance/
  Plugin.cs              entry point, patch application, environment log
  Config/Settings.cs     every configurable value
  Core/RigModel.cs       drives the game's force function, computes the balance
  Shipyard/              Harmony hooks deciding when to recompute
  UI/ShipyardGizmo.cs    markers, overlay camera, button strip
  Compat/ModDetect.cs    runtime detection of other mods
```

## Read FINDINGS.md first

`FINDINGS.md` in the repository root documents how Sailwind's sail and hull model actually
works, with a `file:line` citation for every claim, and records the places where an earlier
conclusion turned out to be wrong.

It is worth reading before touching anything in `Core`. Several things in there are not
what you would guess:

- there is no center of lateral resistance as a hull property; the hull origin is the
  effective one, and that is derived from where the game applies lateral drag
- hull lateral drag is applied as an acceleration, so its equivalent force scales with the
  boat's mass and is a first-class term, not a rounding error
- sails and masts add mass without moving the center of mass
- the center of mass must be read from `BoatKeel`, not from the rigidbody, because the game
  only maintains the rigidbody value for the boat you sailed in on

## Design rules this codebase follows

**No invented identifiers.** Every game or mod identifier referenced is one that has been
read in decompiled source or in a mod's own source, cited in a comment where it is not
obvious. If something cannot be found, that is recorded rather than guessed at.

**Minimal patch surface.** Seven Harmony postfixes, each of which does nothing but set a
flag. Patches are applied one class at a time rather than in a batch, so one failing target
costs one feature instead of all of them.

**Fail soft.** A member that does not resolve is logged and the feature degrades. Nothing
throws into the game's own code.

**Nothing runs outside the shipyard.** No work per frame anywhere else, and no
`FindObjectsOfType` in any update loop.

## What is not committed

Decompiled game source and decompiled mod source are never committed and never published.
`.gitignore` covers the decompile output paths, build artifacts and scratch directories.
