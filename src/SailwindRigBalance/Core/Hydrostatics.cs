using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRigBalance.Core
{
    /// <summary>
    /// How the boat would sit in flat water under a steady wind: how deep, and how far heeled and
    /// pitched.
    /// </summary>
    public struct SeaAttitude
    {
        /// <summary>
        /// True when this came from the hull's own buoyancy. False is the upright reading the mod
        /// always gave before heel was modeled, with the boat origin taken as the waterline.
        /// </summary>
        public bool Valid;

        /// <summary>Degrees about the boat's fore-and-aft axis, Unity's Euler z. Positive lifts starboard.</summary>
        public float Heel;

        /// <summary>Degrees about the boat's athwartships axis, Unity's Euler x. Positive puts the bow down.</summary>
        public float Pitch;

        /// <summary>How far the boat origin sits above the sea, in meters. Negative is below it.</summary>
        public float OriginHeight;

        /// <summary>
        /// No balance was found before the heel limit: the wind lays her over further than the model
        /// follows. The angles are left at the limit.
        /// </summary>
        public bool Overpowered;

        /// <summary>The boat's rotation in a frame whose yaw is the boat's own heading.</summary>
        public Quaternion Rotation => Quaternion.Euler(Pitch, 0f, Heel);

        /// <summary>
        /// Height of a point above the sea, given as an offset from the boat origin in the boat's own
        /// axes (meters, not scaled).
        /// </summary>
        public float HeightOf(Vector3 offsetLocal) => OriginHeight + (Rotation * offsetLocal).y;
    }

    /// <summary>
    /// Crest's buoyancy and the boat's weight, read off the live components, and the static balance
    /// they reach against the sails.
    ///
    /// The game floats a boat on Crest.BoatProbes.FixedUpdateBuoyancy (Crest/BoatProbes.cs:186-201):
    /// a dozen points, each pushing straight up with 1000 * g * depth * weight * _forceMultiplier /
    /// _totalWeight newtons, depth being how far the point is below the water. That is a set of
    /// linear springs, so a static balance can be worked out exactly rather than estimated. Against
    /// them is the rigidbody's weight at Rigidbody.worldCenterOfMass, which BoatMass keeps up to date
    /// with cargo and with the player aboard (BoatMass.cs:66-117).
    ///
    /// The sails push along the boat's own forward and right axes at their windcenters
    /// (Sail.cs:461-470), and the hull's water drag pushes back along the same axes at the boat
    /// origin (BoatProbes.cs:220-223, _forceHeightOffset is 0 on every hull, FINDINGS section 3). In
    /// a steady state those cancel, so the forces sum to zero and moments can be taken about any
    /// point. The origin is used because the drag has no moment about it. What the sails leave over
    /// is then a moment in the boat's frame: their side force at a height heels her, their drive at a
    /// height pitches her, and their yaw is what the helm holds.
    ///
    /// The rudder (Rudder.cs:52-71) is a pure torque about the boat's own up axis. Buoyancy and
    /// weight are both vertical, so neither can resist any turn about the world vertical, and once
    /// the boat heels the rudder has to cancel every part of the sails' moment that points that way,
    /// not just their yaw about the boat's mast. That extra part is the drive acting off to leeward,
    /// and it is the classic reason a heeled boat carries more weather helm.
    /// </summary>
    public static class Hydrostatics
    {
        private static readonly FieldInfo FCrestCenterOfMass =
            AccessTools.Field(typeof(Crest.BoatProbes), "_centerOfMass");
        private static readonly FieldInfo FTotalWeight =
            AccessTools.Field(typeof(Crest.BoatProbes), "_totalWeight");

        /// <summary>Further than this the model does not follow her over. Past it she is laid flat.</summary>
        public const float HeelLimit = 70f;
        private const float PitchLimit = 30f;

        /// <summary>One boat's buoyancy and weight, in the boat's own axes about its origin.</summary>
        public sealed class HullModel
        {
            public Vector3[] Points;
            public float[] Stiffness;       // N per meter of depth, per point
            public float Mass;
            public Vector3 CenterOfMass;
            public float Gravity;
            public float Weight => Mass * Gravity;

            /// <summary>The half span of the probe set fore and aft, to scale the moment residuals.</summary>
            public float Reach;

            /// <summary>A short description for the log, so a bad heel can be traced to its inputs.</summary>
            public string Describe() => $"{Points.Length} buoyancy points, {Mass:F0} kg, center of mass "
                + $"({CenterOfMass.x:F2}, {CenterOfMass.y:F2}, {CenterOfMass.z:F2}) m, "
                + $"{TotalStiffness / 1000f:F0} kN per meter of sinkage";

            public float TotalStiffness
            {
                get
                {
                    float sum = 0f;
                    for (int i = 0; i < Stiffness.Length; i++) sum += Stiffness[i];
                    return sum;
                }
            }
        }

        /// <summary>
        /// The sideways position of the buoyancy points' stiffness-weighted center, in the boat's
        /// axes. A center of mass right above it floats upright with no wind.
        /// </summary>
        public static float BuoyancyCenterX(HullModel hull)
        {
            float sum = 0f, weighted = 0f;
            for (int i = 0; i < hull.Points.Length; i++)
            {
                sum += hull.Stiffness[i];
                weighted += hull.Stiffness[i] * hull.Points[i].x;
            }
            return sum > 0f ? weighted / sum : 0f;
        }

        private static readonly HashSet<string> Warned = new HashSet<string>();

        private static void WarnOnce(string key, string message)
        {
            if (Warned.Add(key)) Plugin.Log.LogWarning(message);
        }

        /// <summary>
        /// Reads the boat's buoyancy and weight off its live components, or null when they are not
        /// there or make no sense, in which case heel is simply not modeled for this boat.
        /// </summary>
        public static HullModel Read(GameObject boatRoot, Rigidbody body)
        {
            try
            {
                if (boatRoot == null || body == null) return null;

                var probes = boatRoot.GetComponent<Crest.BoatProbes>();
                if (probes == null || probes._forcePoints == null || probes._forcePoints.Length < 3)
                {
                    WarnOnce("probes", "[Heel] this boat has no Crest buoyancy points, so heel is not "
                        + "modeled and the reading is for an upright boat.");
                    return null;
                }

                Transform boat = boatRoot.transform;

                // Crest raises every query point by its own _centerOfMass.y (BoatProbes.cs:162). That
                // is Crest's private field, not the rigidbody's center of mass, which BoatMass
                // overwrites. The offsets are already scaled by customScale, which Start applies in
                // place (BoatProbes.cs:96-103).
                float lift = 0f;
                if (FCrestCenterOfMass != null && FCrestCenterOfMass.GetValue(probes) is Vector3 crestCom)
                    lift = crestCom.y;

                // Vanilla divides by the total it cached in Start (BoatProbes.cs:120, :129-137), not by
                // a live sum. Shipyard Expansion's outrigger zeroes weights after that, so the cached
                // total is what the game actually uses.
                float total = 0f;
                if (FTotalWeight != null && FTotalWeight.GetValue(probes) is float cached) total = cached;
                if (total <= 0f)
                    foreach (Crest.FloaterForcePoints p in probes._forcePoints)
                        if (p != null) total += p._weight;
                if (total <= 0f) return null;

                float g = Mathf.Abs(Physics.gravity.y);
                var points = new List<Vector3>();
                var stiffness = new List<float>();
                float minZ = float.MaxValue, maxZ = float.MinValue;

                foreach (Crest.FloaterForcePoints p in probes._forcePoints)
                {
                    if (p == null || p._weight <= 0f) continue;

                    // BoatProbes.cs:162, then into the boat's rotation frame in meters, so a scaled
                    // boat root is measured the way physics sees it.
                    Vector3 world = boat.TransformPoint(p._offsetPosition + new Vector3(0f, lift, 0f));
                    Vector3 local = boat.InverseTransformDirection(world - boat.position);
                    points.Add(local);
                    stiffness.Add(1000f * g * p._weight * probes._forceMultiplier / total);
                    minZ = Mathf.Min(minZ, local.z);
                    maxZ = Mathf.Max(maxZ, local.z);
                }

                if (points.Count < 3 || body.mass <= 0f) return null;

                var hull = new HullModel
                {
                    Points = points.ToArray(),
                    Stiffness = stiffness.ToArray(),
                    Mass = body.mass,
                    CenterOfMass = boat.InverseTransformDirection(body.worldCenterOfMass - boat.position),
                    Gravity = g,
                    Reach = Mathf.Max((maxZ - minZ) * 0.5f, 1f),
                };

                if (hull.TotalStiffness <= 0f || float.IsNaN(hull.TotalStiffness)) return null;
                return hull;
            }
            catch (Exception e)
            {
                WarnOnce("read", "[Heel] could not read the hull's buoyancy, so the reading is for an "
                    + "upright boat: " + e.Message);
                return null;
            }
        }

        /// <summary>How she floats with no wind: level, at the depth where buoyancy carries her weight.</summary>
        public static SeaAttitude Float(HullModel hull)
        {
            var attitude = new SeaAttitude { Valid = hull != null };
            if (hull == null) return attitude;

            // Buoyancy only falls as the origin rises, so the height that carries the weight is a
            // single crossing and halving finds it.
            float low = -200f, high = 200f;
            for (int i = 0; i < 60; i++)
            {
                float mid = (low + high) * 0.5f;
                if (Buoyancy(hull, mid, Quaternion.identity) > hull.Weight) low = mid;
                else high = mid;
            }
            attitude.OriginHeight = (low + high) * 0.5f;

            // A crossing at the edge of the search is no crossing: the points cannot carry her weight
            // at any depth, or carry it with her floating in the air. Either way the buoyancy read is
            // not something to heel her on, and the reading falls back to upright.
            if (Mathf.Abs(attitude.OriginHeight) > 50f)
            {
                WarnOnce("float", $"[Heel] the hull's buoyancy puts her origin {attitude.OriginHeight:F0} m "
                    + $"from the sea ({hull.Describe()}), which is not a floating boat, so heel is not modeled.");
                attitude.Valid = false;
            }
            return attitude;
        }

        /// <summary>
        /// The attitude where buoyancy, weight and the given sail moment balance, starting the search
        /// from <paramref name="from"/>.
        /// </summary>
        /// <param name="sailMoment">
        /// The sails' (and the hull windage's) moment about the boat origin, in the boat's own axes,
        /// newton meters, at the wind speed being judged.
        /// </param>
        public static SeaAttitude Solve(HullModel hull, Vector3 sailMoment, SeaAttitude from)
        {
            if (hull == null) return from;
            if (!from.Valid)
            {
                // A hull Float cannot place has no balance to search from either: say so rather
                // than iterate from the edge of the depth search and report it as found.
                from = Float(hull);
                if (!from.Valid) return from;
            }

            float h = from.OriginHeight;
            float heel = Mathf.Clamp(from.Heel, -HeelLimit, HeelLimit);
            float pitch = Mathf.Clamp(from.Pitch, -PitchLimit, PitchLimit);

            const float StepH = 0.002f, StepAngle = 0.02f;
            var r = Residual(hull, sailMoment, h, heel, pitch);

            for (int iteration = 0; iteration < 60; iteration++)
            {
                if (r.sqrMagnitude < 1e-12f) break;

                // Central differences, because the springs switch off as points leave the water and a
                // one-sided step straddling that kink gives a slope from only one side of it.
                Vector3 dH = (Residual(hull, sailMoment, h + StepH, heel, pitch)
                    - Residual(hull, sailMoment, h - StepH, heel, pitch)) / (2f * StepH);
                Vector3 dHeel = (Residual(hull, sailMoment, h, heel + StepAngle, pitch)
                    - Residual(hull, sailMoment, h, heel - StepAngle, pitch)) / (2f * StepAngle);
                Vector3 dPitch = (Residual(hull, sailMoment, h, heel, pitch + StepAngle)
                    - Residual(hull, sailMoment, h, heel, pitch - StepAngle)) / (2f * StepAngle);

                Vector3 step;
                if (!SolveLinear(dH, dHeel, dPitch, -r, out step)) break;

                // Held to small moves, so a step that lands past a point leaving the water cannot fling
                // the search somewhere the springs mean something else, and then backtracked until the
                // residual actually falls.
                float scale = 1f;
                float largest = Mathf.Max(Mathf.Abs(step.y), Mathf.Abs(step.z));
                if (largest > 4f) scale = 4f / largest;
                if (Mathf.Abs(step.x) * scale > 0.25f) scale = 0.25f / Mathf.Abs(step.x);

                bool improved = false;
                for (int backtrack = 0; backtrack < 8; backtrack++)
                {
                    float nh = h + step.x * scale;
                    float nHeel = Mathf.Clamp(heel + step.y * scale, -HeelLimit, HeelLimit);
                    float nPitch = Mathf.Clamp(pitch + step.z * scale, -PitchLimit, PitchLimit);
                    Vector3 nr = Residual(hull, sailMoment, nh, nHeel, nPitch);
                    if (nr.sqrMagnitude < r.sqrMagnitude)
                    {
                        h = nh; heel = nHeel; pitch = nPitch; r = nr;
                        improved = true;
                        break;
                    }
                    scale *= 0.5f;
                }
                if (!improved) break;
            }

            // A balance that was found has a residual of a few millionths. One that stalls is the boat
            // past her greatest righting moment: points on the high side have left the water, every
            // further degree rights her less, and there is no angle where she stops. Measured on a port
            // of this solver, a stall leaves a residual of a few thousandths or more.
            bool atLimit = Mathf.Abs(heel) >= HeelLimit - 0.01f || Mathf.Abs(pitch) >= PitchLimit - 0.01f;
            return new SeaAttitude
            {
                Valid = true,
                OriginHeight = h,
                Heel = heel,
                Pitch = pitch,
                Overpowered = atLimit || r.sqrMagnitude > 4e-6f,
            };
        }

        /// <summary>
        /// The share of the sails' moment the rudder must hold, expressed as a yaw about the boat's own
        /// up axis, which is the axis Rudder.cs applies its torque about.
        /// </summary>
        public static float RudderYaw(Vector3 sailMoment, SeaAttitude attitude)
        {
            Quaternion q = attitude.Rotation;
            float upright = (q * Vector3.up).y;
            if (upright < 0.05f) upright = 0.05f;
            return (q * sailMoment).y / upright;
        }

        /// <summary>
        /// Out of balance, in units that make the three parts comparable: vertical force as a share of
        /// the boat's weight, and the two righting moments as a share of her weight acting at the end
        /// of the hull.
        /// </summary>
        private static Vector3 Residual(HullModel hull, Vector3 sailMoment, float h, float heel, float pitch)
        {
            Quaternion q = Quaternion.Euler(pitch, 0f, heel);
            float weight = hull.Weight;

            float lift = 0f;
            Vector3 torque = Vector3.zero;
            for (int i = 0; i < hull.Points.Length; i++)
            {
                Vector3 arm = q * hull.Points[i];
                float depth = -(h + arm.y);
                if (depth <= 0f) continue;

                float force = hull.Stiffness[i] * depth;
                lift += force;
                torque += Vector3.Cross(arm, new Vector3(0f, force, 0f));
            }

            torque += Vector3.Cross(q * hull.CenterOfMass, new Vector3(0f, -weight, 0f));

            // The sails' moment, plus the rudder holding whatever of it turns her about the vertical.
            // Buoyancy and weight have no vertical moment at all, so the rudder takes all of that and
            // only its sideways leftovers, from its axis being heeled, reach the balance below.
            Vector3 sails = q * sailMoment;
            Vector3 up = q * Vector3.up;
            float rudder = -sails.y / Mathf.Max(up.y, 0.05f);
            torque += sails + up * rudder;

            float momentScale = weight * hull.Reach;
            return new Vector3((lift - weight) / weight, torque.z / momentScale, torque.x / momentScale);
        }

        /// <summary>Total buoyancy with the origin at this height and the boat at this rotation.</summary>
        private static float Buoyancy(HullModel hull, float h, Quaternion q)
        {
            float lift = 0f;
            for (int i = 0; i < hull.Points.Length; i++)
            {
                float depth = -(h + (q * hull.Points[i]).y);
                if (depth > 0f) lift += hull.Stiffness[i] * depth;
            }
            return lift;
        }

        /// <summary>Solves [a b c] x = d for a three by three system by Cramer's rule.</summary>
        private static bool SolveLinear(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 x)
        {
            x = Vector3.zero;
            float det = Vector3.Dot(a, Vector3.Cross(b, c));
            if (Mathf.Abs(det) < 1e-12f || float.IsNaN(det)) return false;

            x = new Vector3(
                Vector3.Dot(d, Vector3.Cross(b, c)) / det,
                Vector3.Dot(a, Vector3.Cross(d, c)) / det,
                Vector3.Dot(a, Vector3.Cross(b, d)) / det);
            return !(float.IsNaN(x.x) || float.IsNaN(x.y) || float.IsNaN(x.z));
        }
    }
}
