# Configuration

Settings live in `BepInEx/config/com.sailwindrigbalance.mod.cfg`, written the first time
the mod runs. Edit it with the game closed, or with the game running and re-enter the
shipyard: marker visibility is re-read on every shipyard visit.

## Markers

| setting | default | what it does |
| --- | --- | --- |
| `ShowCenterOfEffort` | `true` | the COE marker |
| `ShowCenterOfLateralResistance` | `true` | the CLR marker and the helm bar |
| `ShowCenterOfMass` | `true` | the center of mass marker |
| `ShowReefTrack` | `true` | with a sail selected, a line from the center of effort to where it would go if you furled that sail, or set it if furled |
| `ShowBalanceRing` | `true` | the ring showing the helm at every point of sail |
| `ShowHelmTicks` | `false` | a tick at every bearing of the ring that reads without color: out of the ring for weather helm, into it for lee helm, longer the stronger the helm, none for balanced or no verdict |
| `ShowSailForces` | `false` | an arrow for each sail's force at the wind arrow's angle |
| `SailForceUnits` | `Kilonewtons` | whether the arrows' numbers are kN or percent |
| `WindArrowStyle` | `Downwind` | `Downwind` points the wind arrow the way the wind blows, `WeatherVane` points it into the wind |
| `ShowWaterline` | `true` | where the sea would meet the hull in the arrow's wind; it turns red when water would come aboard |

The three markers, the ring and the sail forces are also buttons in game. Clicking a
button writes the setting, so a marker you hide stays hidden next session.

## Interface

| setting | default | what it does |
| --- | --- | --- |
| `ShowPanel` | `true` | the button strip and the furl button |
| `FurlSelectedSailKey` | `G` | furls the selected sail, or sets it if it is furled; clear it to turn the key off |
| `ShowRingLegend` | `true` | while the ring is shown, a legend of its colors under the button strip: strong weather helm, weather helm, balanced, lee helm, strong lee helm, and gray for no verdict |
| `ColorblindPalette` | `false` | helm colors in blue, white and orange instead of blue, green and red, on the ring, the helm bar, the reef track, the legend and the ring button |
| `ShowHullShowcaseButton` | `false` | a Hull showcase button on the main menu, which steps through every boat in the world with the waterline, heel and balance ring on it and writes each hull's waterline to the log |

Turn `ShowPanel` off for markers and nothing else, which also turns off the furl key. The strip sits in the top right corner.

## Rendering

| setting | default | what it does |
| --- | --- | --- |
| `DrawThroughHull` | `true` | draw markers on top of the boat |
| `MarkerScale` | `1.0` | marker size multiplier, 0.25 to 4 |

`DrawThroughHull` is the important one. Two of the three markers sit inside the hull, so
with this off you will only reliably see the center of effort and the helm bar's arrow. It works by rendering the
markers with a second camera that clears depth, on a Unity layer nothing else uses. That is
the least conventional thing this mod does, so the switch exists: if it ever conflicts with
another mod's rendering, turning it off leaves a working but occluded gizmo rather than a
broken screen.

Markers already scale with camera distance to hold a roughly constant size on screen.
`MarkerScale` multiplies that, it does not replace it.

## Camera

| setting | default | what it does |
| --- | --- | --- |
| `ExtendShipyardCamera` | `true` | in the shipyard, hold the right mouse button and move the mouse up or down to raise or lower the view, from the deck to the top of the rig, and zoom out further than the game allows |
| `MaxZoomOutMeters` | `80` | how far the shipyard camera zooms out with `ExtendShipyardCamera` on, 40 to 150; the game stops at 40 |

The view is never taken below the sea. With NANDTweaks' camera tweak on, this mod's lift
replaces NANDTweaks' in the shipyard, so the two do not add up. With HMS Leopard installed,
the zoom is Leopard's own, out to 80 m, and `MaxZoomOutMeters` has no effect.

## Model

| setting | default | what it does |
| --- | --- | --- |
| `StartFromLocalWind` | `true` | start the wind arrow at the wind blowing in port when the shipyard opens |
| `WindAngleDegrees` | `45` | apparent wind angle used to judge the rig when `StartFromLocalWind` is off and in the hull showcase, 20 to 150 |
| `BalancedThresholdFraction` | `0.02` | how far off center reads as balanced, as a fraction of hull length, 0.002 to 0.15 |
| `WindSpeedMetersPerSecond` | `10` | the wind speed the arrow starts at when `StartFromLocalWind` is off and in the hull showcase, 1 to 51.4 (100 knots) |
| `ModelHeel` | `true` | heel the boat in the arrow's wind from her buoyancy and weight, and read the helm at that heel; off reads her upright and hides the waterline |
| `ModelSailShading` | `true` | a sail with another sail close upwind of it loses part of its force, as the game does at sea; off judges every sail in clear air |

`StartFromLocalWind` sets the arrow once, when the shipyard opens: the bearing the wind in port
comes from, measured against the way the boat is lying, and its speed. From there it is dragged
as usual. In a dead calm, or with the setting off, the arrow starts at `WindAngleDegrees` and
`WindSpeedMetersPerSecond`, as it always does in the hull showcase.

`WindAngleDegrees` is 45, close hauled, because that is where balance bites. Raise it
toward 90 to judge a beam reach. Push it much past 120 and there is barely any side force
left to balance, so everything will read as neutral. This is a judgement about where
balance matters, not a physical constant, which is why it is exposed.

`BalancedThresholdFraction` is the green band on the helm bar, as a fraction of the hull's
length, so one setting suits a small boat and a brig. At the default of 0.02 a 30 m hull gets
a 0.6 m band. Inside it the rig reads as balanced; outside, the bar takes a helm color.
Raise it if you want a more forgiving verdict, lower it if you want to tune a rig finely.

## Diagnostics

| setting | default | what it does |
| --- | --- | --- |
| `LogHeelAtSea` | `true` | while sailing, once a minute, log the heel and ride height the mod would predict for your wind and sails next to what the boat actually has; it only reads |

## What is not configurable

Marker colors are fixed. They map to Kerbal Space Program's convention closely enough to
be learnable, and a color-coded readout stops being readable the moment everyone's is
different.

The refresh delay after a shipyard edit is fixed. It is not a performance tuning knob; it
exists because applying a customization destroys and recreates every sail, and the mod has
to let that finish before it measures anything.
