using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SailwindRigBalance.Compat;
using UnityEngine;

namespace SailwindRigBalance
{
    /// <summary>
    /// Entry point. Applies the shipyard hooks and hosts the gizmo. Nothing this plugin does runs
    /// outside a shipyard.
    /// </summary>
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Soft dependencies only. Every one of these is optional; declaring them makes BepInEx load us
    // AFTER them when they are present, so their patches are applied and their configs are bound
    // before we read anything. Presence is then detected at runtime through the chainloader
    // (see ModDetect) rather than assumed from the attribute.
    [BepInDependency(ModDetect.ShipyardExpansionGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ModDetect.SailCollisionFixGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ModDetect.NandTweaksGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ModDetect.TowableBoatsGuid, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ModDetect.LeopardGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.sailwindrigbalance.mod";
        public const string PluginName = "Sailwind Rig Balance";

        // Must stay System.Version-parseable (major.minor[.build]). BepInEx 5 does NOT strip a
        // semver pre-release suffix: a "-alpha" tag makes the chainloader reject the plugin
        // ("version is invalid") and skip it entirely. Any "alpha" wording lives in prose only.
        // Bump for every shipped zip, including a packaging-only change, so two machines can never
        // silently differ.
        public const string PluginVersion = "0.10.3";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            // Fully qualified: inside a BaseUnityPlugin, the bare name "Config" binds to the
            // inherited ConfigFile property, not to this mod's Config namespace.
            SailwindRigBalance.Config.Settings.Bind(base.Config);
            LogEnvironment();

            ApplyPatches();
            gameObject.AddComponent<UI.ShipyardGizmo>();

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        /// <summary>
        /// Applies each hook independently. Harmony's PatchAll aborts the whole batch on the first
        /// bad target, which is how a single ambiguous method name silently disabled everything
        /// downstream of it in 0.3.0. One patch failing should cost one feature, not all of them.
        /// </summary>
        private void ApplyPatches()
        {
            _harmony = new Harmony(PluginGUID);
            int applied = 0, failed = 0;

            foreach (Type type in typeof(Shipyard.ShipyardHooks).GetNestedTypes(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    applied++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogWarning($"[Init] hook '{type.Name}' did not apply: {e.Message}");
                }
            }

            if (failed == 0)
                Log.LogInfo($"[Init] {applied} shipyard hook(s) applied; rig model available={Core.RigModel.Available}");
            else
                Log.LogWarning($"[Init] {applied} shipyard hook(s) applied, {failed} failed; "
                    + $"the gizmo may not refresh on every edit. Rig model available={Core.RigModel.Available}");
        }

        /// <summary>
        /// Mod detection deliberately runs in Start, not Awake. Chainloader.PluginInfos is filled in
        /// as plugins load, so an Awake-time snapshot only sees the plugins that happened to load
        /// BEFORE us and reports the rest as absent. Start runs after every plugin's Awake, so the
        /// roster is complete regardless of load order. (Found in the step 1 log: SailInfo loaded
        /// one line after us and was therefore reported "not installed".)
        /// </summary>
        private void Start()
        {
            ModDetect.InitAndLog();
        }

        /// <summary>
        /// One-shot environment dump. This exists because two of the recon's conclusions are only
        /// valid against a specific build: the decompile that the force model was read from matches
        /// Sailwind 0.38.1, and the plugin must be loaded from exactly one path (a stale duplicate
        /// under a nested plugins folder has bitten this author's other mod before).
        /// </summary>
        private void LogEnvironment()
        {
            try
            {
                Log.LogInfo($"[Env] Game '{Application.productName}' version '{Application.version}', "
                    + $"Unity {Application.unityVersion}, platform {Application.platform}");

                var bepinex = typeof(BaseUnityPlugin).Assembly.GetName();
                Log.LogInfo($"[Env] BepInEx {bepinex.Version}");

                Log.LogInfo($"[Env] Loaded from: {GetType().Assembly.Location}");

                // Physics context the yaw-moment maths depends on. Measured from the shipped project
                // settings as 45.005 Hz (fixedDeltaTime 0.02222) with maximumDeltaTime 0.1; if these
                // differ at runtime, something else has changed them and the numbers move.
                Log.LogInfo($"[Env] Physics: fixedDeltaTime={Time.fixedDeltaTime:F5} "
                    + $"({(Time.fixedDeltaTime > 0f ? 1f / Time.fixedDeltaTime : 0f):F2} Hz), "
                    + $"maximumDeltaTime={Time.maximumDeltaTime:F3}, gravity={Physics.gravity.y:F2}, "
                    + $"solverIterations={Physics.defaultSolverIterations}");
            }
            catch (Exception e)
            {
                Log.LogWarning("[Env] Environment dump failed; continuing. " + e);
            }
        }
    }
}
