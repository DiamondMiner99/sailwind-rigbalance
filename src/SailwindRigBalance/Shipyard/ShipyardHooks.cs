using System;
using HarmonyLib;
using UnityEngine;

namespace SailwindRigBalance.Shipyard
{
    /// <summary>
    /// Knows when a shipyard session starts, ends, and changes the rig. Every patch is a postfix
    /// that only sets a flag, so a throw here cannot break the shipyard.
    ///
    /// Shipyard.UpdateOrder is the obvious hook and is NOT sufficient on its own: CancelOrder runs
    /// SaveableBoatCustomization.LoadData, which destroys and re-instantiates every sail on the
    /// boat, and neither it nor ResetOrder calls UpdateOrder. Since DischargeShip calls CancelOrder
    /// whenever it runs, an UpdateOrder-only hook would keep drawing from destroyed Sail references
    /// on every exit. LoadData is patched separately for exactly that reason.
    /// </summary>
    public static class ShipyardHooks
    {
        public static GameObject CurrentBoat { get; private set; }
        public static bool InShipyard => CurrentBoat != null;

        /// <summary>
        /// When the current boat was admitted, Time.unscaledTime. The furl-button search waits
        /// this out: the admit frame is when every UI mod in the install is building shipyard
        /// widgets, and a field crash caught Unity dying in a native child walk over an object
        /// that was not finished existing yet.
        /// </summary>
        public static float EnteredAt { get; private set; }

        private static bool _dirty;

        /// <summary>
        /// Which hooks have asked for a rebuild since the last one actually ran, named and without
        /// repeats.
        ///
        /// Kept because the log recorded THAT a rebuild happened and never what caused it, so a hook
        /// that silently stopped matching its target was indistinguishable from a hook whose event
        /// the player had not triggered. That matters most for the paths that exist precisely because
        /// they have no vanilla funnel: Shipyard Expansion's jib flip and spar rotation reach nothing
        /// else, so if either patch fails to apply, the markers keep showing the pre-edit answer and
        /// the gizmo looks live while being wrong. Naming the trigger turns testing that from a
        /// description of what someone thought they saw into a line anyone can read back.
        ///
        /// Bounded, because a drag-resize fires SetSailArea every frame.
        /// </summary>
        private static readonly System.Collections.Generic.List<string> Reasons =
            new System.Collections.Generic.List<string>(8);

        /// <summary>True once since the last call. Recompute when it returns true.</summary>
        public static bool ConsumeDirty()
        {
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }

        /// <summary>
        /// Every distinct trigger since the last time this was called, and clears them.
        ///
        /// Deliberately NOT cleared by ConsumeDirty. The rebuild is debounced, so one rebuild absorbs
        /// many dirty flags, and clearing on the flag would report whichever trigger happened to be
        /// last rather than all of them. Called where the rebuild really runs, so a whole drag
        /// collapses to one line naming one cause.
        /// </summary>
        public static string TakeReasons()
        {
            if (Reasons.Count == 0) return "unknown";
            string joined = string.Join(", ", Reasons.ToArray());
            Reasons.Clear();
            return joined;
        }

        public static void MarkDirty(string reason)
        {
            _dirty = true;
            NoteReason(reason);
        }

        /// <summary>
        /// Records what is about to cause a rebuild WITHOUT asking for one.
        ///
        /// Separate from MarkDirty because the gizmo schedules its own rebuilds for things no vanilla
        /// hook knows about: the selected sail changing, the wind arrow being dragged, and a finished
        /// sweep needing the markers re-colored against it. Those already own their deadline, and
        /// routing them through MarkDirty would ALSO raise the dirty flag, which is what makes the ring
        /// re-sweep. On "sweep finished" that is a sweep that schedules a sweep, forever.
        /// </summary>
        public static void NoteReason(string reason)
        {
            if (reason == null) return;

            for (int i = 0; i < Reasons.Count; i++)
            {
                if (Reasons[i] == reason) return;
            }

            if (Reasons.Count < 8) Reasons.Add(reason);
        }

        private static void Enter(global::Shipyard shipyard)
        {
            try
            {
                CurrentBoat = shipyard != null ? shipyard.GetCurrentBoat() : null;
                EnteredAt = UnityEngine.Time.unscaledTime;
                MarkDirty("AdmitShip");
                Plugin.Log.LogInfo($"[Shipyard] entered with '{(CurrentBoat == null ? "<null>" : CurrentBoat.name)}'");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Shipyard] enter hook failed: " + e.Message);
            }
        }

