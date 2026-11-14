using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRigBalance.Core
{
    /// <summary>
    /// Checks the shipyard's heel against the real thing while sailing, so any session with the mod
    /// installed tests the solver without anyone setting out to.
    ///
    /// Aboard and under way, it averages two things over a minute: the boat's actual heel and pitch,
    /// and the moment her sails and hull windage are actually putting on her, rebuilt from the same
    /// fields Sail.ApplyForce and BoatWind.FixedUpdate use (Sail.cs:461-470, BoatWind.cs:25-33). Then
    /// it asks Hydrostatics what heel that moment should hold and logs the two side by side. Waves
    /// roll her about, which is what the average is for. A boat moving slower than a walking pace is
    /// skipped for heel, because one held by its moorings or anchor has rope forces this does not see,
    /// but still gets a line comparing how deep she sits, which is what the waterline is drawn from.
    ///
    /// Read only. It writes nothing to the boat or the sails.
    /// </summary>
    public class HeelCheck : MonoBehaviour
    {
        private const float WindowSeconds = 60f;
        private const float SampleSeconds = 0.25f;
        private const float MinSpeed = 1f;

        private static readonly FieldInfo FUnampFwd = AccessTools.Field(typeof(Sail), "unamplifiedForwardForce");
        private static readonly FieldInfo FUnampSide = AccessTools.Field(typeof(Sail), "unamplifiedSidewayForce");

        private Transform _boat;
        private float _nextSample;
        private float _windowStart;
        private int _samples;
        private Vector3 _momentSum;
        private float _heelSum, _pitchSum, _speedSum, _windSum, _heightSum;

        private void Update()
        {
            try
            {
                if (SailwindRigBalance.Config.Settings.LogHeelAtSea != null
                    && !SailwindRigBalance.Config.Settings.LogHeelAtSea.Value) { Reset(null); return; }

                if (Shipyard.ShipyardHooks.InShipyard || GameState.currentBoat == null || GameState.lastBoat == null)
                {
                    Reset(null);
                    return;
                }

                if (GameState.lastBoat != _boat) Reset(GameState.lastBoat);
                if (Time.time < _nextSample) return;
                _nextSample = Time.time + SampleSeconds;

                Sample();

                if (Time.time - _windowStart >= WindowSeconds) Report();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("[HeelCheck] skipped: " + e.Message);
                Reset(null);
            }
        }

        private void Reset(Transform boat)
        {
            _boat = boat;
            _samples = 0;
            _momentSum = Vector3.zero;
            _heelSum = _pitchSum = _speedSum = _windSum = _heightSum = 0f;
            _windowStart = Time.time;
        }

        private void Sample()
        {
            var body = _boat.GetComponent<Rigidbody>();
            var refs = _boat.GetComponent<BoatRefs>();
            if (body == null || refs == null || refs.masts == null || FUnampFwd == null || FUnampSide == null) return;

            Vector3 moment = Vector3.zero;
            foreach (Mast mast in refs.masts)
            {
                if (mast == null || mast.sails == null || !mast.gameObject.activeInHierarchy) continue;
                foreach (GameObject sailObject in mast.sails)
                {
                    if (sailObject == null) continue;
                    var sail = sailObject.GetComponent<Sail>();
                    if (sail == null || sail.windcenter == null || sail.shipRigidbody != body) continue;

                    // Sail.ApplyForce, Sail.cs:461-470: forward and side along the boat's own axes at
                    // windcenter, the side with its extra half again.
                    float power = sail.GetRealSailPower() * RigModel.CategoryMultiplier(sail.category) * 50f;
                    float fwd = (FUnampFwd.GetValue(sail) is float f ? f : 0f) * power;
                    float side = (FUnampSide.GetValue(sail) is float s ? s : 0f) * power * 1.5f;

                    Vector3 arm = _boat.InverseTransformDirection(sail.windcenter.position - _boat.position);
                    moment += Vector3.Cross(arm, new Vector3(side, 0f, fwd));
                }
            }

            // BoatWind.FixedUpdate, BoatWind.cs:25-33, its forward and side parts in boat axes.
            var windage = _boat.GetComponent<BoatWind>();
            Vector3 apparent = Wind.currentWind - body.velocity;
            if (windage != null)
            {
                float offBow = Mathf.Abs(Vector3.SignedAngle(_boat.forward, apparent.normalized, Vector3.up));
                if (offBow > 90f) offBow = 180f - offBow;
                float magnitude = Mathf.Lerp(windage.frontForce, windage.sideForce, Mathf.InverseLerp(0f, 90f, offBow));
                Vector3 force = _boat.InverseTransformDirection(apparent * magnitude);
                moment += Vector3.Cross(windage.forceOffset, new Vector3(force.x, 0f, force.z));
            }

            // Positive heel lifts starboard and positive pitch puts the bow down, as in SeaAttitude.
            float heel = Mathf.Asin(Mathf.Clamp(_boat.right.y, -1f, 1f)) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(_boat.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            // How high her origin rides over the mean sea, which is what the waterline is drawn from.
            // Waves move it about; a minute's average is the comparison.
            float sea = 0f;
            if (Crest.OceanRenderer.Instance != null) sea = Crest.OceanRenderer.Instance.SeaLevel;
            _heightSum += _boat.position.y - sea;

            _momentSum += moment;
            _heelSum += heel;
            _pitchSum += pitch;
            _speedSum += body.velocity.magnitude;
            _windSum += apparent.magnitude;
            _samples++;
        }

        private void Report()
        {
            int n = _samples;
            Transform boat = _boat;
            Vector3 moment = n > 0 ? _momentSum / n : Vector3.zero;
            float heel = n > 0 ? _heelSum / n : 0f;
            float pitch = n > 0 ? _pitchSum / n : 0f;
            float speed = n > 0 ? _speedSum / n : 0f;
            float wind = n > 0 ? _windSum / n : 0f;
            float height = n > 0 ? _heightSum / n : 0f;
            Reset(boat);

            if (n < 10 || boat == null) return;

            Hydrostatics.HullModel hull = Hydrostatics.Read(boat.gameObject, boat.GetComponent<Rigidbody>());
            if (hull == null) return;

            SeaAttitude predicted = Hydrostatics.Solve(hull, moment, Hydrostatics.Float(hull));

            // Slow or still, the heel is not worth comparing (ropes hold her), but how deep she sits
            // is, and a boat at rest in harbor is the steadiest reading of it there is.
            if (speed < MinSpeed)
            {
                Plugin.Log.LogInfo($"[HeelCheck] '{boat.name}' at rest over the last {WindowSeconds:F0} s: predicted "
                    + $"origin {predicted.OriginHeight:F2} m above the sea, actual {height:F2} m (the waterline's height; "
                    + $"mooring or anchor ropes can pull a little). {hull.Describe()}.");
                return;
            }
            Plugin.Log.LogInfo($"[HeelCheck] '{boat.name}' over the last {WindowSeconds:F0} s, "
                + $"{wind:F1} m/s apparent wind, {speed:F1} m/s boat speed: predicted heel "
                + $"{predicted.Heel:F1} deg{(predicted.Overpowered ? " (no balance)" : "")}, actual {heel:F1} deg; "
                + $"predicted pitch {predicted.Pitch:F1} deg, actual {pitch:F1} deg; predicted origin "
                + $"{predicted.OriginHeight:F2} m above the sea, actual {height:F2} m (the waterline's height). "
                + $"Moment ({moment.x:F0}, {moment.y:F0}, {moment.z:F0}) Nm, {hull.Describe()}.");
        }
    }
}
