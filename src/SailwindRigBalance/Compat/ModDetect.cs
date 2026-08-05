using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Bootstrap;

namespace SailwindRigBalance.Compat
{
    /// <summary>
    /// Runtime presence/version detection for the installed mods that change what a rig-balance
    /// number MEANS. Everything here goes through BepInEx's chainloader, so there is no hard
    /// assembly reference to any of them and nothing throws when they are absent.
    ///
    /// The GUIDs are the ones each mod actually declares in its own BepInPlugin attribute.
    /// Note two traps for anyone extending this list:
    ///  - Bridge/library DLLs (SE_Bridge, LeopardBridge, WindiBridge) carry NO BepInPlugin, so
    ///    they never appear in PluginInfos. Detect them via their parent plugin instead.
    ///  - A mod can patch the game without declaring a single [HarmonyPatch] attribute. SailInfo
    ///    patches imperatively via Harmony.Patch, so an attribute-only survey misses it entirely.
    /// </summary>
    public static class ModDetect
    {
        // Soft dependencies are declared on Plugin so BepInEx orders us AFTER these when present.
        public const string ShipyardExpansionGuid = "com.nandbrew.shipyardexpansion";
        public const string SailCollisionFixGuid = "com.nandbrew.sailcollisionfix";
        public const string NandTweaksGuid = "com.nandbrew.nandtweaks";
        public const string TowableBoatsGuid = "com.nandbrew.towableboats";
        public const string SailInfoGuid = "pr0skynesis.sailinfo";
        public const string InstrumentDisplayGuid = "com.dixiecapt.sailwind.marinershud";
        public const string LeopardGuid = "com.winter.leopard";
        public const string CoopGuid = "com.sailwindcoop.mod";

        /// <summary>
        /// Why each one matters to a center-of-effort / yaw-moment readout. Printed alongside the
        /// detection result so a pasted log explains itself without needing this source file.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] Watched =
        {
            new KeyValuePair<string, string>(ShipyardExpansionGuid,
                "sail resizing, spar rotation, jib flip; also replaces Sail.GetSailArea outright"),
            new KeyValuePair<string, string>(SailCollisionFixGuid,
                "widens the installed sail angle limits, so the same rig trims differently"),
            new KeyValuePair<string, string>(NandTweaksGuid,
                "already writes into the shipyard description panel; heavy ShipyardUI patcher"),
            new KeyValuePair<string, string>(TowableBoatsGuid,
                "patches BoatPerformanceSwitcher/BoatHorizon, which gate the hull's drag component"),
            new KeyValuePair<string, string>(SailInfoGuid,
                "reads the same private Sail force fields; prior art, not a conflict"),
            new KeyValuePair<string, string>(InstrumentDisplayGuid,
                "adjacent readout (metacentric height) drawn in OnGUI"),
            new KeyValuePair<string, string>(LeopardGuid,
                "adds hulls beyond the seven vanilla ones"),
            new KeyValuePair<string, string>(CoopGuid,
                "adds remote crew weight to Rigidbody.centerOfMass, which is our yaw pivot"),
        };

        public static bool ShipyardExpansionInstalled { get; private set; }
        public static string ShipyardExpansionVersion { get; private set; } = "";

        /// <summary>
        /// Reads the chainloader once and logs what is present. Never throws: a detection failure
        /// must degrade the readout, not stop the plugin loading.
        /// </summary>
        public static void InitAndLog()
        {
            ShipyardExpansionInstalled = false;
            ShipyardExpansionVersion = "";

            try
            {
                var infos = Chainloader.PluginInfos;
                Plugin.Log.LogInfo($"[Compat] BepInEx reports {infos.Count} plugin(s) loaded.");

                var sb = new StringBuilder();
                foreach (var watched in Watched)
                {
                    string version = TryGetVersion(infos, watched.Key);
                    if (version == null)
                    {
                        sb.AppendLine($"  [ ] {watched.Key} - not installed");
                        continue;
                    }

                    sb.AppendLine($"  [x] {watched.Key} v{version} - {watched.Value}");
                    if (watched.Key == ShipyardExpansionGuid)
                    {
                        ShipyardExpansionInstalled = true;
                        ShipyardExpansionVersion = version;
                    }
                }

                Plugin.Log.LogInfo("[Compat] Mods that affect a rig-balance readout:\n" + sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Compat] Mod detection failed; continuing without it. " + e);
            }
        }

        private static string TryGetVersion(IDictionary<string, BepInEx.PluginInfo> infos, string guid)
        {
            if (infos == null) return null;
            if (!infos.TryGetValue(guid, out var info) || info == null) return null;
            var metadata = info.Metadata;
            if (metadata == null) return "unknown";
            var version = metadata.Version;
            return version == null ? "unknown" : version.ToString();
        }
    }
}
