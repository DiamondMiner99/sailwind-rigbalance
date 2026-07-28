# Recon findings: centre of effort and lateral resistance in Sailwind 0.38.1

Read-only investigation for a shipyard rig-balance gizmo. No mod code has been written.

## How to read the citations

- `Sail.cs:401` and similar name a class and line in a **local decompile of the game's
  `Assembly-CSharp.dll`** (build 0.38.1, byte-identical to the installed assembly,
  sha256 `978A21A6...CE23`). `Crest/BoatProbes.cs` is the same for `Crest.dll`,
  `BoatAlignNormal.cs` for `Crest.Examples.dll`. That decompiled source is never
  committed and never published; only identifier names, single expressions and line
  numbers are quoted here, which is the same convention the sailwind-coop docs use.
- `SE/...` is NANDbrew's public Shipyard Expansion source (clone HEAD `40aacb9`,
  version 0.9.90). Where the **installed** 0.9.0 build matters it is called out
  explicitly.
- `level24`, `sharedassets1.assets` and similar are the game's shipped Unity data,
  read with UnityPy. Those values are measured, not inferred, and the probe scripts
  are reproducible.
- Every identifier below was read. Where something is inference rather than a read, it
  says so.

Two review passes independently re-derived the asset numbers from scratch and found no
fabricated identifiers. Corrections they produced are folded in.

---

## 1. The centre of effort is real, and it is one Transform per sail

`Sail.windcenter` (`Sail.cs:14`) is a `public Transform`. Across the whole assembly it
has **zero assignment sites**: eight pure reads (`Sail.cs:387, 411, 412, 468, 469, 497,
500, 507`) and the declaration. It is authored in the prefab.

It is the centre of effort in the only sense that matters, because it is used for both
jobs at once:

- **Force application point.** `Sail.ApplyForce` (`Sail.cs:455-471`) makes exactly two
  `AddForceAtPosition` calls per sail per `FixedUpdate`, both at `windcenter.position`
  (`Sail.cs:468`, `:469`), one along `shipRigidbody.transform.forward`, one along
  `.right`, `ForceMode.Force`. There is no aggregation and no `AddTorque` on the sail
  path, so each sail's moment arm is preserved exactly.
- **Height-curve input.** `Sail.GetWindHeightMult(float worldY)` (`Sail.cs:401-404`) is
  `Mathf.Lerp(0.8f, 1.4f, Mathf.InverseLerp(0f, 22f, worldY))`, called with
  `windcenter.transform.position.y` (`Sail.cs:411`).

**The 80-to-140% over 0-to-22m curve is exactly as described.** Those are the real
constants, and a grep for a height term across the sail, wind, mast and hull files finds
nothing else: `position.y` appears only at `Sail.cs:411`, `:412` and in `Mast`'s
sail-ordering sort. `Mathf.InverseLerp` clamps, so the multiplier saturates at 0.8 below
sea level and 1.4 above 22 m.

Height is **raw world Y with sea level assumed to be zero**. That assumption is correct
at sea: Crest's `OceanRenderer` sits on the scene-root GameObject `Ocean (Crest)` at
world `(0,0,0)`, Crest creates its `Root` as a child at `localPosition` zero, and
`OceanRenderer.LateUpdatePosition` explicitly preserves `Root.position.y`, so
`SeaLevel` (`Crest/OceanRenderer.cs:290`) is latched at 0.0 forever. The floating origin
never touches Y either: shifts are built as `new Vector3(x, 0f, z)`
(`FloatingOriginManager.cs:178`, `:214`). **In the shipyard the assumption is wrong, and
by a measured amount - see section 5.**

### The question that decided the architecture: does the CE move when a sail is scaled?

No source file states `windcenter`'s parent, so this was measured out of the shipped
prefabs. **Answer: it is inside the scaled subtree, uniformly, for all 96 stock sail
prefabs.**

| Measured | Result |
|---|---|
| Sail prefabs in `PrefabsDirectory.sails` | 135 slots, 96 non-null, 39 genuine nulls |
| `windcenter` is a descendant of `cloth.transform.parent` | 96 of 96 |
| `windcenter`'s `m_Father` equals `cloth`'s `m_Father` | 96 of 96 (it is a **sibling of the cloth**) |
| Depth of `windcenter` below the prefab root | 2, for all 96 |
| SE's `GetComponentInChildren<Animator>().transform` equals `cloth.transform.parent` | 96 of 96 (exactly one Animator per prefab) |
| Composed rotation, root to `windcenter` | identity for all 96 |

`Sail.ChangeScale` writes both `localScale` and `localPosition` on that same transform
(`Sail.cs:183-186`), and because `windcenter` is a direct child with `localPosition.z`
of 0 and the parent's `localPosition.y` of 0, `windcenter`'s position in root space maps
**affinely** under a resize: root-space X scales by the x-scale ratio, root-space Z by
the y-scale ratio, root-space Y stays 0. Simulating `ChangeScale` over all 96 prefabs at
six scale steps gives a worst-case deviation from a pure affine map of 1.6e-05 m.

**So vanilla's moment arm and its height multiplier are both correct for resized sails,
including SE-resized ones.** There is no vanilla defect here, and no CE-versus-centroid
divergence to draw for stock sails. That is the opposite of what a code-only reading
suggested, and it simplifies the mod considerably: read `windcenter.position` and apply
no compensation.

### Facts that will bite an implementation

- **The `Windcenter` component is attached to nothing.** The class exists
  (`Windcenter.cs`, an empty `Start`/`Update` stub) but an exhaustive scan of
  `resources.assets`, all 43 `sharedassets*.assets` and all 43 `level*` files finds
  **zero instances**. `GetComponent<Windcenter>()` will never find the CE.
- **Do not name-match either.** The GameObject is called `wincenter & sail audio` in all
  96 prefabs, misspelled, and its components are `Transform`, `AudioSource`,
  `SailFlapAudio`. Scene sails in `level24` use at least six other names, 19 of them
  the correctly spelled `windcenter`.
- **Null-check `windcenter`.** Four shipped `Sail` components in `level24` (on `boom`,
  `boom`, `squareboom`, `genoa_boom` under the waverider test boats) have
  `windcenter == null` with a non-null `cloth`.
- **`sailArea` is 0.0 in every shipped prefab**, along with `scaledInstallHeight`. It is
  not editor-baked; it is filled only by `Sail.Start` calling `SetSailArea()`
  (`Sail.cs:335`) and refreshed on every `ChangeScale` (`Sail.cs:188`) and in
  `ShipyardSailInstaller.AddNewSail` (`:56`). `SailAreaPrefabSetter` is an
  `[ExecuteInEditMode]` tool behind a `run` bool that was evidently never run.
- **`minAngle` / `maxAngle` are 0.0 in every prefab.** `Sail.Start` back-fills them from
  the `HingeJoint` limits (`Sail.cs:322-329`), which are uniform across all 96 prefabs:
  `min = -50`, `max = +50`, axis `(0,0,1)`, anchor `(0,0,0)`, `useLimits = true`,
  `useSpring = false`, `useMotor = false`. The collision sweep then narrows them.
