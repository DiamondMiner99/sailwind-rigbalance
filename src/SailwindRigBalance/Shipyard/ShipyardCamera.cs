using System;
using System.Collections.Generic;
using HarmonyLib;
using SailwindRigBalance.Compat;
using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.Shipyard
{
    /// <summary>
    /// Widens the shipyard camera so a tall rig can be seen to the masthead.
    ///
    /// Vanilla's shipyard view is BoatCamera orbiting a point at the boat's origin (BoatCamera.cs
    /// Update: position = currentBoat.position + currentPosOffset, eye at camHeight up and zoomLevel
    /// back). Right-drag pans that point sideways only (Mouse X along the camera's right, clamped to
    /// 25 m either way), and UpdateZoom clamps the distance to 8..40 m. Nothing moves the point up,
    /// and pitching up to look at the tops swings the eye down under the sea, so on a big rig the
    /// upper sails cannot be framed at all.
    ///
    /// Two additions, shipyard only:
    ///  - Right-drag up and down lifts the orbit point, from the deck up to the top of the rig. It is
    ///    written into vanilla's own currentPosOffset.y, which vanilla leaves alone (its pan only
    ///    clamps x and z), so the lift lives where the camera already reads its offset and
    ///    SwitchOn's reset of that offset clears it on the next visit.
    ///  - Zoom reaches out to MaxZoomOutMeters instead of 40.
    ///
    /// Each steps aside for a mod that already does the same thing, rather than doubling it:
    ///  - NANDTweaks' camera tweak lifts the same offset with the same right-drag, -5..15 m
    ///    (NANDTweaks CameraPatches.cs CameraPatch). Both adding to it meant four times the rate
    ///    and its floor 5 m below the deck. Its 15 m ceiling is also short of a brig's masthead,
    ///    so rather than step aside the lift is owned outright: the offset's height is taken
    ///    before the camera's update, and after every other postfix (HarmonyAfter NANDTweaks) it
    ///    is set from that plus this mod's own step, so whatever NANDTweaks added is replaced.
    ///  - HMS Leopard replaces UpdateZoom everywhere, out to 80 m (Patch_UpdateZoom.cs). Harmony
    ///    runs every prefix even after one has skipped the original, so both stepped the zoom on
    ///    each scroll. With it installed, the zoom is left to it.
    ///
    /// Whatever moves the eye, it is kept above the sea: pitched up at a long zoom, vanilla's
    /// geometry puts it under the water.
    /// </summary>
    public static class ShipyardCamera
    {
        /// <summary>Vanilla's nearest zoom, kept as is.</summary>
        private const float NearestZoom = -8f;

        /// <summary>Per-frame mouse factor, the same as vanilla's sideways pan.</summary>
        private const float LiftPerMouseUnit = 3f;

        /// <summary>How fast the lift settles down to a lower top, when a tall sail or mast goes.</summary>
        private const float LiftSettleMetersPerSecond = 12f;

        /// <summary>How far above the sea the eye is kept.</summary>
        private const float EyeClearance = 1f;

        /// <summary>How often the rig's top is measured again, since sails come and go in the shipyard.</summary>
        private const float TopRefreshSeconds = 1f;

        private static readonly AccessTools.FieldRef<BoatCamera, float> ZoomLevel =
            AccessTools.FieldRefAccess<BoatCamera, float>("zoomLevel");
        private static readonly AccessTools.FieldRef<BoatCamera, float> ZoomSpeed =
            AccessTools.FieldRefAccess<BoatCamera, float>("zoomSpeed");
        private static readonly AccessTools.FieldRef<BoatCamera, Vector3> PosOffset =
            AccessTools.FieldRefAccess<BoatCamera, Vector3>("currentPosOffset");
        private static readonly AccessTools.FieldRef<BoatCamera, Transform> CenterEye =
            AccessTools.FieldRefAccess<BoatCamera, Transform>("centerEye");

        private static float _top;
        private static float _topMeasuredAt = float.NegativeInfinity;
        private static GameObject _boat;
        private static bool _failed;
        private static readonly List<Renderer> Renderers = new List<Renderer>();

        private static bool Active =>
            !_failed
            && Config.Settings.ExtendShipyardCamera.Value
            && BoatCamera.on
            && GameState.currentShipyard != null
            && GameState.currentBoat != null;

        [HarmonyPatch(typeof(BoatCamera), "Update")]
        [HarmonyAfter(ModDetect.NandTweaksGuid)]
        private static class UpdatePatch
        {
            /// <summary>The lift as it stood before this frame's update. Vanilla never writes it.</summary>
            private static void Prefix(BoatCamera __instance)
            {
                try
                {
                    _liftBefore = PosOffset(__instance).y;
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }

            private static void Postfix(BoatCamera __instance)
            {
                try
                {
                    if (!Active)
                    {
                        // Switched off mid-visit (the setting, or a failure) with the view lifted:
                        // vanilla never moves the height, so without this the view would stay up
                        // there until the next visit.
                        if (_lifted && BoatCamera.on && GameState.currentShipyard != null)
                            PosOffset(__instance).y = 0f;
                        _lifted = false;
                        Reset();
                        return;
                    }

                    GameObject boat = ShipyardHooks.CurrentBoat;
                    if (boat != _boat)
                    {
                        Reset();
                        _boat = boat;
                    }

                    if (Time.unscaledTime - _topMeasuredAt > TopRefreshSeconds)
                    {
                        _top = MeasureTop(GameState.currentBoat);
                        _topMeasuredAt = Time.unscaledTime;
                    }

                    ref Vector3 offset = ref PosOffset(__instance);
                    NoteNandTweaks(offset.y, _liftBefore);
                    float lift = _liftBefore;
                    if (Input.GetMouseButton(1))
                        lift += Input.GetAxis("Mouse Y") * LiftPerMouseUnit;

                    // Up to the top and no lower than the deck. A top that drops while the view is
                    // up there, a tall sail or mast taken off, is followed down at a walk rather
                    // than in one jump.
                    if (lift > _top)
                        lift = Mathf.Max(_top, _liftBefore - LiftSettleMetersPerSecond * Time.unscaledDeltaTime);
                    offset.y = Mathf.Max(lift, 0f);
                    _lifted = offset.y > 0f;

                    KeepEyeAboveSea(__instance);
                }
                catch (Exception e)
                {
                    Fail(e);
                }
            }
        }

        [HarmonyPatch(typeof(BoatCamera), "UpdateZoom")]
        private static class UpdateZoomPatch
        {
            private static bool Prefix(BoatCamera __instance)
            {
                try
                {
                    if (_failed || !Config.Settings.ExtendShipyardCamera.Value || GameState.currentShipyard == null)
                        return true;
                    if (ModDetect.IsLoaded(ModDetect.LeopardGuid))
                        return true;

                    // Vanilla's own step, with the far limit moved out.
                    ref float zoom = ref ZoomLevel(__instance);
                    zoom += GameInput.GetScrollAxis() * ZoomSpeed(__instance);
                    zoom = Mathf.Clamp(zoom, -Config.Settings.MaxZoomOut.Value, NearestZoom);
                    return false;
                }
                catch (Exception e)
                {
                    Fail(e);
                    return true;
                }
            }
        }

        /// <summary>
        /// Vanilla has just placed the eye (centerEye at camHeight up and zoomLevel back from the
        /// orbit point, turned by the camera's pitch). Pitched up at a long zoom that is below the
        /// water; when it is, the orbit point is raised for this frame by the shortfall. Vanilla
        /// rebuilds the position from the boat every frame before this runs, so nothing accumulates.
        /// </summary>
        private static void KeepEyeAboveSea(BoatCamera camera)
        {
            Transform eye = CenterEye(camera);
            if (eye == null) return;

            float sea = 0f;
            if (Crest.OceanRenderer.Instance != null) sea = Crest.OceanRenderer.Instance.SeaLevel;

            float shortfall = sea + EyeClearance - eye.position.y;
            if (shortfall > 0f) camera.transform.position += Vector3.up * shortfall;
        }

        /// <summary>
        /// Says once, the first time another patch is seen moving the lift (NANDTweaks' camera
        /// tweak), that this mod's lift replaces it in the shipyard.
        /// </summary>
        private static void NoteNandTweaks(float after, float before)
        {
            if (_notedOther || Mathf.Abs(after - before) < 0.0001f) return;
            _notedOther = true;
            Plugin.Log.LogInfo("[Camera] another mod also moves the shipyard view up and down"
                + (ModDetect.IsLoaded(ModDetect.NandTweaksGuid) ? " (NANDTweaks' camera tweak)" : "")
                + "; with ExtendShipyardCamera on, this mod's lift replaces it, from the deck to the top "
                + "of the rig.");
        }

        private static bool _notedOther;
        private static bool _lifted;
        private static float _liftBefore;

        private static void Reset()
        {
            _boat = null;
            _topMeasuredAt = float.NegativeInfinity;
        }

        /// <summary>
        /// How high above the boat's origin its highest visible part reaches: the masthead, or a
        /// topmast's truck. Renderers rather than colliders, because a sail's own collider can sit on
        /// a different layer or be switched off, and the camera only cares about what can be seen.
        /// Anything not boat-sized is skipped by the same rule the model uses (RigModel.BoatSized):
        /// Shipyard Expansion parts that sit kilometers away would otherwise set the top.
        /// </summary>
        private static float MeasureTop(Transform boat)
        {
            float origin = boat.position.y;
            float top = 0f;
            boat.GetComponentsInChildren(false, Renderers);
            foreach (Renderer r in Renderers)
            {
                if (!r.enabled) continue;
                Bounds b = r.bounds;
                if (!RigModel.BoatSized(b.size, b.center, boat)) continue;
                top = Mathf.Max(top, b.max.y - origin);
            }
            Renderers.Clear();

            if (Mathf.Abs(top - _loggedTop) > 0.5f)
            {
                _loggedTop = top;
                Plugin.Log.LogInfo($"[Camera] the shipyard view can lift up to {top:F1} m, the top of "
                    + $"'{boat.name}'.");
            }
            return top;
        }

        private static float _loggedTop = float.NegativeInfinity;

        /// <summary>
        /// Switches the extension off for the rest of the session and says so. Without that the
        /// same exception would recur every frame while the message claimed vanilla had taken over.
        /// </summary>
        private static void Fail(Exception e)
        {
            if (_failed) return;
            _failed = true;
            Plugin.Log.LogWarning($"[Camera] shipyard camera extension failed and is off until the game "
                + $"restarts; the camera is the game's own: {e}");
        }
    }
}