        private static void Exit()
        {
            CurrentBoat = null;
            MarkDirty("DischargeShip");
            Plugin.Log.LogInfo("[Shipyard] left");
        }

        [HarmonyPatch(typeof(global::Shipyard), "AdmitShip")]
        private static class AdmitShipPatch
        {
            private static void Postfix(global::Shipyard __instance) => Enter(__instance);
        }

        // Since 0.41 the game ignores an exit click while it is still sliding the boat into the
        // cradle (the private movingShip flag), and the player stays in the shipyard, so the session
        // must not end then. Before 0.41 the field does not exist and every exit counts.
        private static readonly System.Reflection.FieldInfo MovingShipField =
            AccessTools.Field(typeof(global::Shipyard), "movingShip");

        private static bool GameIgnoresDischarge(global::Shipyard shipyard)
        {
            try
            {
                return MovingShipField != null && shipyard != null && (bool)MovingShipField.GetValue(shipyard);
            }
            catch
            {
                return false;
            }
        }

        // Prefix: DischargeShip's first statement is CancelOrder(), which destroys every sail.
        // Tear down before that rather than after, so nothing holds a dead reference.
        [HarmonyPatch(typeof(global::Shipyard), nameof(global::Shipyard.DischargeShip))]
        private static class DischargeShipPatch
        {
            private static void Prefix(global::Shipyard __instance)
            {
                if (GameIgnoresDischarge(__instance)) return;
                Exit();
            }
        }

        // Shipyard has TWO UpdateOrder overloads, UpdateOrder(bool) and UpdateOrder(). Patching by
        // name alone is an ambiguous match and throws, so both are named explicitly.
        [HarmonyPatch(typeof(global::Shipyard), nameof(global::Shipyard.UpdateOrder), new Type[] { typeof(bool) })]
        private static class UpdateOrderBoolPatch
        {
            private static void Postfix() => MarkDirty("UpdateOrder(bool)");
        }

        [HarmonyPatch(typeof(global::Shipyard), nameof(global::Shipyard.UpdateOrder), new Type[0])]
        private static class UpdateOrderPatch
        {
            private static void Postfix() => MarkDirty("UpdateOrder()");
        }

        // The rig rebuild path: destroys and re-instantiates every sail from PrefabsDirectory.
        [HarmonyPatch(typeof(SaveableBoatCustomization), nameof(SaveableBoatCustomization.LoadData))]
        private static class LoadDataPatch
        {
            private static void Postfix() => MarkDirty("LoadData");
        }

        [HarmonyPatch(typeof(Mast), nameof(Mast.AttachSailToMast))]
        private static class AttachSailPatch
        {
            private static void Postfix() => MarkDirty("AttachSailToMast");
        }

        [HarmonyPatch(typeof(Mast), nameof(Mast.DetachSailFromMast))]
        private static class DetachSailPatch
        {
            private static void Postfix() => MarkDirty("DetachSailFromMast");
        }

        // Resizing a sail. UpdateOrder is NOT reached on every path to it, so hooking the funnel is
        // not enough: Shipyard.ChangeSailScale goes through ShipyardSailInstaller.ChangeSailScale,
        // which calls Sail.ChangeScale and then MoveHeldSail(0) - and MoveHeldSail returns at
        // ShipyardSailInstaller.cs:122-126 when the mast is onlySquareSails, before the
        // ApplySailPosition that would have called UpdateOrder. So on a spritsail mast the player
        // drags the sail bigger, the game accepts it, the price updates, and every hook here stayed
        // quiet while the sail's area AND its center of effort both moved.
        //
        // Sail.SetSailArea is the one point every resize goes through, vanilla (Sail.cs:188) and
        // Shipyard Expansion (SailScaler.cs:190) alike, and it is the method that rewrites the
        // sailArea field the model reads back through GetRealSailPower. It also fires from Sail.Start
        // and from AddNewSail, which is harmless: the flag is only consumed inside a shipyard and
        // the rebuild is debounced.
        [HarmonyPatch(typeof(Sail), nameof(Sail.SetSailArea))]
        private static class SetSailAreaPatch
        {
            private static void Postfix() => MarkDirty("SetSailArea");
        }

        // Sliding a sail up or down its mast moves the center of effort without changing anything
        // else about it.
        [HarmonyPatch(typeof(Sail), nameof(Sail.ChangeInstallHeight))]
        private static class ChangeInstallHeightPatch
        {
            private static void Postfix() => MarkDirty("ChangeInstallHeight");
        }
    }
}