- **`sailAmplifier` and `windEfficiency` are dead.** Both are public on `Sail` with zero
  reads or writes anywhere in the assembly. `sailAmplifier` is authored with plausible
  per-prefab values (measured range 0.26 to 2.65 over 42 distinct values), which makes
  it an inviting trap. The live knob is `upwindEfficiency`.
- **`Debugger.instance.debugSailAreaMult` is serialized as 0.02, not the `1f` field
  initializer.** `GetRealSailPower()` (`Sail.cs:450-453`) is therefore
  `sailArea * 0.02` in the shipped game. `debugYardMult` is 0.015 as in source, and it
  is a **force floor**: `Mathf.Max(debugYardMult, currentUnroll)` (`Sail.cs:415`) means a
  fully furled sail still makes 1.5% of its force. Both `Debugger` components live in
  `level24`, which is never unloaded, so `Debugger.instance` is always non-null in a
  shipyard and the unguarded dereferences are safe.

### Authored per-sail values worth having

`upwindEfficiency` has no per-sail variation at all, only per category:

| Category | `upwindEfficiency` | Count |
|---|---|---|
| square | 0.25 | 28 |
| lateen | 0.50 | 11 |
| junk | 0.44 | 7 |
| gaff | 0.48 | 25 |
| other | 0.44 | 4 |
| staysail | 0.50 | 21 |

`installHeight` is the one genuinely per-prefab geometry number (2.40 to 22.77).
`squareSail` is exactly equivalent to `category == SailCategory.square`. 49 of the 96
prefabs are flagged `obsolete`.

---

## 2. Where the force lands, and how a what-if can be computed

The whole sail-force chain is:

1. `Sail.Update` calls `UpdateWindForceOnSail()` (`Sail.cs:338-345`).
2. `Sail.FixedUpdate` sets `apparentWind = GetApparentWind()`, then calls `ApplyForce()`
   and `ApplyRotationFromWind()` (`Sail.cs:347-356`).

So compute and apply straddle two different Unity callbacks, and the force applied in a
physics step uses the previous render frame's numbers. A sampler sees a one-step-stale
mix, not a self-consistent snapshot.

`UpdateWindForceOnSail` (`Sail.cs:407-448`) is the useful part. It takes no arguments,
reads the apparent wind from the **public mutable field** `Sail.apparentWind`
(`Sail.cs:52`), contains no `AddForce` of any kind, and writes only to the sail's own
fields. **A synthetic-wind what-if is therefore possible by writing a fake
`apparentWind`, invoking the method, reading the results, and restoring.** It writes
exactly nine fields: `relativeWindAngle`, `totalWindForce`, `outHeightMult`,
`appliedWindForce`, `pushingBoatBackwards`, `outForwardShare`, `outSidewayShare`,
`unamplifiedForwardForce`, `unamplifiedSidewayForce`.

Only `appliedWindForce` and `apparentWind` are public; the two force components are
private (`Sail.cs:67`, `:69`). Six vanilla classes read `appliedWindForce` live (rope
tension in the four `RopeControllerSailAngle*`/`Reef` classes, `JibSelfRighting`,
`Hints`), and `WindCloth` plus `SailFlapAudio` read the wind fraction, so a what-if must
save and restore or it will visibly perturb rope resistance, cloth and audio for a frame.

The scaling `ApplyForce` puts on top, all of which the gizmo must reproduce:

```
force_forward = unamplifiedForwardForce * GetRealSailPower() * categoryMult * 50f
force_side    = unamplifiedSidewayForce * GetRealSailPower() * categoryMult * 50f * 1.5f
```

`categoryMult` is 0.75 for junk, 0.85 for gaff, 1.0 otherwise (`Sail.cs:458-465`). The
**side force carries an extra 1.5x** (`Sail.cs:467`) that a naive model would miss, and
the forward component alone gets a `-0.33` reverse-thrust factor when the sail pushes
backwards (`Sail.cs:429`); the side component's sign factor is plus or minus 1.

The other force paths that touch yaw:

| Source | Application point | Mode | Measured constants |
|---|---|---|---|
| Sails | `windcenter.position` | Force | see above |
| `BoatWind` hull windage (`BoatWind.cs:33`) | `transform.TransformPoint(forceOffset)` | Force | `frontForce` 1.45 on all hulls; `sideForce` 6.0 on all 7 active hulls; `forceOffset` **(0,0,-7)** dhow and kakam, **(0,0,-8)** cog, **(0,0,-12)** sanbuq, brig, jong, junk. The `(0,0,-2)` source default is overridden on every playable boat |
| `Rudder` (`Rudder.cs:62`, `:67`) | none - `AddRelativeTorque` plus a `Vector3.back` force at the COM | Force | rudder **position is physically irrelevant** |
| Hull lateral drag | see section 3 | Acceleration | see section 3 |
| Buoyancy (`Crest/BoatProbes.cs:194`) | per force point | - | direction is hard-coded `Vector3.up`, so **zero yaw** |

`Sail.ApplyRotationFromWind` (`Sail.cs:387-388`) pushes the sail's *own* rigidbody at
`windcenter.position` with `apparentWind * 0.22f`; that reaches the hull only as a
`HingeJoint` reaction and is not in the gizmo's budget.

---

## 3. There is no centre of lateral resistance in this game

This is the finding that most changes the product, and it survived a deliberate attempt
to refute it.

Hull sideways resistance is **one linear force**:
`transform.right * Vector3.Dot(transform.right, -velocity) * _dragInWaterRight`, applied
by `Crest.BoatProbes.FixedUpdateDrag` (`Crest/BoatProbes.cs:222`) at the single point
`_rb.position + _forceHeightOffset * Vector3.up` (`:220`), with
`ForceMode.Acceleration`.

Measured out of `level24`, where all 12 `BoatProbes` in the entire game live:

- **`_forceHeightOffset` is literal hex `0x00000000`, i.e. 0.0, on every one of the 12
  hulls.** The lateral force is applied at exactly `_rb.position`, the hull transform
  origin, with no vertical offset. There are zero writers anywhere.
- `_dragInWaterRight` is **3.0 on every active hull** (6.0 appears only on two inactive
  brig variants). It is a constant: `HullDrag` computes `addedSideDrag` every
  `FixedUpdate` (`HullDrag.cs:28`) and **nothing ever reads it**, in either assembly. So
  lateral drag does not scale with speed the way forward drag does, and
  `HullDrag.baseSideDragMult` (0.1 on the starter dhow, 0.0 on all 11 others) has no
  physics effect at all.
- There is no keel Transform, no lateral sample array, no hull-shape or waterplane
  sampling. `BoatKeel` and `BoatCustomKeel` only write `Rigidbody.centerOfMass`. A
  case-insensitive search for `lateral`, `leeway`, `sideslip` or `CLR` across both
  decompiles hits nothing relevant.
