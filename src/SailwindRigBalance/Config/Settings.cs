using BepInEx.Configuration;
using UnityEngine;

namespace SailwindRigBalance.Config
{
    /// <summary>
    /// Every knob the mod has, bound once at load.
    ///
    /// Two things here are deliberately configurable rather than fixed, because both are places
    /// where a future game or mod update could break the gizmo and a player should be able to work
    /// around it without waiting for a release:
    ///  - DrawThroughHull, because it costs a second camera. That is the least conventional thing
    ///    the mod does, and if it ever conflicts with another mod's rendering, turning it off leaves
    ///    a working, merely occluded, gizmo instead of a broken screen.
    ///  - WindAngle, because 45 degrees is a judgement about where balance matters, not a fact.
    ///
    /// There is deliberately no panel position entry. The strip is fixed in the top right: nothing
    /// else in the shipyard can be dragged, so one thing that could read as belonging to a
    /// different program rather than to the game.
    /// </summary>
    /// <summary>What the number beside each sail force arrow is written in.</summary>
    public enum SailForceUnits
    {
        Kilonewtons,
        Percent,
    }

    /// <summary>Which way the wind arrow on the balance ring points.</summary>
    public enum WindArrowStyles
    {
        /// <summary>Points the way the air moves, inward at the boat.</summary>
        Downwind,

        /// <summary>Points into the wind, out toward where it comes from, like a weather vane.</summary>
        WeatherVane,
    }

    /// <summary>The wind arrow's speed range, m/s. Vanilla winds run from a regional base up to about
    /// twenty more in a storm or far out at sea (Wind.cs:104-142). The base is clamped to 18 (the scene's
    /// Wind.maximumMagnitude, level24), so a steady storm aims at 38 m/s, and every half second a gust
    /// scales that by up to a third either way, a ceiling of about 50.5. The arrow goes to 100 knots so
    /// the wind in port is never cut off (it was at 30, on three visits in one storm, 2026-11-14).</summary>
    public static class RigBalanceLimits
    {
        public const float MinWindSpeed = 1f;
        public const float MaxWindSpeed = 51.4444f;   // 100 knots
    }

    public static class Settings
    {
        public static ConfigEntry<bool> ShowEffort { get; private set; }
        public static ConfigEntry<bool> ShowResistance { get; private set; }
        public static ConfigEntry<bool> ShowMass { get; private set; }
        public static ConfigEntry<bool> ShowReefTrack { get; private set; }
        public static ConfigEntry<bool> ShowRing { get; private set; }
        public static ConfigEntry<bool> ShowSailForces { get; private set; }
        public static ConfigEntry<SailForceUnits> SailForceUnit { get; private set; }
        public static ConfigEntry<WindArrowStyles> WindArrowStyle { get; private set; }
        public static ConfigEntry<bool> ShowPanel { get; private set; }
        public static ConfigEntry<KeyboardShortcut> FurlSelectedSailKey { get; private set; }
        public static ConfigEntry<bool> DrawThroughHull { get; private set; }
        public static ConfigEntry<float> MarkerScale { get; private set; }
        public static ConfigEntry<float> WindAngle { get; private set; }
        public static ConfigEntry<float> BalancedThreshold { get; private set; }
        public static ConfigEntry<bool> ModelSailShading { get; private set; }
        public static ConfigEntry<bool> ModelHeel { get; private set; }
        public static ConfigEntry<float> WindSpeed { get; private set; }
        public static ConfigEntry<bool> StartFromLocalWind { get; private set; }
        public static ConfigEntry<bool> ShowWaterline { get; private set; }
        public static ConfigEntry<bool> LogHeelAtSea { get; private set; }
        public static ConfigEntry<bool> ShowHullShowcaseButton { get; private set; }
        public static ConfigEntry<bool> ShowRingLegend { get; private set; }

