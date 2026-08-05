using System;
using System.Reflection;
using HarmonyLib;
using SailwindRigBalance.Shipyard;

namespace SailwindRigBalance.Compat
{
    /// <summary>
    /// Invalidates the gizmo when Shipyard Expansion moves a sail by a route that never reaches
    /// vanilla's UpdateOrder.
    ///
    /// The gizmo's normal invalidation funnels through Shipyard.UpdateOrder, and Shipyard Expansion
    /// never calls it. SE reaches it only incidentally, through
    /// ShipyardSailInstaller.MoveHeldSail at SailScaleButton.cs:107, and two of its controls return
    /// before that:
    ///  - the jib flip branch is `scaler.FlipJib(); return;` (SailScaleButton.cs:96-99), and
    ///    SailScaler.FlipJib rewrites scaleablePart's localEulerAngles and localPosition
    ///    (SailScaler.cs:110-121), which is the transform windcenter lives inside on every stock
    ///    prefab. The sail's center of effort physically moves and nothing fires.
    ///  - SetAngle only forwards to MoveHeldSail when the sail is the currently selected one
    ///    (SailScaler.cs:95-100), and MoveHeldSail itself early-returns on an uninstalled or
    ///    spritsail mast before ApplySailPosition.
    ///
    /// Left alone the magenta marker, the helm link and the whole ring kept showing the pre-flip
    /// answer indefinitely, until the player happened to touch some other control. That is the
    /// failure a user cannot catch, because the gizmo looks live and is simply wrong.
    ///
    /// Resolved by type name through Harmony rather than by referencing SE, so this compiles and
    /// runs whether or not it is installed. SetScaleAbs is covered by the vanilla Sail.SetSailArea
    /// hook, which SE calls at SailScaler.cs:190; the two here have no such vanilla funnel.
    /// </summary>
    public static class SailScalerHooks
    {
        private const string ScalerTypeName = "ShipyardExpansion.SailScaler";

        public static void Apply(Harmony harmony)
        {
            if (harmony == null || !ModDetect.ShipyardExpansionInstalled) return;

            try
            {
                Type scaler = AccessTools.TypeByName(ScalerTypeName);
                if (scaler == null)
                {
                    Plugin.Log.LogWarning($"[Compat] Shipyard Expansion is loaded but '{ScalerTypeName}' "
                        + "did not resolve, so a jib flip or a spar rotation will not refresh the "
                        + "markers. Touch any other shipyard control to force a refresh.");
                    return;
                }

                // One postfix each rather than one shared, so the log names which of SE's controls
                // fired. These two are the whole reason this file exists: neither reaches any vanilla
                // funnel, so if one silently stops matching, the markers keep showing the pre-edit
                // answer and nothing else in the log would ever say so.
                int applied = 0;
                applied += Patch(harmony, scaler, "FlipJib", new[] { typeof(bool) },
                    new HarmonyMethod(AccessTools.Method(typeof(SailScalerHooks), nameof(InvalidateFlip))));
                applied += Patch(harmony, scaler, "SetAngle", new[] { typeof(float) },
                    new HarmonyMethod(AccessTools.Method(typeof(SailScalerHooks), nameof(InvalidateAngle))));

                Plugin.Log.LogInfo($"[Compat] {applied} Shipyard Expansion sail-edit hook(s) applied.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Compat] could not hook Shipyard Expansion's sail editing: " + e.Message);
            }
        }

        /// <summary>
        /// The no-argument FlipJib() delegates to FlipJib(bool) (SailScaler.cs:102-105), and
        /// RotateFwd/RotateBkwd delegate to SetAngle(float) (SailScaler.cs:156-162), so patching the
        /// two that do the work covers every button.
        /// </summary>
        private static int Patch(Harmony harmony, Type type, string name, Type[] arguments,
            HarmonyMethod postfix)
        {
            try
            {
                MethodInfo method = AccessTools.Method(type, name, arguments);
                if (method == null)
                {
                    Plugin.Log.LogWarning($"[Compat] Shipyard Expansion has no {type.Name}.{name}; "
                        + "that control will not refresh the markers.");
                    return 0;
                }

                harmony.Patch(method, null, postfix);
                return 1;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Compat] {type.Name}.{name} could not be hooked: " + e.Message);
                return 0;
            }
        }

        /// <summary>Sets a flag and nothing else, so a throw here cannot break SE's own button.</summary>
        private static void InvalidateFlip() => ShipyardHooks.MarkDirty("SE FlipJib");

        /// <summary>As above, for SE's spar rotation. Named apart so the log distinguishes them.</summary>
        private static void InvalidateAngle() => ShipyardHooks.MarkDirty("SE SetAngle");
    }
}