- Buoyancy **is** multi-point (`_forcePoints`, 12 entries, all `_weight` 1.0, one
  byte-identical authored offset template shared by all 12 hulls, differentiated only by
  a per-hull `customScale`), but its force is strictly `Vector3.up`, so it contributes
  heave and roll and **exactly zero yaw**.
- Performance-mode hulls keep lateral resistance: `BoatAlignNormal`
  (`Crest.Examples.dll`, not present in any prior decompile) applies the same
  right-axis drag with `_dragInWaterRight = 2.0` and `_forceHeightOffset = -0.3` on all
  26 shipped instances.
- The cog and the kakam additionally carry `RigidbodyDirectionalDrag` (prefab-wired
  despite having zero code references), `x = y = 0.96`, `zForward = 0.999`,
  `zBack = 0.96`. It rewrites `rb.velocity` in local space, so it damps leeway with no
  moment arm and no yaw.

### The consequence, which is the actual design insight

Unity generates torque about the rigidbody's centre of mass.
`Rigidbody.centerOfMass` is `keel.centerOfMass + zero` (`BoatMass.cs:116`), and the
authored `BoatKeel.centerOfMass` is **purely vertical** on every hull:

| Hull (`sceneIndex`) | `BoatKeel.centerOfMass` | `m_Mass` | `BoatWind.forceOffset` |
|---|---|---|---|
| dhow (10) | (0, -0.75, 0) | 780 | (0,0,-7) |
| sanbuq (20) | (0, -0.75, 0) | 5300 | (0,0,-12) |
| cog (40) | (0, -0.75, 0) | 1200 | (0,0,-8) |
| brig (50) | (0, -1.25, 0) | 11023 | (0,0,-12) |
| jong (70) | (0, -1.75, 0) | 14500 | (0,0,-12) |
| junk (80) | (0, -0.80, 0) | 4700 | (0,0,-12) |
| kakam (90) | (0, -0.75, 0) | 950 | (0,0,-7) |

So the lever arm from the COM to the lateral-drag application point is **purely
vertical**, and the hull's lateral resistance produces **roll and exactly zero yaw**.
It only produces yaw at all because `BoatMass` shifts the live COM in x and z from loose
items (`BoatMass.cs:89`) and the 160 kg aboard player (`:113`). Sail mass does **not**
shift it: `GetSailMass` feeds only the scalar total that becomes `body.mass`
(`BoatMass.cs:105`, `:115`), never the offset accumulator.

**Therefore, in this sim, yaw balance is: the longitudinal offset of each sail's
`windcenter` from the centre of mass, times that sail's side force, plus the hull
windage term at 7 to 12 m aft, opposed only by the rudder.** Working in boat-local axes
(forward `+z`, right `+x`, up `+y`), the side force at a point offset `(x, y, z)` from
the COM contributes a yaw moment of `z * F_side`. That is a single, exact, checkable
number, and it is computed entirely from members that exist.

A CLR dot placed at some fraction of waterline length would look right to a sailor and
would **not** predict this sim. The honest markers are the sail `windcenter` positions,
the hull origin (where lateral drag acts), and `Rigidbody.worldCenterOfMass` (the pivot).

One more consequence worth showing the player: hull drag is `ForceMode.Acceleration`
(mass-independent) while sail force is `ForceMode.Force` (mass-dependent). Loading cargo
therefore leaves resistance-per-mass unchanged while diluting sail authority, so **a
heavy ship makes less leeway and carries less weather helm for the same rig**. It also
means the COM shifts, which is the only thing that gives hull drag any yaw at all.

