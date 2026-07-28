using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRigBalance.Core
{
    /// <summary>One sail's contribution to the balance, in the boat's own frame.</summary>
    public struct SailContribution
    {
        public Sail Sail;
        public string Name;
        public Vector3 CentreOfEffort;   // world
        public float HeightAboveWaterline;
        public float ForwardForce;       // N
        public float SideForce;          // N
        public float LeverFromOrigin;    // m, positive forward
        public float YawMoment;          // Nm, positive turns the bow to starboard
        public bool CeTracksResizes;     // false only for a Shipyard Expansion added sail
        public float Unroll;             // what the model actually evaluated this sail at

        // Sail.cs:415 drags a sail's force from its full value at 90 degrees down toward
        // totalWindForce * upwindEfficiency as the apparent wind closes on the bow. Sails with
        // different efficiencies therefore lose force at different rates coming up on the wind,
        // which is the mechanism that can move a rig's balance with point of sail.
        public float UpwindEfficiency;
        public SailCategory Category;
    }

    /// <summary>The whole answer for one rig at one synthetic wind.</summary>
    public struct RigBalance
    {
        public List<SailContribution> Sails;
        public float TotalSideForce;
        public float TotalForwardForce;
        public float YawMoment;          // about the hull origin
        public float SignedHelm;         // positive = weather helm
        public Vector3 CombinedCentreOfEffort;
        public Vector3 HullOrigin;
        public bool AnySailHasFrozenCe;
        public float HullLength;         // m, along the boat's own fore-aft axis
        public float WindAngle;          // degrees off the bow this was evaluated at

        /// <summary>
        /// False only when the rig is producing essentially no force at all, which happens inside
        /// about 16 degrees of the wind where Sail.cs:416 ramps every sail to nothing.
        /// </summary>
        public bool HelmMeaningful;

        /// <summary>
        /// The yaw moment expressed as a lever arm, in metres, positive for weather helm.
        ///
        /// Divided by the rig's TOTAL output rather than by its side force alone. Dividing by side
        /// force is the textbook centre-of-effort-against-centre-of-resistance picture and it works
        /// beautifully on the wind, but it has two failures this rig exposed. Running, the side
        /// force goes to zero and the division explodes, reporting metres of imaginary lee helm.
        /// And side force is signed, so the verdict could inverbut  purely because the denominator
        /// changed sign, which on a square rigger with backed sails it does.
        ///
        /// A square sail pushing forward from a point off the centreline still yaws the boat: the
        /// log shows yaw of -1366 with a side force of exactly zero. That is a real handling
        /// problem on the point of sail a square rigger lives on, and the old divisor could not see
        /// it. Total output is always positive, never vanishes while the sails draw, and reduces to
        /// the same number when the force is mostly sideways.
        /// </summary>
        public float HelmMetres;
    }

    /// <summary>
    /// Computes a rig's yaw balance by driving the game's own force function with a synthetic
    /// apparent wind, rather than reimplementing it.
    ///
    /// Two things make this valid in a shipyard, where there is no useful wind and the sails
    /// cannot swing:
    ///  - Sail.UpdateWindForceOnSail takes no arguments, reads the wind from the public mutable
    ///    field Sail.apparentWind, and contains no AddForce of any kind. So it can be driven with a
    ///    fake wind and read back without touching physics. Everything it writes is saved and
    ///    restored.
    ///  - The sail cannot trim itself (its rigidbody is kinematic at a shipyard), so the pose is
    ///    imposed: each sail is evaluated at both ends of its own swing limits and the one giving
    ///    the most drive is kept. That is what trimming for best drive means, and it avoids
    ///    guessing a hinge sign convention.
    ///
    /// The moment is taken about the HULL ORIGIN, not the centre of mass. In lateral-force
    /// equilibrium the hull's lateral drag exactly opposes the sails' side force and acts at the
    /// origin (Crest applies it at rb.position, and _forceHeightOffset is 0 on every hull), so the
    /// two side-force terms collapse to a single lever about that point. That is what makes a
    /// pre-purchase number possible: it needs no leeway and no wind speed.
    /// </summary>
    public static class RigModel
    {
        private static readonly FieldInfo FUnampFwd = AccessTools.Field(typeof(Sail), "unamplifiedForwardForce");
        private static readonly FieldInfo FUnampSide = AccessTools.Field(typeof(Sail), "unamplifiedSidewayForce");
        private static readonly FieldInfo FTotalWind = AccessTools.Field(typeof(Sail), "totalWindForce");
        private static readonly FieldInfo FRelAngle = AccessTools.Field(typeof(Sail), "relativeWindAngle");
        private static readonly FieldInfo FOutHeightMult = AccessTools.Field(typeof(Sail), "outHeightMult");
        private static readonly FieldInfo FOutFwdShare = AccessTools.Field(typeof(Sail), "outForwardShare");
        private static readonly FieldInfo FOutSideShare = AccessTools.Field(typeof(Sail), "outSidewayShare");
        private static readonly FieldInfo FPushingBack = AccessTools.Field(typeof(Sail), "pushingBoatBackwards");
        private static readonly MethodInfo MUpdateForce = AccessTools.Method(typeof(Sail), "UpdateWindForceOnSail");

        /// <summary>True when every private member the model drives resolved.</summary>
        public static bool Available =>
            FUnampFwd != null && FUnampSide != null && MUpdateForce != null;

        /// <summary>
        /// Apparent wind speed the synthetic evaluation uses. The sim's force is LINEAR in wind
        /// speed (totalWindForce starts as apparentWind.magnitude), so this scales the whole answer
        /// without changing its sign or the balance between sails. 10 m/s is a brisk working
        /// breeze and keeps the printed numbers in a readable range.
        /// </summary>
        public const float SyntheticWindSpeed = 10f;

        /// <summary>
        /// Evaluate a rig at an apparent wind coming from <paramref name="windAngleDegrees"/> off
        /// the bow, on the port bow (so positive yaw means the bow turns away from the wind).
        /// 45 is close hauled, 90 is a beam reach.
        /// </summary>
        /// <param name="overrideSail">
        /// Optional. Evaluate as if this one sail were set to <paramref name="overrideUnroll"/>
        /// instead of its real furl state, leaving every other sail alone. This is what draws the
        /// reef track: run it once as things stand and once with the selected sail at the opposite
        /// extreme, and the two combined centres bound a straight segment covering every reef
        /// setting in between. Straight because Sail.cs:415 scales the force linearly and nothing
        /// in the reef path moves that sail's own windcenter, so only its weight in the average
        /// changes, and a weighted average with one weight swept traces a line.
        /// </param>
        public static bool TryEvaluate(GameObject boatRoot, float windAngleDegrees, out RigBalance result,
            Sail overrideSail = null, float overrideUnroll = 1f)
        {
            result = default(RigBalance);
            if (boatRoot == null || !Available) return false;

            var refs = boatRoot.GetComponent<BoatRefs>();
            var body = boatRoot.GetComponent<Rigidbody>();
            if (refs == null || body == null || refs.masts == null) return false;

            Transform boat = boatRoot.transform;

            // Wind on the PORT bow: blowing toward starboard (+x) and aft (-z). With the wind on
            // port, leeward is starboard, so a positive yaw (bow to starboard) is the bow falling
            // AWAY from the wind, i.e. lee helm. SignedHelm flips that so positive reads as weather.
            float rad = windAngleDegrees * Mathf.Deg2Rad;
            Vector3 windLocal = new Vector3(Mathf.Sin(rad), 0f, -Mathf.Cos(rad)) * SyntheticWindSpeed;
            Vector3 windWorld = boat.TransformVector(windLocal);

            var sails = new List<SailContribution>();
            float totalSide = 0f, totalFwd = 0f, totalYaw = 0f;
            float weightSum = 0f;
            Vector3 ceAccum = Vector3.zero;
            bool anyFrozen = false;

            // BoatRefs.masts is a sparse array of length 31 indexed by Mast.orderIndex.
            foreach (Mast mast in refs.masts)
            {
                if (mast == null || mast.sails == null) continue;
                if (!mast.gameObject.activeInHierarchy) continue;

                foreach (GameObject sailObject in mast.sails)
                {
                    if (sailObject == null) continue;
                    var sail = sailObject.GetComponent<Sail>();
                    if (sail == null || sail.windcenter == null) continue;

                    SailContribution c;
                    float unroll = sail == overrideSail ? overrideUnroll : sail.currentUnroll;
                    if (!TryEvaluateSail(sail, boat, body, windWorld, unroll, out c)) continue;

                    // A sail producing no force cannot affect the balance by definition, and on a
                    // brig three of them turn up parked at the boat origin, presumably not yet
                    // placed on a mast. They were already weighted zero; dropping them here keeps
                    // the sail count and the log honest.
                    if (Mathf.Abs(c.ForwardForce) + Mathf.Abs(c.SideForce) < 0.001f) continue;

                    sails.Add(c);
                    totalSide += c.SideForce;
                    totalFwd += c.ForwardForce;
                    totalYaw += c.YawMoment;
                    if (!c.CeTracksResizes) anyFrozen = true;

                    float w = Mathf.Abs(c.SideForce) + Mathf.Abs(c.ForwardForce);
                    ceAccum += c.CentreOfEffort * w;
                    weightSum += w;
                }
            }

            result.Sails = sails;
            result.TotalSideForce = totalSide;
            result.TotalForwardForce = totalFwd;
            result.YawMoment = totalYaw;
            // Wind is on the port bow, so weather helm is the bow turning to port, i.e. negative yaw.
            result.SignedHelm = -totalYaw;
            result.HullOrigin = body.position;
            result.CombinedCentreOfEffort = weightSum > 0.0001f ? ceAccum / weightSum : body.position;
            result.AnySailHasFrozenCe = anyFrozen;
            result.HullLength = MeasureHullLength(boatRoot, boat);
            result.WindAngle = windAngleDegrees;

            // Side force has to be a real share of what the rig is producing before the ratio it
            // sits underneath means anything. A twentieth is generous: at a beam reach the two are
            // comparable, and by the time the side force is below this the boat is running and
            // there is nothing to balance.
            result.HelmMeaningful =
                Mathf.Abs(totalSide) > 0.05f * (Mathf.Abs(totalFwd) + Mathf.Abs(totalSide));

            return sails.Count > 0;
        }

        private static bool TryEvaluateSail(Sail sail, Transform boat, Rigidbody body,
            Vector3 windWorld, float unroll, out SailContribution c)
        {
            c = default(SailContribution);

            // Save every input and output the call touches. UpdateWindForceOnSail writes nine
            // fields, and six vanilla components read appliedWindForce live, so nothing may leak.
            Vector3 sApparent = sail.apparentWind;
            float sUnroll = sail.currentUnroll;
            Rigidbody sShip = sail.shipRigidbody;
            Quaternion sRot = sail.transform.localRotation;
            float sApplied = sail.appliedWindForce;
            object sTotal = FTotalWind?.GetValue(sail);
            object sRel = FRelAngle?.GetValue(sail);
            object sHeight = FOutHeightMult?.GetValue(sail);
            object sFwdShare = FOutFwdShare?.GetValue(sail);
            object sSideShare = FOutSideShare?.GetValue(sail);
            object sPushing = FPushingBack?.GetValue(sail);
            object sUnampF = FUnampFwd.GetValue(sail);
            object sUnampS = FUnampSide.GetValue(sail);

            try
            {
                // A previewed sail is not wired to the hull yet; the force function dereferences
                // shipRigidbody for the boat's axes, so lend it the hull's.
                sail.shipRigidbody = body;
                sail.apparentWind = windWorld;
                sail.currentUnroll = unroll;

                // The caller passes the sail's own currentUnroll, so this is normally a no-op; it
                // differs only for the one sail a reef track is being drawn for. It used to be
                // readout showed the rig's potential, but the balance a player cares about is the
                // one they will actually sail under. Sail.cs:415 multiplies the wind force by it
                // linearly, so a furled sail contributes nothing and a reefed one contributes in
                // proportion, and because reefing never moves a sail's own windcenter, the effect
                // lands entirely on how much that sail counts toward the combined centre. That is
                // exactly right: reef every sail equally and the balance does not move, reef one
                // and it does.

                // Trim for best drive: evaluate both ends of the sail's own swing limits plus
                // centred, and keep whichever gives the most forward force.
                float bestFwd = float.NegativeInfinity, bestSide = 0f;

                // The centre of effort is captured INSIDE the winning branch, with the sail still
                // in the pose that won. It used to be read after the loop, which left the sail at
                // whatever the last iteration set, always sail.maxAngle. The force then came from
                // the winning trim while the point it acted at came from a different pose.
                //
                // That is not a small error. windcenter hangs off the sail root (Sail.cs:186 gives
                // the scaled subtree a nonzero local X offset), so swinging the root sweeps it
                // athwartships. In boat axes the yaw reduces to -arm.x * forwardForce, and forward
                // force is the largest force in the rig, so a metre of bogus athwartships offset
                // fabricates thousands of Nm. Worse, every sail was left at the SAME end of its
                // swing, so the errors shared a sign and accumulated rather than cancelling.
                //
                // Square sails were immune: their force acts along transform.right (Sail.cs:487-489)
                // and a symmetric yard puts the centre on the hinge axis. Everything fore-and-aft
                // was affected, which is the whole rig of a dhow or a junk.
                Vector3 bestCe = sail.windcenter.position;

                Vector3 euler = sRot.eulerAngles;
                foreach (float trim in new[] { sail.minAngle, 0f, sail.maxAngle })
                {
                    sail.transform.localRotation = Quaternion.Euler(euler.x, euler.y, trim);

                    MUpdateForce.Invoke(sail, null);

                    float unampF = ToFloat(FUnampFwd.GetValue(sail));
                    float unampS = ToFloat(FUnampSide.GetValue(sail));
                    float scale = sail.GetRealSailPower() * CategoryMultiplier(sail.category) * 50f;
                    float fwd = unampF * scale;
                    if (fwd > bestFwd)
                    {
                        bestFwd = fwd;
                        bestSide = unampS * scale * 1.5f;   // the sideways component carries an extra 1.5x
                        bestCe = sail.windcenter.position;
                    }
                }

                Vector3 ce = bestCe;
                Vector3 arm = ce - body.position;                       // about the HULL ORIGIN
                Vector3 armLocal = boat.InverseTransformVector(arm);
                Vector3 force = boat.forward * bestFwd + boat.right * bestSide;
                float yaw = Vector3.Dot(Vector3.Cross(arm, force), boat.up);

                c.Sail = sail;
                c.Name = sail.sailName;
                c.CentreOfEffort = ce;
                // The hull origin sits essentially at the waterline when afloat, so measuring the
                // CE against it removes the shipyard cradle's height offset, which would otherwise
                // inflate the height multiplier by up to 0.185 and saturate the curve early.
                c.HeightAboveWaterline = ce.y - body.position.y;
                c.ForwardForce = bestFwd;
                c.SideForce = bestSide;
                c.LeverFromOrigin = armLocal.z;
                c.YawMoment = yaw;
                c.CeTracksResizes = CeTracksResizes(sail);
                c.Unroll = unroll;
                c.UpwindEfficiency = sail.upwindEfficiency;
                c.Category = sail.category;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RigModel] '{sail.sailName}' could not be evaluated: {e.Message}");
                return false;
            }
            finally
            {
                sail.transform.localRotation = sRot;
                sail.apparentWind = sApparent;
                sail.currentUnroll = sUnroll;
                sail.shipRigidbody = sShip;
                sail.appliedWindForce = sApplied;
                Restore(FTotalWind, sail, sTotal);
                Restore(FRelAngle, sail, sRel);
                Restore(FOutHeightMult, sail, sHeight);
                Restore(FOutFwdShare, sail, sFwdShare);
                Restore(FOutSideShare, sail, sSideShare);
                Restore(FPushingBack, sail, sPushing);
                Restore(FUnampFwd, sail, sUnampF);
                Restore(FUnampSide, sail, sUnampS);
            }
        }

        /// <summary>
        /// Whether this sail's centre of effort actually moves when the sail is resized.
        ///
        /// The transform a resize scales is <c>cloth.transform.parent</c>. That is vanilla's own:
        /// Sail.ChangeScale takes it at Sail.cs:140 and writes its localScale at Sail.cs:185, and
        /// Sail.UpdateScaledInstallHeight reads it back at Sail.cs:202-206. Shipyard Expansion
        /// scales the same object by a different route (SailScaler.cs:36 finds it as the Animator's
        /// transform, SailScaler.cs:187 scales it) and then calls vanilla's UpdateScaledInstallHeight
        /// at SailScaler.cs:189, which only works because it is the same transform.
        ///
        /// So the question is simply whether windcenter is inside it. It is, on every stock sail.
        /// It is not on Shipyard Expansion's three added lug sails, where the centre of effort is
        /// reparented to the sail root and stays frozen through a resize.
        ///
        /// This used to go via GetComponentInChildren&lt;Animator&gt;() and treat a missing Animator as
        /// success. That was SE's route to the transform, not the game's, and the fallback assumed
        /// the answer it was supposed to be measuring. Vanilla sail resizing is the common case now,
        /// so it reads the game's own field.
        /// </summary>
        private static bool CeTracksResizes(Sail sail)
        {
            try
            {
                if (sail.cloth == null || sail.cloth.transform.parent == null)
                {
                    // Nothing to compare against. Say so rather than quietly claiming it tracks.
                    Plugin.Log.LogWarning($"[RigModel] '{sail.sailName}' has no cloth parent; "
                        + "cannot tell whether its centre of effort follows a resize.");
                    return true;
                }

                return sail.windcenter.IsChildOf(sail.cloth.transform.parent);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RigModel] resize check failed for '{sail.sailName}': {e.Message}");
                return true;
            }
        }

        /// <summary>
        /// The hull's fore-and-aft extent, so a tolerance can be expressed as a fraction of the
        /// boat rather than a flat number of metres. Half a metre of offset is nothing on a brig
        /// and most of the boat on a dinghy, so a fixed threshold necessarily lies to one of them.
        ///
        /// Measured from the hull's own colliders, on layer 4 (Water) or the hull layer, rather
        /// than from renderers: renderer bounds would include the masts and the sail cloth, which
        /// overhang the bow and stern and would inflate the figure. Falls back to the full collider
        /// set, and then to a fixed value, rather than returning something absurd.
        /// </summary>
        private static float MeasureHullLength(GameObject boatRoot, Transform boat)
        {
            try
            {
                var colliders = boatRoot.GetComponentsInChildren<Collider>();
                float minZ = float.MaxValue, maxZ = float.MinValue;

                foreach (Collider collider in colliders)
                {
                    if (collider == null || collider.isTrigger) continue;

                    Bounds bounds = collider.bounds;
                    // Both extremes of the world-space box, expressed along the boat's own axis.
                    Vector3 a = boat.InverseTransformPoint(bounds.min);
                    Vector3 b = boat.InverseTransformPoint(bounds.max);
                    minZ = Mathf.Min(minZ, Mathf.Min(a.z, b.z));
                    maxZ = Mathf.Max(maxZ, Mathf.Max(a.z, b.z));
                }

                float length = maxZ - minZ;
                if (length > 1f && length < 500f) return length;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] hull length could not be measured: " + e.Message);
            }

            return DefaultHullLength;
        }

        /// <summary>Used only when the hull's colliders give nothing sane to measure.</summary>
        public const float DefaultHullLength = 20f;

        private static float CategoryMultiplier(SailCategory category)
        {
            if (category == SailCategory.junk) return 0.75f;
            if (category == SailCategory.gaff) return 0.85f;
            return 1f;
        }

        private static float ToFloat(object o) => o is float f ? f : 0f;

        private static void Restore(FieldInfo field, object target, object value)
        {
            if (field == null || value == null) return;
            try { field.SetValue(target, value); } catch { }
        }
    }
}
