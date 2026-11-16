# Troubleshooting

Everything below starts with `BepInEx/LogOutput.log`. The mod logs a line for every
decision it makes, tagged `[Init]`, `[Env]`, `[Gizmo]`, `[RigModel]` or `[Compat]`.

## No markers at all

Check the startup lines first:

```
[Init] 9 shipyard hook(s) applied; rig model available=True
```

**If a hook failed**, the log names it. A failed hook costs one refresh trigger, not the
whole gizmo, so the markers may still appear but not update on some edits.

**If `rig model available=False`**, the fields the force model drives did not resolve
against your game version. The gizmo disables itself rather than guess. This means a game
update moved something.

**If neither line appears**, the plugin did not load. Confirm the DLL is at
`BepInEx/plugins/SailwindRigBalance/SailwindRigBalance.dll` and that BepInEx itself is
producing a log at all.

## Markers appear, but hidden inside the boat

Two of the three markers sit inside the hull by nature. They are normally drawn on top of
it by an overlay camera. If that is off you will only see the COE.

Look for either of these:

```
[Gizmo] DrawThroughHull is off; markers inside the hull will be hidden by it.
[Gizmo] every layer on this build is named, so no private layer was free.
```

The first means the setting is off; set `DrawThroughHull = true` in the config. The second
means another mod has claimed every spare Unity layer, and there is nothing the mod can do
except fall back to drawing them normally. The helm bar's arrow sits at the top of the hull, so
it stays visible either way.

## Markers do not update when I change the rig

Rebuilds are deliberately delayed a fraction of a second, because applying a customization
destroys and recreates every sail and the mod has to let that settle before measuring.

Each rebuild logs a line:

```
[Gizmo] 7 sail(s); wind 45 deg off the bow; helm lever -1.42 m (lee helm, band 0.59 m, hull 29.7 m)
```

If that line appears when you change something, the gizmo is working and the markers are
where the model puts them. If it never appears, one of the shipyard hooks did not apply;
see the first section.

Forces in the log are the game's own newtons. The kN labels on the sail force arrows are scaled
to real-world force, so a label reads 6.125 times the matching figure in the log.

## The COE marker is red

One of the sails aboard has a center of effort that does not track its own size, so the
combined figure is not trustworthy. In practice this is one of Shipyard Expansion's three
added lug sails. See [Mod Compatibility](Mod-Compatibility). The mod flags it rather than
correcting it.

## Markers are the wrong size

`MarkerScale` in the config. They scale with camera distance to hold a roughly constant
size on screen, and this multiplies that.

## The button strip is in the way

It is fixed in the top right corner. To remove it while keeping the markers, set
`ShowPanel = false`, which also removes the furl button and turns off the furl key.

## Everything reads as balanced

Check `WindAngleDegrees`. Near a run there is almost no side force to balance, so every rig
looks neutral. The default of 45 is close hauled, which is where balance actually bites.

## Reporting a problem

The useful things to include:

- the `[Env]` lines from the log, which give the game, Unity and BepInEx versions
- the `[Init]` line
- any `[Gizmo]` or `[RigModel]` warnings
- which boat, and which mods you have installed