        public static void Bind(ConfigFile config)
        {
            ShowEffort = config.Bind("Markers", "ShowCenterOfEffort", true,
                "Show the magenta marker: where the sails pull, with each sail weighted by its force "
                + "at best trim.");

            ShowResistance = config.Bind("Markers", "ShowCenterOfLateralResistance", true,
                "Show the blue marker, where the hull's lateral drag pushes back, and the helm bar "
                + "that runs from it, which is the balance reading.");

            ShowMass = config.Bind("Markers", "ShowCenterOfMass", true,
                "Show the yellow marker: the boat's own balance point. It does not respond to sails, "
                + "only to a keel or ballast option where the hull has one.");

            ShowReefTrack = config.Bind("Markers", "ShowReefTrack", true,
                "With a sail selected, draw a line from the current center of effort to where it "
                + "would sit with that sail furled. Its color is the helm at the far end, or gray "
                + "where the sails are barely drawing.");

            ShowRing = config.Bind("Markers", "ShowBalanceRing", true,
                "Draw a ring around the boat colored by what the helm would do at every point of "
                + "sail: green balanced, blue weather helm, red lee helm, gray where the sails are "
                + "not drawing enough for the question to mean anything. Red anywhere on the ring is "
                + "a boat that develops lee helm on some heading, which a single reading cannot show.");

            ShowSailForces = config.Bind("Markers", "ShowSailForces", false,
                "Draw an arrow at each sail's center of effort showing the force it makes at the "
                + "wind arrow's angle, trimmed for best drive, in the horizontal plane that decides "
                + "the helm, with the force written beside it. The longest arrow is the sail pulling "
                + "hardest. Also a button on the strip, which cycles off, kN, percent.");

            SailForceUnit = config.Bind("Markers", "SailForceUnits", SailForceUnits.Kilonewtons,
                "How the number beside each sail force arrow is written. Kilonewtons is about what "
                + "a sail of that area would pull in a real wind of the wind arrow's speed, with the "
                + "game deciding how hard each sail draws at its angle. Percent is that sail's share of all "
                + "the sails' force added together, ignoring direction, so a rig adds up to 100. "
                + "The strip's button sets this as it cycles.");

            WindArrowStyle = config.Bind("Markers", "WindArrowStyle", WindArrowStyles.Downwind,
                "Which way the wind arrow on the balance ring points. Downwind points the way the air "
                + "moves, in at the boat. WeatherVane points into the wind, out toward where it comes "
                + "from. Either way the arrow sits at the bearing the wind comes from and is dragged "
                + "the same way.");
            WindArrowStyle.SettingChanged += (sender, args) =>
                SailwindRigBalance.Shipyard.ShipyardHooks.MarkDirty("wind arrow style setting");

            ShowPanel = config.Bind("Interface", "ShowPanel", true,
                "Show the button strip and the furl button, and listen for the furl key. Turn this off "
                + "to keep the markers with no interface at all.");

            FurlSelectedSailKey = config.Bind("Interface", "FurlSelectedSailKey",
                new KeyboardShortcut(KeyCode.G),
                "Furls the sail selected in the shipyard, or sets it if it is furled, the same as the "
                + "mod's furl button. Clear it to turn the key off.");

            DrawThroughHull = config.Bind("Rendering", "DrawThroughHull", true,
                "Draw the markers on top of the boat with a depth-clearing overlay camera. Two of "
                + "the three markers sit inside the hull, so with this off they are hidden by it. "
                + "Turn it off if it ever conflicts with another mod's rendering.");

            MarkerScale = config.Bind("Rendering", "MarkerScale", 1f,
                new ConfigDescription(
                    "Multiplier on marker size. Markers scale with camera distance to hold a "
                    + "roughly constant size on screen; this scales that.",
                    new AcceptableValueRange<float>(0.25f, 4f)));

            StartFromLocalWind = config.Bind("Model", "StartFromLocalWind", true,
                "When the shipyard opens, start the wind arrow at the wind blowing in port: the bearing "
                + "it comes from against the way the boat is lying, and its speed. Drag the arrow from "
                + "there as usual. Off, or in a dead calm, or in the hull showcase, the arrow starts at "
                + "WindAngleDegrees and WindSpeedMetersPerSecond.");

            WindAngle = config.Bind("Model", "WindAngleDegrees", 45f,
                new ConfigDescription(
                    "Apparent wind angle off the bow used to evaluate the rig, when StartFromLocalWind "
                    + "is off and in the hull showcase. 45 is close hauled, "
                    + "which is where balance bites. Near a run there is almost no side force to "
                    + "balance and every rig reads as neutral.",
                    new AcceptableValueRange<float>(20f, 150f)));

            BalancedThreshold = config.Bind("Model", "BalancedThresholdFraction", 0.02f,
                new ConfigDescription(
                    "How long the helm lever may get before the helm bar stops reading as "
                    + "balanced, as a fraction of the hull's "
                    + "length. A fraction rather than a fixed distance because half a meter is "
                    + "nothing on a brig and most of the boat on a dinghy.",
                    new AcceptableValueRange<float>(0.002f, 0.15f)));

            ModelSailShading = config.Bind("Model", "ModelSailShading", true,
                "Let sails take wind from each other the way the game does at sea: a sail with another "
                + "sail close upwind of it loses part of its force. Turn it off to judge every sail as "
                + "if it had clear air.");
            ModelSailShading.SettingChanged += (sender, args) =>
                SailwindRigBalance.Shipyard.ShipyardHooks.MarkDirty("sail shading setting");

            ModelHeel = config.Bind("Model", "ModelHeel", true,
                "Heel the boat in the wind the wind arrow sets, from the hull's own buoyancy and weight, "
                + "and read the helm at that heel. Turn it off to read every rig as if she sailed "
                + "upright, which hides the waterline too.");
            ModelHeel.SettingChanged += (sender, args) =>
                SailwindRigBalance.Shipyard.ShipyardHooks.MarkDirty("heel setting");

            WindSpeed = config.Bind("Model", "WindSpeedMetersPerSecond", 10f,
                new ConfigDescription(
                    "The wind speed the wind arrow starts at when StartFromLocalWind is off and in the "
                    + "hull showcase, in meters per second (10 is about 19 "
                    + "knots). Drag the knob on the arrow's tail to change it in the shipyard.",
                    new AcceptableValueRange<float>(RigBalanceLimits.MinWindSpeed, RigBalanceLimits.MaxWindSpeed)));

            LogHeelAtSea = config.Bind("Diagnostics", "LogHeelAtSea", true,
                "While sailing, write a line to the log once a minute comparing the heel the mod "
                + "would predict for the wind and sails you have with the heel the boat actually has. "
                + "It only reads, and is there so a pasted log shows how far the shipyard's heel can be trusted.");

            ShowWaterline = config.Bind("Markers", "ShowWaterline", true,
                "Draw where the sea would meet the hull in the wind the arrow sets. It turns red when "
                + "water would come aboard. The balance ring's button hides it along with the ring.");
            ShowWaterline.SettingChanged += (sender, args) =>
                SailwindRigBalance.Shipyard.ShipyardHooks.MarkDirty("waterline setting");

            ShowRingLegend = config.Bind("Interface", "ShowRingLegend", true,
                "While the balance ring is shown, put a legend of its colors under the button strip.");

            ShowHullShowcaseButton = config.Bind("Interface", "ShowHullShowcaseButton", false,
                "Put a Hull showcase button on the main menu. Off by default; it is a tool for checking "
                + "the mod against every hull rather than something a player needs. It looks at every boat in the world in "
                + "turn with the waterline, heel and balance ring drawn on it, before any save is "
                + "loaded, and writes each hull's waterline to the log. It only reads: no boat moves "
                + "and nothing is saved.");

            // No PanelPosition entry. The strip is fixed in the top right, because nothing else in
            // the shipyard can be dragged and a panel that could read as belonging to another
            // program.
        }
    }
}