Physics context, read from `globalgamemanagers`: fixed timestep 0.02222 (**45.005 Hz**,
not the 50 Hz the coop docs assume), `Time.maximumDeltaTime` 0.1 (not Unity's 0.333),
gravity -9.81, 30 solver iterations, enhanced determinism off.

---

## 4. The shipyard is a good place to measure, with three caveats

**Previewed sails are real.** `Shipyard.AddNewSail` does
`sailInstaller.AddNewSail(Object.Instantiate(prefab))` (`Shipyard.cs:321`), and
`ShipyardSailInstaller.AddNewSail` adds the live object to `Mast.sails` (`:76`) and
parents it to the mast (`:77`). It has its full component set. The only differences from
a paid sail are the private `Sail.installed` bool (set true only at `Mast.cs:199`), a
disabled `Sail` component (`:55`), a kinematic rigidbody (`:57`), and a null
`shipRigidbody` - that last one is assigned only in `Mast.AttachSailToMast`
(`Mast.cs:188`). `sailArea` **is** valid on a preview, because `AddNewSail` calls
`SetSailArea()`. Uninstalled previews are forced to `currentUnroll = 1`
(`RopeControllerSailReef.cs:104`).

**The hull is kinematic and lifted out of the water.** `Shipyard.MoveShip` is started
with `setToKinematic: true` (`Shipyard.cs:274`) and ends with
`ship.position = newPos.position` (`:441`). `BoatHorizon.Update` early-returns while any
shipyard is active, so nothing fights the flag. `Time.timeScale` is untouched; the sun,
`PlayerNeeds`, autosave, item physics and wakes are all gated on
`GameState.currentShipyard`. Physics steps **do** still run.

### Caveat 1: the cradle is above sea level, by a measured per-shipyard amount

There are 5 `Shipyard` components; 4 are live (one per island scene) and one in `level24`
is dead on an inactive GameObject with null installers. Measured world Y of
`editedShipPosition`:

| Shipyard | scene | cradle Y | height-multiplier error | 1.4x saturation reached at true height |
|---|---|---|---|---|
| Al'Ankh (Gold Rock) | `level1` | 4.730 | +0.129 | 17.27 m |
| Dragon Cliffs | `level9` | 3.771 | +0.103 | 18.23 m |
| Aestrin (Fort) | `level15` | 6.770 | +0.185 | 15.23 m |
| Lagoon | `level27` | 2.820 | +0.077 | 19.18 m |

The curve's slope is `0.6 / 22 = 0.02727` per metre, and sea level is exactly 0, so a
CE height read in the shipyard is inflated by the cradle Y and the curve saturates early.
**The gizmo must subtract the cradle Y** (equivalently, evaluate the curve at height
relative to sea level 0), and the correction is per-shipyard, not one constant. Cradle Y
is stable at runtime: the floating origin never shifts Y, and `IslandHorizon`'s island
drop is zero within 1000 m of the player.

All four cradle rotations are yaw-only, so no pitch or roll is applied to the hull.
Every shipyard hierarchy is authored at uniform world scale **0.5**, so anything parented
under `editedShipPosition` or a `shipyard ...` transform inherits that scale.

### Caveat 2: the invalidation funnel has a hole

`Shipyard.UpdateOrder(bool skipParts)` (`Shipyard.cs:99`) is reached from every sail add,
move, scale and removal, from every part-option change, and from col-check completion. It
is the obvious hook. **But `Shipyard.CancelOrder` does not call it.** `CancelOrder`
(`Shipyard.cs:87-92`) calls `SaveableBoatCustomization.LoadData(originalData)`, which
`RemoveAllSails()` on every mast and re-`Instantiate`s each sail from
`PrefabsDirectory`, then `GC.Collect()`, then `ResetOrder()` - and `ResetOrder` does not
call `UpdateOrder` either. Since `DischargeShip` calls `CancelOrder` unconditionally as
its first statement (`Shipyard.cs:289`), **every exit from a shipyard, bought or not,
destroys and re-instantiates the entire rig invisibly to an `UpdateOrder`-only hook.**

`Shipyard.UpdateOrder` also destroys sails as a side effect (`Shipyard.cs:128`), so a
cached `Sail` reference must not be held across it either.

There is no C# event, `Action` or delegate anywhere in the shipyard classes. Harmony is
the only mechanism.

### Caveat 3: enumeration and frame traps

- The supported traversal is `boatRoot.GetComponent<BoatRefs>().masts` then each
  `Mast.sails`. `BoatRefs.masts` is allocated `new Mast[31]` and indexed by
  `Mast.orderIndex` (`BoatRefs.cs:15`, `:22`), so it is **sparse - null-check every
  slot**. Masts can be inactive mid-edit and vanilla skips them.
- `Shipyard.GetCurrentBoat()` returns the boat **root**. `GameState.currentBoat` is *not*
  the root - `AdmitShip` sets it to `BoatRefs.boatModel`, a child (`Shipyard.cs:275`).
- `Sail.UpdateInstallPosition` does `SetActive(false)` then `SetActive(true)` on the sail
  GameObject (`Sail.cs:292-293`) on every add and every move. **Never parent a gizmo
  object or start a coroutine on a Sail**; parent to the mast or the boat root.
- Do not poll `SaveableBoatCustomization.GetData()` as a change detector: with SE
  installed, SE postfixes it and rewrites `GameState.modData` plus a log line every call.
  The coop mod hit this and decimated its own poll for that reason.
- An edit's consequences land asynchronously. `RecheckAllSailsCols` yields two
  end-of-frames plus three fixed updates, and each col check sweeps the sail through
  plus or minus 89 degrees in 5-degree steps over many `FixedUpdate`s before calling
  `UpdateOrder`.

---

## 5. Trim: the sail angle is emergent, so the pose must be imposed

This decides whether "call the game's own force functions" survives, and the answer is
"yes, but only if the mod also imposes a pose".

- **No servo.** Of 200 `HingeJoint`s in the shipped data, **zero** have
  `useMotor = true`, and no code anywhere enables a joint spring, motor or limit. The 43
  with `useSpring = true` are all rudders, booms or damage-water objects, never a sail
  prefab.
- The rope controllers only write `JointLimits` - a *window* of allowed angles, not an
  angle. Seven call sites in five classes, all verified. The commanded quantity is
  `RopeController.currentLength` (0 to 1); the achieved angle is reported by
  `RopeControllerSailAngle.currentAngle`, `SquareAngleMaster.GetCurrentAngle()`,
  `JibAngleMaster.GetCurrentAngle()` (unsigned) and by `HingeJoint.angle`.
- The only thing that moves a sail is `ApplyRotationFromWind` pushing the sail's
  rigidbody until it rests against a limit, plus the player shoving a boom by hand
  (`GPButtonSailPusher`).
- **In the shipyard nothing can trim.** `Sail.cs:351` forces
  `sailRigidbody.isKinematic = GameState.currentShipyard != null`, so installed sails sit
  frozen at whatever stale at-sea angle they carried in, and preview sails are kinematic
  with `Sail` disabled. Reading the live pose in the shipyard measures an arbitrary pose.
- The trim angle is the Sail root's **local Z euler** relative to its parent mast, with 0
  fore-and-aft. SE's unfurl button independently confirms the convention by zeroing
  exactly that component.

The window mechanic, measured: all 47 fore-and-aft sheet ropes ship at
`currentLength = 0.0`, and `min = Lerp(-pad, sail.minAngle, L)`,
`max = Lerp(+pad, sail.maxAngle, L)` with `pad` of 3.0 (36 instances) or 1.0 (11). So
`Sail.minAngle`/`maxAngle` is the **fully eased mechanical stop**, not a "sheeted"
position, and at `L = 0` a sail is pinned within a degree or three of dead centre. The
honest move is to expose the sheet fraction `L` as a gizmo input and name the assumed
pose explicitly, rather than calling one limit "trimmed". A torque-free weathervane
equilibrium also exists inside the window on downwind headings.

Sail-on-sail blanketing is recomputable but should be ignored: the shadow collider mesh
has exactly 9 vertices on all 96 prefabs, so a synchronous recompute for a hypothetical
wind is 9 raycasts per sail on layer 24. Vanilla's cached value is useless in the
shipyard (the coroutine is gated on `currentUnroll > 0` and raycasts along the real
wind). Pass 1.0 and document it.

---

## 6. Rendering surface

- **There is no shipyard camera.** `Shipyard.AdmitShip` calls
  `BoatCamera.instance.SwitchOn()` (`Shipyard.cs:276`), which reparents the single player
  camera into orbit mode. `Camera.main` is the `Camera` on `CenterEyeAnchor`, inside the
  OVR rig. It must be read, never repositioned.
- **Culling masks, measured** (this was previously only a hedged code comment):
  `Camera.main`'s `m_CullingMask` is `0x009E7E17`, which **includes layer 0 (Default) and
  layer 23 (WorldUI)** and excludes layer 5 (UI). The GameObject literally named
  `UI camera` is a child of the main camera with mask `32`, i.e. **layer 5 only**, and
  `ClearFlags = Depth`. Both serialize `m_Depth = 0.0`, so their relative order is not
  determined by asset data.
- All 31 `ShipyardUI.mastButtons` are on **layer 23 WorldUI**, so they render on the main
  camera. They are scene instances, not a prefab.
- **`ShipyardUI.mastButtonAlwaysOnTopMaterial` is confirmed a ZTest-Always material**:
  it is `ui background alwaysOnTop`, whose shader is Unity's built-in `GUI/Text Shader`
  with `zTest = 8` (Always), `zWrite = 0`, `culling = 0`, `SrcAlpha/OneMinusSrcAlpha`,
  queue Transparent. Caveat: its `_MainTex` is the `ButtonSpacebarSprite` texture and its
  `_Color` is a tan tint, so an unmodified copy shows that sprite.
- **The game has essentially no uGUI Canvas** and ships no TextMeshPro. All vanilla UI,
  the entire shipyard menu included, is world-space `TextMesh` plus mesh quads layered by
  material `renderQueue` (`NotificationUi` uses 3002, `MissionDetailsUI` 3001).
- `ShipyardUI.OverrideDescriptionText(string)` is public, and `UpdateDescriptionText`
  already prints the selected sail's rounded `sailArea`. Vanilla also already spawns a
  runtime world-space mesh overlay in the shipyard (the sail obstruction outline), so a
  world-space gizmo is idiomatic here.
- Strongest precedents to copy, all in sailwind-coop: `DebugOverlay.DrawItemLabels`
  already does world-anchored IMGUI labels via `Camera.main.WorldToScreenPoint` with a
  behind-camera cull and a GUI-space Y flip (`DebugOverlay.cs:578-630`) - no layer,
  material, shader or canvas needed; `TradeFeedUI` builds its own
  `ScreenSpaceOverlay` canvas at `sortingOrder 5000` behind a try/catch that permanently
  degrades to notifications on failure; `RemotePlayerManager` builds a world marker with
  `CreatePrimitive`, `new Material(Shader.Find("Standard"))` and `layer = 0`.
- `Shader.Find` strings with real working evidence in mod source: only `"Standard"` and
  `"UI/Default"`.
- **No installed mod draws a 3D overlay of any kind**, so there is no precedent for the
  marker itself, only for the number.
- Minor oddity worth one look in game: the mast buttons' `LineRenderer`s have
  `useWorldSpace = true` with authored positions `(0,0,0)` to `(15,15,15)` and nothing in
  the assembly ever writes them. Either they are invisible for a reason not visible in
  the data, or it is a long-standing visual bug. Do not rely on that channel.
- VR is not a constraint: `GameState.VREnabled` is declared once, read twice, and
  **assigned nowhere**.

---

## 7. Shipyard Expansion

`GUID com.nandbrew.shipyardexpansion`. **Installed here: 0.9.0.** The public source
clone is 0.9.90. The installed build was decompiled and diffed method by method: every
sail-relevant piece is semantically identical (`SailScaler` in full, `SailAreaPatch`,
`SailScalePatch`, `Util.CopySail`, `SailLimits`, `SailAdder`, `TopsailPatch`,
`ShipyardSailInstallerPatches`). One suspicion was refuted: the `LoadSailConfig` version
gate is byte-identical and evaluates the same way in both builds. The only differences
are in the copper-cladding feature, which is inert on 0.9.0.

Detect it at runtime with
`Chainloader.PluginInfos["com.nandbrew.shipyardexpansion"].Metadata.Version`, which needs
only a BepInEx reference and is already proven working on this machine.

**How SE stores dimensions.** There are no width or height fields. The live state is
`SailScaler.scaleablePart.localScale`, and `scaleablePart` is
`GetComponentInChildren<Animator>().transform` (`SE/Scripts/SailScaler.cs:36`) - measured
to be exactly `cloth.transform.parent` on all 96 prefabs. Axis mapping is by `ScaleType`:
Square gives `(width, height, width)`, Jib `(width, height, height)`, Uniform
`(height, height, height)` with width discarded. Limits are 0.2 to 3.0 per axis, step
0.05, angle step 1 degree. `stretchableSquares` is
`{4,32,48,49,50,51,52,59,80,113,114,115,116}`, `stretchableJibs` is `{110,111}`,
`flippableSquares` is **empty** so only staysails are flippable.

Spar rotation is `SailScaler.Angle` (public get, private set), on `rotatablePart` which is
the sail root for lateen, `scaleablePart` for `SailCategory.other`, and null otherwise.
**Spar height is pure vanilla**: `Sail.currentInstallHeight` via `ChangeInstallHeight` and
`UpdateInstallPosition`, persisted as `SaveSailData.installHeight`.

**Polling is avoidable.** `SailScaler.SetScaleAbs(float width, float height)`
(`SE/Scripts/SailScaler.cs:167`) is the single funnel for every SE dimension change; all
the increase/decrease/scale methods and the `Sail.LoadScale` postfix route through it.
Better still, the one hook that catches **both** vanilla and SE resizes is the vanilla
public `Sail.SetSailArea()`: SE calls it at the end of `SetScaleAbs`
(`SE/Scripts/SailScaler.cs:190`) and vanilla calls it in `ChangeScale` and `Start`.

**SE patches nothing in the force path** - not `ApplyForce`, `UpdateWindForceOnSail`,
`GetWindHeightMult`, `GetRealSailPower` or `ApplyRotationFromWind`.

### The question you asked me to answer explicitly

> When SE resizes a sail, does vanilla's CE and its 80-to-140% power multiplier follow the
> new geometry?

**For all 96 stock sails: yes, both, correctly.** `windcenter` is inside the transform SE
scales (section 1), so the CE moves affinely with the cloth and
`GetWindHeightMult(windcenter.position.y)` reads the new height. And **sail area is kept
in sync too, more accurately than vanilla**: SE installs a Harmony **Prefix on
`Sail.GetSailArea` that returns false**, replacing vanilla's formula entirely
(`SE/Patches/SailAreaPatch.cs`), and `SetScaleAbs` calls `sail.SetSailArea()` on every
change, so the cached `sailArea` that `GetRealSailPower` and `ApplyForce` consume is never
stale. SE's version sums true world-space triangle areas via `TransformVector`; vanilla
multiplied the raw mesh area by the **square of an average** of the parent's scale
components, which is geometrically wrong under anisotropic scaling. The two agree exactly
when the parent chain is unit-scaled, so SE is a strict improvement, not a divergence.

**But there is one real defect, and it is SE's, in SE's own added sails.**
`Util.CopySail` - the routine that builds SE's three lug sails - reparents
`sail.windcenter` to the **sail root** (`SE/Scripts/Util.cs:348`, and identically in the
installed 0.9.0) and only afterwards writes `localScale` to the Animator transform
(`:366`). Unity's `Transform.parent` setter preserves world pose, so for those sails
`windcenter` ends up **outside** the scaled subtree, frozen. Consequences:

- prefabIndex **156** (`balanced lug low`, from stock 5), **157** (`balanced lug full`,
  from 15) and **158** (`balanced lug wide`, from 30) have a CE that does not follow any
  later resize. Simulating representative resizes gives **1.2 to 8.9 m of moment-arm
  error** (measured: 2.38 m on a comparable stock square at +1.0/+1.0, 4.77 m on prefab
  115, 8.92 m on prefab 110).
- Those three sails **are** resizable: SE adds a `SailScaler` to every non-null entry of
  `PrefabsDirectory.sails`, has explicit `angleLimits` entries for 156/157/158, and they
  fall through to `ScaleType.Uniform`. So the defect is reachable in normal play.
- Because `SailAdder` re-categorises them to `SailCategory.other`, `SailScaler` sets
  `rotatablePart = scaleablePart`, so the spar-rotation control also desyncs the CE's
  **orientation**, which corrupts `GetWindAngle` (`Sail.cs:497-507`) as well as the arm.
- CopySail's own scaling is not the problem: all three source prefabs are unit-scaled and
  all three calls pass `scale = 1f`, so that write is a no-op. The reachable divergence is
  the later `SailScaler` resize only.

On SE's side this looks like a two-line fix: move `sail.windcenter.parent = sailBase;`
to after the `localScale` write, or delete both reparent lines, since the first one is
already a no-op for stock prefabs (`windcenter` is *already* a child of that Animator
transform). Per your instruction I have not fixed or worked around it. It is worth
leading with when you talk to NANDbrew.

**Do not use `windcenter.parent != cloth.transform.parent` as the detector.** It
false-positives on 43 of the 57 `Sail` instances authored directly in `level24`, where the
`Sail` component sits on a child and `cloth.transform.parent` is a rig group. The safe
check is "`windcenter` is not a descendant of
`GetComponentInChildren<Animator>().transform`".

**SE's live config on this machine:** `Override scaling` true, `Combined scaling` false,
`Add lug sails` true, `Link topmasts` true, `Unfurl sails in shipyard` true, `Auto-fit
sails` true, `Vertical lateens` true, `Vertical fins` true, `skip sail data` false.

---

## 8. Prior art and conflicts among the 16 installed mods

All 19 plugin and bridge assemblies were decompiled and their patch surfaces enumerated.

**`SailInfo` 1.2.1 (`pr0skynesis.sailinfo`) is direct prior art, not a conflict.** It
patches **no member of `Sail` at all**. Instead it caches `AccessTools.Field` handles for
the private `unamplifiedForwardForce`, `unamplifiedSidewayForce` and `totalWindForce` and
reads them per frame to show per-sail forward and side force percentages, writing a Unity
rich-text string into the vanilla `GoPointerButton.description`. That is a working,
shipped proof that the reflection approach in section 2 is viable. Note it declares zero
`[HarmonyPatch]` attributes and patches imperatively via `Harmony.Patch`, so an
attribute-only conflict scan misses it entirely. Its live config has `sailEfficiency`
on and the per-sail force readouts off.

**`NANDTweaks` 1.7.4 already injects rig numbers into the shipyard UI**, by postfixing
`ShipyardUI.UpdateDescriptionText` and adding a weight computed from
`sail.GetRealSailPower() * 20f`. `ShipyardUI` is the most contended type in the install:
5 NANDTweaks patches plus 4 SE patches, and NANDTweaks' live-enabled "Wide UI" relocates
16 shipyard UI elements to hardcoded positions. Anything the gizmo adds to that panel
needs to expect company.

**`InstrumentDisplay` 3.0.2 is precedent for a physics-derived balance readout**: it
reflects three private `BoatMass` fields, reads `worldCenterOfMass.y`, and computes a
metacentric-height estimate, drawn purely in `OnGUI` with zero Harmony patches.

**The force and hull surface is clear.** Nothing installed patches `Sail.ApplyForce`,
`Sail.UpdateWindForceOnSail`, `Sail.GetRealSailPower`, `Crest.BoatProbes`, `HullDrag` or
`BoatWind`, and nothing writes `sailArea`, `upwindEfficiency`, `_dragInWaterRight` or
`_forceHeightOffset`. Two exceptions to know about: SE_Bridge's `SE_Keel` writes
`BoatKeel.centerOfMass` on enable, and `SE_Outrigger` zeroes
`BoatProbes._forcePoints[i]._weight` on disable.

**Sailwind Coop v0.2.37 is deployed on this machine** (contrary to a standing note that
it was not). It postfixes `BoatMass.UpdateMass` and adds crew weight to
`Rigidbody.centerOfMass`, so in a co-op session the COM the gizmo draws against includes
that contribution.

`Sail Collision Fix` 1.1.3 forces `IsCollidingWithSail` and `IsObstructed` to false and
resets the col-check angles, which changes `Sail.minAngle`/`maxAngle` and therefore what
any trim-dependent number means on that install.

---

## 9. Proposed architectures

All three share the same data layer, which the findings above fix in place: enumerate
`BoatRefs.masts` (sparse, null-checked) then `Mast.sails`; read `windcenter.position` per
sail with no scale compensation; read `Rigidbody.centerOfMass` live rather than any
asset field; subtract the per-shipyard cradle Y before evaluating the height curve;
invalidate on `Shipyard.UpdateOrder` **plus** `SaveableBoatCustomization.LoadData` and
`Mast.AttachSailToMast`/`DetachSailFromMast`, **plus** `SailScaler.SetScaleAbs` when SE is
present.

They differ in how the per-sail force is obtained.

### Architecture A - Invoke the game's own function with an imposed pose (recommended)

For each sail: save state, set `apparentWind` to the synthetic vector, set
`currentUnroll = 1`, point `shipRigidbody` at the boat's rigidbody if it is a preview,
rotate the Sail root's local Z to the assumed trim, reflect-invoke
`UpdateWindForceOnSail()`, read the two private force components, restore everything in a
`finally`. Then apply `ApplyForce`'s own scaling (`GetRealSailPower()` times the category
multiplier times 50, side times 1.5) and sum the yaw moments about
`worldCenterOfMass`.

