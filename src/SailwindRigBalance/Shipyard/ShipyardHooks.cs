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
    /// unconditionally, an UpdateOrder-only hook would keep drawing from destroyed Sail references
    /// on every exit. LoadData is patched separately for exactly that reason.
    /// </summary>
    public static class ShipyardHooks
    {
        public static GameObject CurrentBoat { get; private set; }
        public static bool InShipyard => CurrentBoat != null;

        private static bool _dirty;

        /// <summary>True once since the last call. Recompute when it returns true.</summary>
        public static bool ConsumeDirty()
        {
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }

        public static void MarkDirty() => _dirty = true;

        private static void Enter(global::Shipyard shipyard)
        {
            try
            {
                CurrentBoat = shipyard != null ? shipyard.GetCurrentBoat() : null;
                _dirty = true;
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
            _dirty = true;
            Plugin.Log.LogInfo("[Shipyard] left");
        }

        [HarmonyPatch(typeof(global::Shipyard), "AdmitShip")]
        private static class AdmitShipPatch
        {
            private static void Postfix(global::Shipyard __instance) => Enter(__instance);
        }

        // Prefix: DischargeShip's first statement is CancelOrder(), which destroys every sail.
        // Tear down before that rather than after, so nothing holds a dead reference.
        [HarmonyPatch(typeof(global::Shipyard), nameof(global::Shipyard.DischargeShip))]
        private static class DischargeShipPatch
        {
            private static void Prefix() => Exit();
        }

        // Shipyard has TWO UpdateOrder overloads, UpdateOrder(bool) and UpdateOrder(). Patching by
        // name alone is an ambiguous match and throws, so both are named explicitly.
        [HarmonyPatch(typeof(global::Shipyard), nameof(global::Shipyard.UpdateOrder), new Type[] { typeof(bool) })]
        private static class UpdateOrderBoolPatch
        {
            private static void Postfix() => MarkDirty();
        }

        [HarmonyPatch(typeof(global::Shipyard), nameof(global::Shipyard.UpdateOrder), new Type[0])]
        private static class UpdateOrderPatch
        {
            private static void Postfix() => MarkDirty();
        }

        // The rig rebuild path: destroys and re-instantiates every sail from PrefabsDirectory.
        [HarmonyPatch(typeof(SaveableBoatCustomization), nameof(SaveableBoatCustomization.LoadData))]
        private static class LoadDataPatch
        {
            private static void Postfix() => MarkDirty();
        }

        [HarmonyPatch(typeof(Mast), nameof(Mast.AttachSailToMast))]
        private static class AttachSailPatch
        {
            private static void Postfix() => MarkDirty();
        }

        [HarmonyPatch(typeof(Mast), nameof(Mast.DetachSailFromMast))]
        private static class DetachSailPatch
        {
            private static void Postfix() => MarkDirty();
        }
    }
}
