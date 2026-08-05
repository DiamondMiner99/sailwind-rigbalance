# Configuration

Settings live in `BepInEx/config/com.sailwindrigbalance.mod.cfg`, written the first time
the mod runs. Edit it with the game closed, or with the game running and re-enter the
shipyard: marker visibility is re-read on every shipyard visit.

## Markers

| setting | default | what it does |
| --- | --- | --- |
| `ShowCenterOfEffort` | `true` | the magenta marker |
| `ShowCenterOfLateralResistance` | `true` | the blue marker |
| `ShowCenterOfMass` | `true` | the yellow marker |

These are also the three buttons in game. Clicking a button writes the setting, so a marker
you hide stays hidden next session.

## Interface

| setting | default | what it does |
| --- | --- | --- |
| `ShowPanel` | `true` | the button strip |
| `PanelPosition` | unset | where the strip sits |

Turn `ShowPanel` off for markers and nothing else. `PanelPosition` is written whenever you
drag the strip; a negative value means unset, which puts it in the top right corner. Delete
the line to reset it.

## Rendering

| setting | default | what it does |
| --- | --- | --- |
| `DrawThroughHull` | `true` | draw markers on top of the boat |
| `MarkerScale` | `1.0` | marker size multiplier, 0.25 to 4 |

`DrawThroughHull` is the important one. Two of the three markers sit inside the hull, so
with this off you will only reliably see the center of effort. It works by rendering the
markers with a second camera that clears depth, on a Unity layer nothing else uses. That is
the least conventional thing this mod does, so the switch exists: if it ever conflicts with
another mod's rendering, turning it off leaves a working but occluded gizmo rather than a
broken screen.

Markers already scale with camera distance to hold a roughly constant size on screen.
`MarkerScale` multiplies that, it does not replace it.

## Model

| setting | default | what it does |
| --- | --- | --- |
| `WindAngleDegrees` | `45` | apparent wind angle used to judge the rig, 20 to 150 |
| `BalancedThresholdMeters` | `0.3` | how far off center still reads as balanced, 0.05 to 3 |

`WindAngleDegrees` is 45, close hauled, because that is where balance bites. Raise it
toward 90 to judge a beam reach. Push it much past 120 and there is barely any side force
left to balance, so everything will read as neutral. This is a judgement about where
balance matters, not a physical constant, which is why it is exposed.

`BalancedThresholdMeters` is the green band on the link. Inside it the rig reads as
balanced; outside, the link takes a helm color. Raise it if you want a more forgiving
verdict, lower it if you want to tune a rig finely.

## What is not configurable

Marker colors are fixed. They map to Kerbal Space Program's convention closely enough to
be learnable, and a color-coded readout stops being readable the moment everyone's is
different.

The refresh delay after a shipyard edit is fixed. It is not a performance tuning knob; it
exists because applying a customization destroys and recreates every sail, and the mod has
to let that finish before it measures anything.