- **For:** the numbers are the sim's numbers by construction, including the pieces a
  reimplementation would get wrong (the 13/16-degree luff cutoff, the 0.44 skew inside
  `GetWindAngle`, the `-0.33` reverse-thrust factor, the `debugYardMult` floor, the 0.02
  `debugSailAreaMult`). It automatically inherits SE's replaced `GetSailArea`. It is a
  shipped, proven pattern: `SailInfo` already reads exactly these two private fields.
- **Against:** 13 fields to save and restore; breaks if the game renames two private
  fields (fail soft with a logged warning and fall back to B); the block must be strictly
  synchronous and must not call back into anything that starts a col check or repositions
  a sail; needs one guarded reflection call per sail per refresh.
- **Risk:** low, and bounded. The perturbation is invisible because Unity's loop is
  single-threaded, and on installed sails `Sail.Update` overwrites the fields from real
  wind on the next frame anyway. On preview sails `Sail.enabled` is false, so the restore
  is mandatory rather than belt-and-braces.

### Architecture B - Replay the arithmetic in mod code

Reimplement `UpdateWindForceOnSail` plus `ApplyForce`'s scaling (about 40 lines) with an
explicit trim.

- **For:** no reflection, no state mutation, no restore, trivially testable, cannot
  perturb the running game, and works identically on a preview sail with a null
  `shipRigidbody`.
