# Shipyard Expansion test matrix

Shipyard Expansion support has never been exercised end to end. This is the script for doing it,
and it is written so the result is read out of the log rather than described from memory.

Every step that should refresh the gizmo produces one line:

```
[Gizmo] rebuild: <trigger name>
```

A missing line means that hook did not fire. `unknown` means something rebuilt that no hook claimed,
which is also worth knowing. Because the rebuild is debounced, a drag collapses to a single line
listing every distinct trigger since the last rebuild, so a line naming two triggers is normal.

## Before you start

Enabled plugins must include `ShipyardExpansion`. Confirm at the top of the log:

```
[Compat] ... Shipyard Expansion ... installed
[Compat] 2 Shipyard Expansion sail-edit hook(s) applied.
[Init] 9 shipyard hook(s) applied; rig model available=True
```

`2 sail-edit hook(s)` is the important one. Anything less and the two SE controls that have no
vanilla funnel are dead, which is the specific failure this file exists to catch.

## The steps

Do them in order, in one shipyard visit. There is no need to buy anything: the gizmo answers before
you confirm an order, which is the whole point of it.

| # | Do this | Expected trigger | What it proves |
|---|---------|------------------|----------------|
| 1 | Walk into a shipyard | `AdmitShip`, then `boat settled` | Session start, and the settle watch |
| 2 | Add a sail to a bare mast | `AttachSailToMast` | Vanilla rig change |
| 3 | Remove a sail | `DetachSailFromMast` | Vanilla rig change |
| 4 | Resize a sail with SE's scaler | `SetSailArea` | SE's `SetScaleAbs` reaching the vanilla funnel |
| 5 | **Flip a jib** (SE control) | `SE FlipJib` | **No vanilla funnel. The marker must move.** |
| 6 | **Rotate a spar** (SE control) | `SE SetAngle` | **No vanilla funnel.** |
| 7 | Slide a sail up or down its mast | `ChangeInstallHeight` | Centre of effort moves, nothing else does |
| 8 | Change a keel or ballast option | `UpdateOrder(bool)` or `UpdateOrder()` | The yellow marker should move |
| 9 | Furl a sail with the gizmo's own button | `furl button` | The mod's one write to the game |
| 10 | Cancel the order | `LoadData` | Every sail is destroyed and re-instantiated |
| 11 | Leave the shipyard | `DischargeShip` | Teardown before the sails die |

Steps 5 and 6 are the ones that matter most. Both rewrite geometry the centre of effort is computed
from, and neither reaches `Shipyard.UpdateOrder`, so before `SailScalerHooks` existed the markers
kept showing the pre-flip answer indefinitely and looked perfectly live while doing it.

## Also worth capturing

- **An SE lug sail, if one is available.** SE's three added lug sails keep their centre of effort
  outside the part that gets scaled, so it does not move when the sail is resized. Expected: the
  magenta marker turns red and the log carries a warning naming the sail. This is a known SE
  limitation the mod only reports; it does not modify SE.
- **A sail on a stay.**
- **Anything that looks wrong.** A marker in the wrong place is worth a screenshot even if every
  trigger line is present, because the hooks firing only proves the gizmo recomputed, not that the
  answer it computed is right.

## Reading the result

Alongside the trigger lines, every rebuild also logs:

```
[Ring] sweep of 36 bearings in ... ms, ...
[Ring] mirror audit: 18 pairs, symmetric.
```

The audit line is a free correctness check on every step. `18 pairs, symmetric` is the healthy
reading. If an SE edit makes it report asymmetric pairs, that edit has produced a rig the model reads
differently on the two tacks, and the per-sail detail printed underneath names the sail. One known
exception: a sweep that straddles a rig change reports most pairs asymmetric, because the sail set
itself changed mid-sweep. It is transient and the rebuild that the change triggers supersedes it.
