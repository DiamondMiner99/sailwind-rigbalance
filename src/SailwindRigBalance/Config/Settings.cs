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
    public static class Settings
    {
        public static ConfigEntry<bool> ShowEffort { get; private set; }
        public static ConfigEntry<bool> ShowResistance { get; private set; }
        public static ConfigEntry<bool> ShowMass { get; private set; }
        public static ConfigEntry<bool> ShowReefTrack { get; private set; }
        public static ConfigEntry<bool> ShowRing { get; private set; }
        public static ConfigEntry<bool> ShowSailForces { get; private set; }
        public static ConfigEntry<bool> ShowPanel { get; private set; }
        public static ConfigEntry<bool> DrawThroughHull { get; private set; }
        public static ConfigEntry<float> MarkerScale { get; private set; }
        public static ConfigEntry<float> WindAngle { get; private set; }
        public static ConfigEntry<float> BalancedThreshold { get; private set; }

        public static void Bind(ConfigFile config)
        {
            ShowEffort = config.Bind("Markers", "ShowCenterOfEffort", true,
                "Show the magenta marker: where the whole sail plan pushes.");

            ShowResistance = config.Bind("Markers", "ShowCenterOfLateralResistance", true,
                "Show the blue marker: where the hull's lateral drag pushes back. The fore and aft "
                + "offset between this and the magenta marker is the balance reading.");

            ShowMass = config.Bind("Markers", "ShowCenterOfMass", true,
                "Show the yellow marker: the boat's own balance point. It does not respond to sails, "
                + "only to a keel or ballast option where the hull has one.");

            ShowReefTrack = config.Bind("Markers", "ShowReefTrack", true,
                "With a sail selected, draw a line from the current center of effort to where it "
                + "would sit with that sail furled. Its color is the verdict at the far end, or "
                + "gray where the sails are barely drawing. The two ends are exact and the line "
                + "between them is close but not exact. It is not drawn at all on a heading where "
                + "there is no helm reading to compare, or where furling the sail reverses which "
                + "side the rig pushes, because a straight line would be inventing an answer.");

            ShowRing = config.Bind("Markers", "ShowBalanceRing", true,
                "Draw a ring around the boat colored by what the helm would do at every point of "
                + "sail: green balanced, blue weather helm, red lee helm, gray where the sails are "
                + "not drawing enough for the question to mean anything. Red anywhere on the ring is "
                + "a boat that develops lee helm on some heading, which a single reading cannot show.");

            ShowSailForces = config.Bind("Markers", "ShowSailForces", false,
                "Draw an arrow at each sail's center of effort showing the force it makes at the "
                + "wind arrow's angle, trimmed for best drive, in the horizontal plane that decides "
                + "the helm, with that sail's share of the rig's total force written beside it. The "
                + "longest arrow is the sail pulling hardest. Shows why the center of effort sits "
                + "where it does. Also a button on the strip.");

            ShowPanel = config.Bind("Interface", "ShowPanel", true,
                "Show the button strip. Turn this off to keep the markers with no interface at all.");

            DrawThroughHull = config.Bind("Rendering", "DrawThroughHull", true,
                "Draw the markers on top of the boat with a depth-clearing overlay camera. Two of "
                + "the three markers sit inside the hull, so with this off they are hidden by it. "
                + "Turn it off if it ever conflicts with another mod's rendering.");

            MarkerScale = config.Bind("Rendering", "MarkerScale", 1f,
                new ConfigDescription(
                    "Multiplier on marker size. Markers scale with camera distance to hold a "
                    + "roughly constant size on screen; this scales that.",
                    new AcceptableValueRange<float>(0.25f, 4f)));

            WindAngle = config.Bind("Model", "WindAngleDegrees", 45f,
                new ConfigDescription(
                    "Apparent wind angle off the bow used to evaluate the rig. 45 is close hauled, "
                    + "which is where balance bites. Near a run there is almost no side force to "
                    + "balance and every rig reads as neutral.",
                    new AcceptableValueRange<float>(20f, 150f)));

            BalancedThreshold = config.Bind("Model", "BalancedThresholdFraction", 0.02f,
                new ConfigDescription(
                    "How far the center of effort may sit from the center of lateral resistance "
                    + "before the link stops reading as balanced, as a fraction of the hull's "
                    + "length. A fraction rather than a fixed distance because half a meter is "
                    + "nothing on a brig and most of the boat on a dinghy.",
                    new AcceptableValueRange<float>(0.002f, 0.15f)));

            // No PanelPosition entry. The strip is fixed in the top right, because nothing else in
            // the shipyard can be dragged and a panel that could read as belonging to another
            // program.
        }
    }
}