- **Against:** it can never be *proven* to match, and it silently rots when the game dev
  retunes a constant. It also has to decide whether to use vanilla's area formula or
  SE's, which means the displayed number stops being comparable across mod sets.
- **Verdict:** the right shape for a **fallback** and for the Phase 3 step 2 cross-check
  harness, not for the primary path.

### Architecture C - Live telemetry only, no synthetic wind

Drop the what-if. Show CE, COM and the net yaw moment for the **actual** current wind
while sailing, and in the shipyard show only geometry (CE positions, longitudinal offsets
from the COM, total area).

- **For:** the smallest possible patch surface and no pose assumption at all, so nothing
  in it can be wrong.
- **Against:** it does not answer the question the mod exists for. A sail plan must be
  judged before it is paid for, and in the shipyard there is no meaningful wind and no
  trim, so a purely live readout is blank exactly when the player needs it.

### Recommendation

**A, with B implemented behind it as a fallback and as the verification harness.** The
signed number is the deliverable, and A is the only route that can honestly claim it is
the sim's own number; B exists so that a renamed private field degrades to a logged
warning and a slightly less trustworthy number rather than a dead gizmo. C is the
degraded mode if the pose assumption turns out to be indefensible in play.

The primary output should be the net yaw moment about `worldCenterOfMass` at a synthetic
close-hauled and beam-reach apparent wind, summing sails plus the `BoatWind` windage term
at its measured 7-to-12 m aft offset, with the sheet fraction `L` exposed as an input and
the assumed pose stated on screen. Markers: CE per sail sized by that sail's contribution,
the summed CE, `worldCenterOfMass`, and the hull origin labelled as where lateral
resistance actually acts. **No CLR dot**, because there is nothing to put one on.

