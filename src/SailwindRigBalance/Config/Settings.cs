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
    /// The panel position is stored rather than exposed: nobody edits a window rectangle by hand,
    /// but having dragged it once, a player should not have to do it again next session.
    /// </summary>
    public static class Settings
    {
        public static ConfigEntry<bool> ShowEffort { get; private set; }
        public static ConfigEntry<bool> ShowResistance { get; private set; }
        public static ConfigEntry<bool> ShowMass { get; private set; }
        public static ConfigEntry<bool> ShowPanel { get; private set; }
        public static ConfigEntry<bool> DrawThroughHull { get; private set; }
        public static ConfigEntry<float> MarkerScale { get; private set; }
        public static ConfigEntry<float> WindAngle { get; private set; }
        public static ConfigEntry<float> BalancedThreshold { get; private set; }
        public static ConfigEntry<Vector2> PanelPosition { get; private set; }

        public static void Bind(ConfigFile config)
        {
            ShowEffort = config.Bind("Markers", "ShowCentreOfEffort", true,
                "Show the magenta marker: where the whole sail plan pushes.");

            ShowResistance = config.Bind("Markers", "ShowCentreOfLateralResistance", true,
                "Show the blue marker: where the hull's lateral drag pushes back. The fore and aft "
                + "offset between this and the magenta marker is the balance reading.");

            ShowMass = config.Bind("Markers", "ShowCentreOfMass", true,
                "Show the yellow marker: the boat's own balance point. It does not respond to sails, "
                + "only to a keel or ballast option where the hull has one.");

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

            BalancedThreshold = config.Bind("Model", "BalancedThresholdMetres", 0.3f,
                new ConfigDescription(
                    "How far the centre of effort may sit from the centre of lateral resistance "
                    + "before the link stops reading as balanced and takes on a helm colour.",
                    new AcceptableValueRange<float>(0.05f, 3f)));

            PanelPosition = config.Bind("Interface", "PanelPosition", new Vector2(-1f, -1f),
                "Where the button strip sits, remembered after you drag it. Negative means "
                + "unset, which places it in the top right corner.");
        }
    }
}