---

## 10. What changes when SE is absent versus present

| | SE absent | SE present (0.9.0 as installed) |
|---|---|---|
| CE follows a resize | yes, vanilla `ChangeScale` only, clamped to a narrow square band | yes for all 96 stock sails; **no** for SE's own 156/157/158 |
| `sailArea` meaning | vanilla: raw mesh area times the square of an averaged scale | SE's true world-space triangle sum. Same number at unit scale, different under anisotropic scale |
| Reachable proportions | square z clamped to `[y-0.2, y+0.6]`, all axes 0.2 to 3.0 | per-axis 0.2 to 3.0 only, so far wider aspect ratios |
| Width and height separately | no | yes, for `stretchableSquares` and `stretchableJibs` |
| Resize hook | `Sail.ChangeScale`, and `Sail.SetSailArea()` catches it | add `SailScaler.SetScaleAbs`; `Sail.SetSailArea()` still catches both |
| Spar rotation | none | `SailScaler.SetAngle`, lateen and `other` only |
| Jib flip | none | `SailScaler.FlipJib`, staysails only, and it **bypasses `UpdateOrder`** |
| Sails on stays | vanilla `staysail` category on stay masts | SE raises per-hull stay capacity and adds winches |
| Extra invalidation needed | none | `SetScaleAbs`, `SetAngle`, `FlipJib` |

The gizmo must be a soft dependency: `BepInDependency` with
`DependencyFlags.SoftDependency` for load order, detection through the chainloader plugin
list at runtime, no hard type reference on any path that runs without SE, and a logged
warning plus degradation to vanilla-only behaviour on an SE version that has not been
inspected. That is also the convention the author's own `SECompat`/`CompatRegistry`
already establishes.

---

## 11. What could not be determined, and what would settle it

1. **Whether `windcenter` sits at the cloth's true area centroid.** Measured and they do
   not coincide (4.6 m, 6.5 m and 12.0 m apart on three sails checked), but the
   comparison is confounded by the skinned mesh's non-identity root bone pose, by
   `Cloth` simulating in world space, and by whatever reef state the Animator was saved
   in. It does not block anything: the game uses `windcenter`, so the gizmo should too.
   **Settle with:** an in-game log of the cloth's world bounds centre against
   `windcenter.position` on one sail.
2. **Whether the Animator on the scaled transform animates that transform's own
   position or scale at runtime.** The controller exists and is enabled; its clip curve
   bindings were not opened. If a clip drove that transform it would fight
   `ChangeScale`'s writes and move the CE. **Settle with:** log
   `cloth.transform.parent.localScale` and `localPosition` across a reef animation.
3. **The runtime hierarchy of SE's added sails as actually instantiated.** The frozen-CE
   conclusion is read from `CopySail` plus Unity's documented parent-setter semantics,
   not observed live. **Settle with:** in the shipyard, install a `balanced lug` sail and
   log `windcenter.parent.name` and `windcenter.position` before and after one resize.
4. **What `currentUnroll` an installed sail actually carries on shipyard entry.** The
   reef rope's serialized `currentLength` is 0.0 in all 99 instances, which drives it to
   0 on the first frame, but mid-session entry depends on winch history. The gizmo sets
   it explicitly for the what-if, so this is cosmetic. **Settle with:** one log on
   `AdmitShip`.
5. **Whether the mast buttons' world-space `LineRenderer`s actually draw** the authored
   `(0,0,0)` to `(15,15,15)` line. **Settle with:** one look in game.
6. **Relative render order of the main camera and the `UI camera`**, since both serialize
   depth 0.0. Only matters if the gizmo must composite over vanilla UI. **Settle with:**
   log both cameras' `depth` at runtime.
7. **Whether the jong's serialized `m_IsKinematic = true` survives into play.** It is the
   only hull authored kinematic, and several systems manage that flag at runtime.
   **Settle with:** log `isKinematic` on the jong while sailing.
8. **Whether `Rigidbody.centerOfMass` is live for the boat on the cradle.**
   `BoatMass.UpdateMass` is gated on `GameState.lastBoat == transform`, and `AdmitShip`
   never assigns `lastBoat`; the read of `PlayerEmbarkDisembarkTrigger` says it stays set
   to the boat you embarked, and disembark is suppressed in the yard, so it should be
   live for the boat you sailed in. Not confirmed live. **Settle with:** log
   `GameState.lastBoat` against `Shipyard.GetCurrentBoat()` on `AdmitShip`. This is the
   one open item that changes a number, because if it is stale the gizmo must compute the
   COM itself from `BoatKeel.centerOfMass` plus `BoatMass`'s leverage terms.

Every item on this list is answerable by the Phase 3 step 2 live readout, which is why
that step is worth doing before any UI exists.

---

## 12. Live confirmation, and one correction to section 3

Five snapshots taken while sailing a Shipyard Expansion rigged brig (`BOAT medi medium (50)`,
sceneIndex 50) in winds from 5 to 31 m/s, on both tacks.

**Confirmed against the live game**, every value matching the asset reads exactly:
`_forceHeightOffset` 0.000, `_dragInWaterRight` 3.000, `BoatWind.forceOffset` (0,0,-12),
`BoatKeel.centerOfMass` (0,-1.25,0), `BoatProbes` enabled with 12 force points. Every one
of the seven sails reported its centre of effort **inside** the scaled part, including
sails SE had scaled to 265%, which confirms the prefab measurement on live objects.
Height multipliers ran 1.005 to 1.400 and tracked sail height. Furled sails reported
`appliedWindForce` near 0.15 rather than 0, which is the `debugYardMult` floor behaving
exactly as predicted.

**Open item 8 is answered: `Rigidbody.centerOfMass` is live.** It sat at
`(-0.21, -0.61, -1.27)` against an authored keel value of `(0, -1.25, 0)`, so `BoatMass`
is running and cargo plus the aboard player had shifted it 1.27 m aft and 0.21 m to port.

**The sign convention is right.** Raw yaw flipped sign with the tack (+38,475 Nm on port,
-25,620 Nm on starboard) while the signed helm stayed negative on all five samples. A
headsail-heavy rig reading consistent lee helm is the correct answer, and it is the answer
the sim's own numbers give.

### The correction

Section 3 says hull lateral resistance produces roll and zero yaw at the authored COM, and
gains yaw only from the COM shift. That is true, but the conclusion drawn from it
understated the term. The drag coefficient is applied with `ForceMode.Acceleration`, so the
equivalent force is mass times the coefficient: on a 24.8 tonne loaded brig that is roughly
74 kN per m/s of leeway. Against a measured 1.27 m arm, the hull's yaw contribution is the
same order as the entire sail plan's. **It is a first-class term and the readout must
include it.** It was missing from the step 2 totals and has been added.

There is a more useful way to say the same thing. In lateral-force equilibrium, which is
what a "will this rig balance" question assumes, hull drag exactly opposes the sails' side
force. The two side-force terms then combine into one lever taken about the point the drag
acts at, so **the effective pivot for the side-force part of the balance is the hull
origin**, not the centre of mass. That is the classical centre-of-lateral-resistance
picture, recovered from the sim rather than assumed: the CLR is real, it is the hull
origin, and it is identical on every hull because `_forceHeightOffset` is zero everywhere.
It is still not a keel, not hull-shape derived, and not something a fraction of waterline
length would predict.

Two practical consequences. The shipyard number can be computed **without knowing the
leeway**, by taking sail side-force moments about the hull origin, which is exactly what a
pre-purchase readout needs. And cargo placement is a genuine helm control in this sim,
because moving the COM fore or aft changes the hull term directly; that is worth surfacing
rather than hiding. The forward-force part of the yaw is separate and keeps its arm from
the COM, since it depends on each sail's athwartships offset.

The snapshot now prints both forms of the sum so the equivalence is checked rather than
asserted.


## 13. The centre of mass, measured, and a correction to what I said about it

This section exists because I stated something confidently and it was wrong. The claim was:
a boat's centre of mass is deaf to the shipyard, so a centre-of-mass marker cannot tell a
player anything about a choice they are about to pay for. Most of that is right. The blanket
part of it is not.

### What actually maintains it

`BoatMass.cs:116` is the only thing that maintains a boat's centre of mass:

    body.centerOfMass = keel.centerOfMass + zero;

`keel.centerOfMass` is a per-hull authored point (`BoatKeel.cs:7`, pushed to the rigidbody once
in `Start` at `BoatKeel.cs:16`). `zero` starts empty at `BoatMass.cs:69` and is only ever fed by
loose cargo (`BoatMass.cs:71-91`) and by the player's own 160 kg standing somewhere
(`BoatMass.cs:109-114`).

The parts and sails block at `BoatMass.cs:92-108` adds a mast's mass at line 97 and each sail's
at line 105, both into `num`, which becomes `body.mass`. Neither ever touches `zero`. So sail
plan and deck parts change what the boat weighs and never where it balances. That part of the
original claim holds and is worth stating plainly, because it is not what a sailor would expect:
**a towering rig does not make a boat tender in this sim.** It makes her heavy and slow.

### The exception I missed

`BoatCustomKeel.cs:11` writes `keel.centerOfMass = overrideCenterOfMass` in `OnEnable`. A part
option is a GameObject that `BoatPart.SetOptionEnabled` (`BoatPart.cs:21`) activates and
deactivates, so any option carrying that component moves the centre of mass when selected.

I asserted no shipped option carries one. Measured instead: a sweep of all 89 serialized files
under `Sailwind_Data` (14,125 MonoBehaviours, 14,124 script names resolved, no file failed) finds
exactly **two**, both on the same hull.

| option GameObject | overrideCenterOfMass | hull |
| --- | --- | --- |
| `deep_keel` | `(0, -1.55, 0)` | BOAT junk medium (80) |
| `(no deep keel)` | `(0, -0.80, 0)` | BOAT junk medium (80) |

That is the junk medium's deep-keel option pair, and choosing the deep keel drops the centre of
mass 0.75 m. It is the only place in vanilla where a shipyard choice moves it.

The parse is not assumed. A MonoBehaviour begins m_GameObject (PPtr, 12) m_Enabled (int32, 4)
m_Script (PPtr, 12) m_Name (aligned string), so `BoatKeel` must total 44 bytes and
`BoatCustomKeel` 56. Every instance consumed its buffer exactly, and the hull baselines came out
as plausible metres on the centreline: junk medium -0.80, junk large -1.75, junk small -2.00,
medi medium -1.25, medi small and the dhows -0.75.

### The three parts that prompted the question

Matched by part list rather than by guessing at internal names. The brig is
`BOAT medi medium (50)`, the hull carrying `option_forecastle` and `(no forecastle)`.

- Forecastle on the brig: no `BoatCustomKeel`. Mass only.
- Cabins (`cabin_full`, `cabin_semi`, `cabin_standard`, `cabin_cloth_only` on the dhow large;
  `part_cabin_wood`, `part_cabin_cloth` on the dhow medium; `struct_cabin_*` on the junk medium):
  none carry one. Mass only.
- Castle on the cog: no option named "castle" exists on any hull, and neither `medi medium (51)`
  nor `(52)` carries a `BoatCustomKeel`.

### With mods installed it is a different picture

`SE_Bridge.SE_Keel` does exactly what `BoatCustomKeel` does and restores on the way out:

    public void OnEnable()  { boatKeel.centerOfMass = localCoM; }   // SE_Keel.cs:23
    public void OnDisable() { boatKeel.centerOfMass = startCoM; }   // SE_Keel.cs:27

Shipyard Expansion's bundle ships four, all parsing to exactly the expected 44 bytes:

| option GameObject | localCoM | SE part set |
| --- | --- | --- |
| `keel_deep` | `(0, -1.75, 0)` | SE_parts_kakam |
| `keel_deep` | `(0, -1.75, 0)` | SE_parts_dhow |
| `keel_deep` | `(0, -1.75, 0)` | SE_parts_cog |
| `cladding` | `(0, -0.25, 0)` | SE_parts_sanbuq |

Drydock does the same through `KeelWeight.cs:24`. So with the mods this author actually runs,
four more hulls gain a moving centre of mass, the cog among them.

### What this changed in the mod

The marker stays, and it is read from `BoatKeel.centerOfMass` rather than from the rigidbody.

That distinction is load-bearing. Every keel option writes `BoatKeel.centerOfMass`, never the
rigidbody. The only thing that copies that field across is `BoatMass.UpdateMass`, whose caller is
gated at `BoatMass.cs:25` on `GameState.playing` and `GameState.lastBoat == this transform`.
`GameState.lastBoat` is set on embark (`PlayerEmbarkTriggerNew.cs:77`) and never cleared, so the
gate is open only for the boat sailed in on. Judging a boat not yet bought, which is the whole
premise of this mod, the rigidbody value sits frozen at whatever `BoatKeel.Start` left there
whichever keel is selected. Reading the source field is live in every case.

Refreshing is confirmed too: `ShipyardPartsInstaller.cs:53` calls `Shipyard.UpdateOrder()`, which
reaches `partsInstaller.UpdateOrder()` at `Shipyard.cs:158` and then
`RefreshPartsWithOrder` at `ShipyardPartsInstaller.cs:62`, which activates the option and fires
`OnEnable`. The mod's postfix on `UpdateOrder(bool)` runs after all of that.
