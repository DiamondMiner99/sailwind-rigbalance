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
        public Vector3 CenterOfEffort;   // world
        public float HeightAboveWaterline;
        public float ForwardForce;       // N
        public float SideForce;          // N
        public float LeverFromOrigin;    // m, positive forward
        public float YawMoment;          // Nm, positive turns the bow to starboard

        /// <summary>
        /// The two halves YawMoment splits into, in boat axes: side force acting at a fore-and-aft
        /// lever, and forward thrust acting off the centerline. They are tracked apart because only
        /// the first one is what a center of effort against a center of lateral resistance describes.
        /// Dividing their SUM by the side force, which is what this used to do, produces nonsense
        /// wherever the side force vanishes but the thrust asymmetry does not.
        /// </summary>
        public float YawFromSide;
        public float YawFromThrust;
        public bool CeTracksResizes;     // false only for a Shipyard Expansion added sail
        public float Unroll;             // what the model actually evaluated this sail at

        /// <summary>
        /// The trim the search settled on, in degrees about the sail's swing axis.
        ///
        /// Recorded for the mirror audit. A rig read at bearing b and at 360-b must choose trims that
        /// are exact negations, and when the ring comes out lopsided this is the first number worth
        /// seeing, because it separates a bad CHOICE of pose from a bad force at a good pose. Those
        /// two have opposite fixes and no other logged quantity tells them apart.
        /// </summary>
        public float Trim;

        // Sail.cs:415 drags a sail's force from its full value at 90 degrees down toward
        // totalWindForce * upwindEfficiency as the apparent wind closes on the bow. Sails with
        // different efficiencies therefore lose force at different rates coming up on the wind,
        // which is the mechanism that can move a rig's balance with point of sail.
        public float UpwindEfficiency;
        public SailCategory Category;

        /// <summary>
        /// Vanilla's sideways share (Sail.cs:443) and backwards flag (Sail.cs:431) at the pose the
        /// search settled on. A share close to 1 means the force found is essentially abeam, and the
        /// flag says which side of the drive discontinuity at Sail.cs:427 that pose sits on. Together
        /// they distinguish a sail that is genuinely driving from one whose apparent drive is an
        /// artifact of the search landing hard against that step. Diagnostic only: nothing computes a
        /// verdict from either.
        /// </summary>
        public float SideShare;
        public bool Aback;

        /// <summary>True when this sail's drive was resolved off vanilla's knife edge. See
        /// RigModel.AthwartshipsShare.</summary>
        public bool DriveClamped;

        /// <summary>
        /// The sail's rest attitude, as the Euler angles the trim search substitutes into.
        ///
        /// This is the measurement that decides how far the remaining mirror defect reaches. The model
        /// poses each sail as Euler(rest.x, rest.y, trim), and Unity composes Ry * Rx * Rz, so
        /// reflecting through the centerline maps Euler(x, y, z) to Euler(x, -y, -z). The x term is
        /// harmless; a nonzero y is not, because then the mirror of a pose the search tries is a pose
        /// it never tries, and that is true at EVERY bearing rather than only where it shows.
        ///
        /// So if y is zero across a boat, the defect really is confined to where it was seen and the
        /// symptom bounds the cause. If it is not, every reading carries a small error and the pose
        /// family has to be rebuilt on the hinge axis instead. Those have opposite conclusions and
        /// nothing already logged separates them.
        /// </summary>
        public Vector3 RestEuler;

        /// <summary>
        /// The center of effort as an arm from the hull origin in BOAT axes, x to starboard and z
        /// forward, captured at the instant the sail was evaluated.
        ///
        /// CenterOfEffort is a world position, and a sweep spans about thirty six frames during which
        /// the shipyard cradle is still lerping the hull into place (Shipyard.MoveShip runs over
        /// roughly two seconds). Comparing two world positions taken that far apart therefore measures
        /// the cradle's travel as well as the rig, which made the mirror audit report a fictitious 3.6
        /// m disagreement in every sail's station. The yaw moments never had that problem, because each
        /// was taken in the hull's own frame at its own instant; only a later comparison of the raw
        /// positions did. This is that same frame-correct arm, kept so the audit can use it.
        /// </summary>
        public Vector3 Arm;

        /// <summary>The swing limits this sail's trim search was allowed, in degrees.</summary>
        public float MinTrim, MaxTrim;
    }

    /// <summary>The whole answer for one rig at one synthetic wind.</summary>
    public struct RigBalance
    {
        public List<SailContribution> Sails;

        /// <summary>
        /// Signed, positive to starboard. Read by the reef track, which refuses to draw across a
        /// sign change in it, and printed in the debug summary.
        /// </summary>
        public float TotalSideForce;

        // The two raw quantities HelmMeters is built from. Nothing computes a verdict from them any
        // more, deliberately, but they are in the debug summary: when a reading looks wrong, which
        // of the two moved is the first thing worth knowing from a pasted log.
        public float TotalForwardForce;
        public float YawMoment;          // about the hull origin

        /// <summary>
        /// YawMoment split into the half that is side force at a lever, which is what a balance
        /// reading is about, and the half that is thrust acting off the centerline, which is not.
        /// The verdict is only offered while the first is the larger of the two. Both are printed in
        /// the debug summary, because "which half moved" is the first question when a reading looks
        /// wrong.
        /// </summary>
        public float YawFromSide;
        public float YawFromThrust;

        public Vector3 CombinedCenterOfEffort;
        public Vector3 HullOrigin;
        public bool AnySailHasFrozenCe;
        public float HullLength;         // m, along the boat's own fore-aft axis
        public float WindAngle;          // degrees off the bow this was evaluated at

        /// <summary>
        /// Where the hull's collider box actually sits, in boat axes, relative to the boat root.
        /// Zero on a healthy boat. The first field report proved the two can come apart: repeated
        /// oversized sail resizes at a cramped cradle walked the visible hull away from its own
        /// root a little at a time, and everything anchored to the root, the shipyard camera and
        /// the drawn ring alike, stayed behind. Anything DRAWN should center on this; everything
        /// computed stays about the origin, because the game applies the hull's lateral drag at the
        /// rigidbody position and that is the real pivot wherever the mesh has walked.
        /// </summary>
        public Vector3 HullCenterLocal;

        /// <summary>
        /// Everything the rig is producing, |forward| + |side|, always positive. Two uses: it is
        /// the honest measure of whether the sails are drawing at all at this angle, and it is
        /// comparable between angles, which is what lets the ring decide where the rig has gone
        /// dead by measurement rather than by an assumed threshold.
        /// </summary>
        public float TotalOutput;

        /// <summary>
        /// False when the rig is producing so little side force that the ratio underneath the
        /// verdict means nothing, which is the case running before the wind.
        /// </summary>
        public bool HelmMeaningful;

        /// <summary>
        /// The yaw moment expressed as a lever arm, in meters, positive for weather helm. This is
        /// the single number the marker POSITION, the marker color, the ring and the reef track
        /// all read; it used to be recomputed from YawMoment and TotalSideForce at three separate
        /// call sites, which is three chances for the sign convention to drift apart.
        ///
        /// Divided by the SIGNED total side force, deliberately. Dividing by |side| or by the rig's
        /// total output was considered and is wrong: in lateral equilibrium the hull's drag opposes
        /// whatever side force the sails make, so if the net side force reverses, the hull's
        /// reaction reverses with it and the whole geometry genuinely inverts. A center of effort
        /// aft of the origin gives weather helm with the sails pushing to leeward and lee helm with
        /// them pushing to windward. The sign change is physics, not an artifact.
        ///
        /// The failure that division does have, blowing up as the side force vanishes on a run, is
        /// handled by HelmMeaningful rather than by choosing a divisor that never vanishes: a
        /// divisor of total output would quietly shrink a real two-meter lever to a tenth of a meter
        /// wherever the drive dominates, and report a one-handed boat as balanced.
        /// </summary>
        public float HelmMeters;

        /// <summary>
        /// True when the rig is drawing, the side force is real and pulling one way, and the lever
        /// still lands beyond the quarter-hull bound HelmMeaningful enforces. That combination is
        /// not noise, it is the answer: a center of effort a quarter of a hull from the pivot is a
        /// boat that slews hard, full stop. The first boat to hit it was a small dhow carrying one
        /// oversized lateen, whose ring came out gray all the way round; the player read gray as
        /// "no power" while the sail plainly drew, and their complaint was correct. HelmMeters stays
        /// zero here on purpose, so nothing downstream positions a marker off a clamped ratio; the
        /// DIRECTION lives in RawLever's sign and the ring paints it at full saturation.
        ///
        /// The couple case stays out via the coherence test in TryEvaluate: when the sails' side
        /// forces pull in MIXED directions, the net side force is the small difference of large
        /// terms, the "point where the side force acts" runs off the hull without any one sail
        /// being there, and painting a confident direction on that would be the lever pole all over
        /// again. Measured on a fourteen-sail junk: dead pockets at 25 and 335 degrees with a 74
        /// percent side share, real bearings, where the fore and aft sails pull opposite ways.
        /// </summary>
        public bool HelmOffScale;

        /// <summary>
        /// The lever before the HelmMeaningful gates, meters, positive weather. Logged always;
        /// trusted only while HelmMeaningful or HelmOffScale says the denominator was real.
        /// </summary>
        public float RawLever;
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
    /// Two inputs the game reads that the shipyard makes meaningless are cancelled, because a
    /// pre-purchase reading has to depend on the rig and nothing else:
    ///  - the mutual sail-shadowing factor, which is keyed to the live outdoor wind and would make
    ///    the same rig read differently as the weather changed (see TryEvaluateSail);
    ///  - the wind-height multiplier, which is taken off raw world Y and so is inflated by however
    ///    high the cradle at this particular shipyard holds the boat.
    ///
    /// The moment is taken about the HULL ORIGIN, not the center of mass. In lateral-force
    /// equilibrium the hull's lateral drag exactly opposes the sails' side force and acts at the
    /// origin (Crest BoatProbes.cs:220-223 applies both the lateral and the forward drag at
    /// rb.position + _forceHeightOffset * up, and a purely vertical offset cannot change a yaw
    /// moment), so the two side-force terms collapse to a single lever about that point. That is
    /// what makes a pre-purchase number possible: it needs no leeway and no wind speed.
    ///
    /// Hull windage is deliberately NOT in the sum. BoatWind.FixedUpdate (BoatWind.cs:33) pushes the
    /// hull downwind at an offset measured 7 to 12 m aft on every playable hull, which is a genuine
    /// one-way weather-helm term of roughly 0.09 m of lever at this wind speed. It is left out for
    /// two reasons. It is a property of the hull, identical whichever rig is fitted, so it cancels
    /// out of the only comparison this tool exists to support: does this sail plan balance better
    /// than that one. And folding it in would move the magenta marker off the sail plan's own center
    /// of effort, which is what the marker is labelled and what the player is told to read. The cost
    /// is a known bias of about a fifth of the balanced band, always in the same direction.
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
        private static readonly FieldInfo FShadowCol = AccessTools.Field(typeof(Sail), "shadowCol");
        private static readonly MethodInfo MUpdateForce = AccessTools.Method(typeof(Sail), "UpdateWindForceOnSail");

        /// <summary>True when every private member the model drives resolved.</summary>
        public static bool Available =>
            FUnampFwd != null && FUnampSide != null && MUpdateForce != null;

        /// <summary>
        /// True when the two vanilla readings needed to recognize a pose lying on vanilla's own drive
        /// knife edge both resolved. Deliberately NOT part of Available: without them the model still
        /// produces every force and every center it ever did, so disabling the gizmo outright would
        /// cost far more than the one defect it would avoid. See AthwartshipsShare.
        /// </summary>
        private static bool CanResolveKnifeEdge =>
            FOutSideShare != null && FPushingBack != null;

        /// <summary>Names already complained about, so a warning cannot repeat every rebuild.</summary>
        private static readonly HashSet<string> Warned = new HashSet<string>();

        private static bool WarnOnce(string key, string message)
        {
            if (!Warned.Add(key)) return false;
            Plugin.Log.LogWarning(message);
            return true;
        }

        /// <summary>
        /// Apparent wind speed the synthetic evaluation uses. The sim's force is LINEAR in wind
        /// speed (totalWindForce starts as apparentWind.magnitude), so this scales the whole answer
        /// without changing its sign or the balance between sails. 10 m/s is a brisk working
        /// breeze and keeps the printed numbers in a readable range.
        /// </summary>
        public const float SyntheticWindSpeed = 10f;

        /// <summary>
        /// Poses tried across a sail's whole swing before the search narrows, and poses tried inside
        /// the winning bracket afterwards. Ten calls into the game's force function per sail, which
        /// lands the trim within about twelve degrees on a hundred degree swing, against fifty for
        /// the three fixed poses it replaced.
        ///
        /// Twelve degrees sounds loose and is not, for the force: drive is flat near its own peak,
        /// so being a few degrees off the optimum costs a couple of percent. It matters for the
        /// GEOMETRY. windcenter hangs off the sail root, so trim sweeps the center of effort
        /// athwartships, and an athwartships error multiplies the largest force in the rig to
        /// fabricate yaw. That is the same mechanism that made reading the center at the wrong trim
        /// the worst defect in the whole model.
        /// </summary>
        private const int CoarseTrimSamples = 5;
        private const int RefineTrimSamples = 5;

        /// <summary>
        /// How close two trims' drive has to be before they count as a tie and the centerd one wins,
        /// as a fraction of what the sail could make at full power. A thousandth: far above float
        /// noise, far below any difference a player could care about, and crucially it is measured
        /// against the sail's OWN capacity rather than against whatever force this particular
        /// evaluation happened to produce, so the window does not vanish when the forces do.
        /// </summary>
        private const float TieFraction = 0.001f;

        /// <summary>
        /// Vanilla's own drive multiplier for a sail whose force is pushing the boat backwards
        /// (Sail.cs:429). Named here only so the correction below can state that it is reproducing
        /// vanilla's other branch rather than inventing a factor.
        /// </summary>
        private const float AbackDriveMultiplier = -0.33f;

        /// <summary>
        /// How close to dead abeam a sail's force may be before vanilla's drive test stops being a
        /// statement about geometry, as a sideways share where 1 is exactly abeam.
        ///
        /// Sail.cs:425-429 flips the drive multiplier from +1 to -0.33 across a STRICT num3 > 90,
        /// num3 being the angle between the sail's force and the bow, and Sail.cs:446 multiplies the
        /// forward force by it. A fore-and-aft sail sheeted exactly amidships puts its force exactly
        /// abeam, so that comparison is made on a dot product whose true value is ZERO and whose sign
        /// is therefore whatever the last bit of the arithmetic says. Trim 0 is its own reflection, so
        /// the identical pose is evaluated on both tacks, lands on opposite sides of the test, and one
        /// tack is awarded +1 while the other gets -0.33: a factor of -3 on one sail in one attitude.
        ///
        /// That is what tore the bearings nearest the bow apart, and it was measured, not reasoned:
        /// on a brig at 15 against 345 degrees the jib read +163 N of drive against -53 N, and two
        /// genoas +85/-23 and +102/-28, each pair a ratio of about -0.33, all three at trim 0.0 with a
        /// sideways share of 1.00. Vanilla's num7 floor (Sail.cs:441, Lerp toward upwindEfficiency)
        /// is what makes the awarded branch large rather than negligible, so the error was whole
        /// newtons rather than a rounding.
        ///
        /// Square sails are untouched by this: at trim 0 a yard braced athwartships puts its force
        /// along the boat's axis, where num3 is nowhere near 90. The log bears that out, every square
        /// sail mirroring its trim and both forces exactly.
        ///
        /// Half a degree, written as a share. Four orders of magnitude above the noise in the
        /// comparison it is guarding, far below any attitude a player could set or notice, and it can
        /// only ever change a pose whose forward force is zero by construction. Poses off the knife
        /// edge keep vanilla's answer untouched, which is why the thirteen bearing pairs on the beam
        /// stay exact.
        /// </summary>
        private const float AthwartshipsShare = 89.5f / 90f;

        /// <summary>
        /// Evaluate a rig at an apparent wind coming from <paramref name="windAngleDegrees"/> off
        /// the bow, on the port bow (so positive yaw means the bow turns away from the wind).
        /// 45 is close hauled, 90 is a beam reach.
        /// </summary>
        /// <param name="overrideSail">
        /// Optional. Evaluate as if this one sail were set to <paramref name="overrideUnroll"/>
        /// instead of its real furl state, leaving every other sail alone. This is what draws the
        /// reef track: run it once as things stand and once with the selected sail at the opposite
        /// extreme, and the two combined centers are the ends of the path the center of effort takes
        /// through every reef setting between them. Sail.cs:415 scales the force linearly and
        /// nothing in the reef path moves that sail's own windcenter, so only its weight in the
        /// average changes; the drawn straight line between the two ends is a close approximation of
        /// that path rather than exactly it, because the line-of-action correction applied to each
        /// end is not itself linear in the swept weight.
        ///
        /// Any sail whose unroll is chained to this one by a RopeControllerMirror is overridden
        /// with it, because the game will move them together whatever the model does.
        /// </param>
        /// <param name="knownHullLength">
        /// A hull length already measured for this boat this rebuild. Measuring walks every collider
        /// on the boat, and the ring's sweep calls this a dozen times for a number that cannot change
        /// between them, so the caller passes it in. Zero means measure.
        /// </param>
        public static bool TryEvaluate(GameObject boatRoot, float windAngleDegrees, out RigBalance result,
            Sail overrideSail = null, float overrideUnroll = 1f, float knownHullLength = 0f,
            RigSnapshot snapshot = null)
        {
            result = default(RigBalance);
            if (boatRoot == null || !Available) return false;

            var refs = boatRoot.GetComponent<BoatRefs>();
            var body = boatRoot.GetComponent<Rigidbody>();
            if (refs == null || body == null || refs.masts == null) return false;

            Transform boat = boatRoot.transform;

            // Wind on the PORT bow: blowing toward starboard (+x) and aft (-z). With the wind on
            // port, leeward is starboard, so a positive yaw (bow to starboard) is the bow falling
            // AWAY from the wind, i.e. lee helm. HelmMeters flips that so positive reads as weather.
            float rad = windAngleDegrees * Mathf.Deg2Rad;
            Vector3 windLocal = new Vector3(Mathf.Sin(rad), 0f, -Mathf.Cos(rad)) * SyntheticWindSpeed;
            Vector3 windWorld = boat.TransformVector(windLocal);

            HashSet<Sail> overridden = overrideSail == null ? null : UnrollGroupOf(boatRoot, overrideSail);

            var sails = new List<SailContribution>();
            float totalSide = 0f, totalFwd = 0f;
            float yawFromSide = 0f, yawFromThrust = 0f;
            float weightSum = 0f;
            float sideMagSum = 0f;
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

                    // Frozen state where a sweep provided one, live state otherwise. A single
                    // bearing is a single frame and has nothing to freeze; a sweep spans about
                    // thirty six, and the boat does not hold still for that long.
                    SailState state = default(SailState);
                    if (snapshot != null) snapshot.Sails.TryGetValue(sail, out state);

                    float unroll = overridden != null && overridden.Contains(sail)
                        ? overrideUnroll
                        : (state.Valid ? state.Unroll : sail.currentUnroll);
                    if (!TryEvaluateSail(sail, boat, body, windWorld, windLocal.x, unroll, state, out c))
                        continue;

                    // A sail producing no force cannot affect the balance by definition, and on a
                    // brig three of them turn up parked at the boat origin, presumably not yet
                    // placed on a mast. They were already weighted zero; dropping them here keeps
                    // the sail count and the log honest.
                    if (Mathf.Abs(c.ForwardForce) + Mathf.Abs(c.SideForce) < 0.001f) continue;

                    sails.Add(c);
                    totalSide += c.SideForce;
                    totalFwd += c.ForwardForce;
                    sideMagSum += Mathf.Abs(c.SideForce);
                    yawFromSide += c.YawFromSide;
                    yawFromThrust += c.YawFromThrust;
                    if (!c.CeTracksResizes) anyFrozen = true;

                    float w = Mathf.Abs(c.SideForce) + Mathf.Abs(c.ForwardForce);
                    ceAccum += c.CenterOfEffort * w;
                    weightSum += w;
                }
            }

            // The hull's own windage, added last because it is not a sail and does not belong in the
            // sail list or in the drawn center's weighting.
            AddHullWindage(boatRoot, boat, body, windWorld, ref totalSide, ref totalFwd,
                ref yawFromSide, ref yawFromThrust);

            float totalYaw = yawFromSide + yawFromThrust;

            result.Sails = sails;
            result.TotalSideForce = totalSide;
            result.TotalForwardForce = totalFwd;
            result.YawMoment = totalYaw;
            result.YawFromSide = yawFromSide;
            result.YawFromThrust = yawFromThrust;
            result.HullOrigin = body.position;
            result.AnySailHasFrozenCe = anyFrozen;
            Vector3 hullCenterLocal = Vector3.zero;
            result.HullLength = knownHullLength > 0f
                ? knownHullLength
                : MeasureHullLength(boatRoot, boat, out hullCenterLocal);
            result.HullCenterLocal = hullCenterLocal;
            result.WindAngle = windAngleDegrees;
            result.TotalOutput = Mathf.Abs(totalFwd) + Mathf.Abs(totalSide);

            // Side force has to be a real share of what the rig is producing before the ratio it
            // sits underneath means anything. A twentieth is generous: at a beam reach the two are
            // comparable, and by the time the side force is below this the boat is running and
            // there is nothing to balance.
            // Positive = center of effort aft of the hull origin = weather helm. See HelmMeters.
            float lever = Mathf.Abs(totalSide) > 0.0001f ? -totalYaw / totalSide : 0f;

            // A center of effort against a center of lateral resistance is a statement about SIDE
            // force acting at a lever, and it says nothing once the side force it is divided by is
            // on its way through zero. As the wind goes aft the net side force crosses zero, but the
            // rest of the yaw does NOT vanish with it, so the ratio runs off to infinity, changes
            // sign and comes back. A live log on a fourteen-sail junk read -1.8 m, -3.3 m, -13.5 m,
            // then +9.8 m over ten degrees of arrow movement, on a 29.7 m hull with a 0.59 m band.
            // On the ring that is a spike of lee helm hard against a spike of weather helm, which is
            // what a pole looks like when you draw it.
            //
            // Two bounds, and it takes both. The share test is the honest statement of when the
            // question applies at all, but a fifth of the way to the crossing the side force is
            // small without yet being a small SHARE, which is how the old 5 percent let a 13 m lever
            // through. The lever bound then catches whatever the share test misses, and it needs no
            // guess about which term ought to dominate: a center of effort a quarter of a hull from
            // the hull's own center is already a dozen times outside the balanced band, so nothing
            // real is lost by declining to put a number on it, whereas a pole drawn as a verdict is
            // actively misleading.
            //
            // Deliberately NOT a test of whether the side-force half of the yaw beats the thrust
            // half. That reads well on paper and would have been wrong here: a junk sets its sails
            // to one side of the mast, so thrust acting off the centerline is a large and entirely
            // genuine term on every heading, and the ring would have gone gray all the way round on
            // the very boat this was found on. The two halves are tracked and logged so the split
            // can be seen, not used as a gate.
            result.HelmMeaningful =
                sails.Count > 0
                && Mathf.Abs(totalSide) > 0.15f * result.TotalOutput
                && Mathf.Abs(totalSide) > 0.01f
                && Mathf.Abs(lever) < 0.25f * Mathf.Max(result.HullLength, DefaultHullLength);

            // The reading beyond that lever bound, kept apart from the noise the bound also
            // rejects. When the share and absolute tests PASS and only the bound failed, the side
            // force is real; the remaining question is whether it acts anywhere in particular. The
            // coherence ratio answers that: near one, every sail pulls the same way and the huge
            // lever is a fact about the rig (one oversized lateen on a small hull puts the center
            // of effort a quarter hull from the pivot by construction); low, the sails pull against
            // each other, the net side force is a residue, and the "point" it acts at is an
            // artifact of dividing by that residue. 0.7 splits a single-sail dhow (1.0 by
            // construction) from the junk's measured mixed-sign pockets comfortably.
            result.RawLever = lever;
            result.HelmOffScale =
                !result.HelmMeaningful
                && sails.Count > 0
                && Mathf.Abs(totalSide) > 0.15f * result.TotalOutput
                && Mathf.Abs(totalSide) > 0.01f
                && sideMagSum > 0.0001f
                && Mathf.Abs(totalSide) >= 0.7f * sideMagSum;

            // Gated on the SAME predicate the marker and every color read, never on a threshold of
            // its own: a reading that is meaningful but has no number, or has a number nothing else
            // trusts, is how the marker ends up pinned on the hull origin reporting a confident
            // "balanced" for a yaw moment it never reproduced.
            result.HelmMeters = result.HelmMeaningful ? lever : 0f;

            result.CombinedCenterOfEffort = CombineCenterOfEffort(
                boat, body, weightSum > 0.0001f ? ceAccum / weightSum : body.position,
                result.HelmMeters, result.HelmMeaningful, result.HullLength);

            // A rig where no sail is drawing is a valid reading, not a failure: the result above is
            // fully populated with zeros, HelmMeaningful is false, and everything downstream paints
            // it as the gray no-verdict state it is. Returning false here was a live bug. The
            // caller's response to false is to hide the gizmo entirely, and the hidden gizmo is
            // also where the wind arrow lives, so a player who parked the arrow head to wind on a
            // fore-and-aft rig, where every sail's best trim is the exactly-zero pose, watched
            // every mod feature vanish and stay vanished until they left the shipyard: the arrow
            // that would have moved the evaluation off the dead bearing was itself hidden. False is
            // reserved for a boat the model cannot ask anything of at all.
            return true;
        }

        /// <summary>
        /// Folds in the force the wind puts on the hull itself, masts and all.
        ///
        /// BoatWind.FixedUpdate (BoatWind.cs:25-33) pushes the boat downwind with a force whose
        /// magnitude lerps between frontForce and sideForce by how square the wind is on the beam,
        /// applied at transform.TransformPoint(forceOffset), which sits 7 to 12 m AFT on every
        /// playable hull. That is a vane behind the pivot, so it is weather helm on every heading
        /// and every tack: it never cancels, and it does not shrink at other wind speeds because
        /// both it and the sails are linear in apparent wind.
        ///
        /// It was left out at first, on the grounds that it is a property of the hull rather than
        /// the rig and so cancels out of "does this sail plan balance better than that one", and
        /// that folding it in would drag the magenta marker off the sail plan's own center of
        /// effort. The second half of that argument died when the marker stopped being the sail
        /// plan's centroid and became the lever arm the yaw moment works out to. A lever arm is
        /// about the whole force balance, so leaving out a real term of it was simply an error of
        /// about 0.09 m in the same direction every time, against a band of 0.59 m on a junk.
        ///
        /// The sails' own list and the drawn center's weighting are untouched: this is a force on
        /// the hull, not a sail, and the per-sail readout stays about sails.
        /// </summary>
        private static void AddHullWindage(GameObject boatRoot, Transform boat, Rigidbody body,
            Vector3 windWorld, ref float totalSide, ref float totalFwd,
            ref float yawFromSide, ref float yawFromThrust)
        {
            try
            {
                var windage = boatRoot.GetComponent<BoatWind>();
                if (windage == null) return;

                // BoatWind.cs:26-32, with the synthetic wind standing in for Wind.currentWind minus
                // the boat's velocity. The boat is kinematic at a shipyard, so velocity is zero and
                // the apparent wind IS the wind.
                float offBow = Mathf.Abs(Vector3.SignedAngle(boat.forward, windWorld.normalized, Vector3.up));
                if (offBow > 90f) offBow = 180f - offBow;
                float magnitude = Mathf.Lerp(windage.frontForce, windage.sideForce,
                    Mathf.InverseLerp(0f, 90f, offBow));

                Vector3 forceLocal = boat.InverseTransformVector(windWorld * magnitude);
                Vector3 armLocal = windage.forceOffset;   // already in the boat's own frame

                // The windage point is on the centerline on every playable hull, which is what makes
                // this term mirror cleanly. If a modded hull ever moves it off, say so rather than
                // letting it tilt the ring asymmetrically without explanation.
                if (Mathf.Abs(armLocal.x) > 0.001f)
                {
                    WarnOnce("windage:offset", "[RigModel] this hull's windage acts "
                        + $"{armLocal.x:F2} m off the centerline, so the balance ring will not be "
                        + "symmetric port and starboard even for a symmetric rig.");
                }

                totalSide += forceLocal.x;
                totalFwd += forceLocal.z;
                yawFromSide += armLocal.z * forceLocal.x;
                yawFromThrust += -armLocal.x * forceLocal.z;
            }
            catch (Exception e)
            {
                WarnOnce("windage", "[RigModel] the hull's own windage could not be read, so the "
                    + "balance reading leaves out a small weather-helm term: " + e.Message);
            }
        }

        /// <summary>
        /// Where to draw the center of effort, so that the offset the player reads IS the verdict.
        ///
        /// The force-weighted centroid of the sails is where the rig physically is, and it is not
        /// the point that reproduces the rig's yaw moment. The yaw weights each sail's fore-and-aft
        /// lever by its SIDE force and its athwartships offset by its FORWARD force, while the
        /// centroid weights both by the sum. On a square rigger close hauled the courses carry most
        /// of the drive and the headsails most of the side force, from opposite ends of the boat, so
        /// the two points differ by meters.
        ///
        /// That mattered because the two halves of the readout came from different places: the
        /// marker's position from the centroid, the link's color from the yaw moment. A player
        /// could see magenta plainly forward of blue, which the mod tells them is lee helm, while
        /// the link between them was painted blue for weather helm, with nothing to say which one
        /// was lying.
        ///
        /// So the marker is put where the classical picture puts it: on the centerline, at the lever
        /// arm the yaw moment works out to. Then yaw = arm.z * sideForce exactly, arm.z is exactly
        /// -HelmMeters, and "magenta aft of blue is weather helm" stops being a rule of thumb and
        /// becomes an identity. Height is left at the centroid's, because that is the heeling lever
        /// against the center of mass and it is a separate reading.
        ///
        /// Note this is NOT the same as sliding the centroid onto the resultant's line of action,
        /// which was the first attempt. That slide is the minimum-norm correction, so it puts as
        /// little as possible of the fix into the fore-and-aft axis and the rest into athwartships,
        /// which is the one axis the elbow link cannot show. With drive at two and a half times side
        /// force it removed about a seventh of the disagreement and left the rest.
        ///
        /// When the reading carries no verdict, the centroid is drawn unchanged: HelmMeters is a
        /// ratio with a vanishing denominator there, and pinning the marker to a clamped lever would
        /// fling it to one end of the boat and then to the other as the side force wandered across
        /// zero. Where there is nothing to assert, the marker shows where the sails are.
        ///
        /// That does mean the marker jumps when the reading crosses into having a verdict, because
        /// the two definitions do not meet. The jump is bounded by clamping the lever to half a hull
        /// length, which is already far outside any balanced band, and it happens on the same frame
        /// the link changes from gray to a color, which is the cue that the reading has changed
        /// character. A smooth blend between a real answer and a placeholder would only disguise
        /// which of the two is on screen.
        /// </summary>
        private static Vector3 CombineCenterOfEffort(Transform boat, Rigidbody body, Vector3 centroid,
            float helmMeters, bool helmMeaningful, float hullLength)
        {
            if (!helmMeaningful) return centroid;

            // Boat axes: x starboard, z forward. A point on the centerline at -helmMeters has, by
            // construction, the rig's own moment about the hull origin: yaw = arm.z * side.
            // Only the height is taken from the centroid; x and z are replaced outright.
            float height = boat.InverseTransformVector(centroid - body.position).y;
            float limit = Mathf.Max(hullLength, DefaultHullLength) * 0.5f;

            var arm = new Vector3(0f, height, Mathf.Clamp(-helmMeters, -limit, limit));
            return body.position + boat.TransformVector(arm);
        }

        /// <summary>
        /// Every sail whose furl state moves together with this one.
        ///
        /// RopeControllerMirror.Update assigns sail.currentUnroll = parentSail.currentUnroll every
        /// frame, unconditionally and with no shipyard guard (RopeControllerMirror.cs:22). So a
        /// mirrored sail cannot be furled on its own: write to it and the next frame puts it back,
        /// and a reef track drawn for it alone would predict a center of effort the player can never
        /// reach. Overriding the whole chained set instead models what the game will actually do.
        /// </summary>
        public static HashSet<Sail> UnrollGroupOf(GameObject boatRoot, Sail sail)
        {
            var group = new HashSet<Sail>();
            if (sail == null) return group;
            group.Add(sail);

            try
            {
                // If this sail is itself a mirror, the parent is what the game copies from, so the
                // group is really the parent's.
                Sail root = sail;
                for (int hops = 0; hops < 4; hops++)
                {
                    var mirror = root.GetComponent<RopeControllerMirror>();
                    if (mirror == null || mirror.parentSail == null || mirror.parentSail == root) break;
                    root = mirror.parentSail;
                    group.Add(root);
                }

                if (boatRoot == null) return group;

                // To a fixed point, not one pass. A single pass over the component array only picks
                // up a chain (C mirrors B, B mirrors A) when hierarchy order happens to enumerate B
                // before C, so whether the far end of a chain was found came down to prefab layout.
                // The array is at most a few dozen entries and this converges in as many rounds as
                // the chain is deep.
                RopeControllerMirror[] mirrors = boatRoot.GetComponentsInChildren<RopeControllerMirror>(true);
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (RopeControllerMirror mirror in mirrors)
                    {
                        if (mirror == null || mirror.parentSail == null) continue;
                        if (!group.Contains(mirror.parentSail)) continue;

                        var follower = mirror.GetComponent<Sail>();
                        if (follower != null && group.Add(follower)) grew = true;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] could not resolve mirrored sails: " + e.Message);
            }

            return group;
        }

        private static bool TryEvaluateSail(Sail sail, Transform boat, Rigidbody body,
            Vector3 windWorld, float windLateral, float unroll, SailState state,
            out SailContribution c)
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
            object sShadow = FShadowCol?.GetValue(sail);

            try
            {
                // A previewed sail is not wired to the hull yet; the force function dereferences
                // shipRigidbody for the boat's axes, so lend it the hull's.
                sail.shipRigidbody = body;
                sail.apparentWind = windWorld;
                sail.currentUnroll = unroll;

                // Sail.cs:413 multiplies the whole force by GetCurrentShadowMult(), the fraction of
                // this sail NOT blanketed by another. That factor is computed from the LIVE OUTDOOR
                // WIND: SailShadowCol.CheckShadow raycasts along Wind.currentWind * -1 over 18 m
                // (SailShadowCol.cs:60), its Update has no shipyard gate, and it re-runs on any sail
                // with unroll above zero, which a preview sail always has because
                // RopeControllerSailReef.cs:103-106 forces uninstalled shipyard sails to 1.
                //
                // So without this, each sail is de-weighted by how much some OTHER sail happens to
                // shade it under today's weather, differently per sail and asymmetrically fore and
                // aft. Dragging the wind arrow would not change which sails were shaded, and the
                // same untouched rig would give a different verdict when the weather outside
                // changed. Sail.GetCurrentShadowMult returns 1 when the field is null
                // (Sail.cs:266-269), so clearing it for the duration is the whole fix.
                if (FShadowCol != null) FShadowCol.SetValue(sail, null);
                else WarnOnce("shadowCol", "[RigModel] Sail.shadowCol did not resolve, so the "
                    + "balance reading includes whatever sail shading today's weather produces.");

                // Both of these are vanilla OUTPUTS, so the model runs without them and Available does
                // not require them. What it loses is the ability to recognize a pose whose force is
                // dead abeam, where vanilla's drive test is deciding the sign of a zero and decides it
                // oppositely on each tack. Said out loud rather than left to be rediscovered: the ring
                // going lopsided at the bow again is precisely the bug this guards, and it cost three
                // releases to find the first time.
                if (!CanResolveKnifeEdge)
                {
                    WarnOnce("knifeEdge", "[RigModel] Sail.outSidewayShare or "
                        + "Sail.pushingBoatBackwards did not resolve, so a sail sheeted amidships "
                        + "cannot be told from one merely near it. The bearings nearest the bow will "
                        + "disagree between port and starboard.");
                }

                // The caller passes the sail's own currentUnroll, so this is normally a no-op; it
                // differs only for the one sail a reef track is being drawn for. Sail.cs:415
                // multiplies the wind force by it linearly, so a furled sail contributes nothing and
                // a reefed one contributes in proportion, and because reefing never moves a sail's
                // own windcenter, the effect lands entirely on how much that sail counts toward the
                // combined center. That is exactly right: reef every sail equally and the balance
                // does not move, reef one and it does.

                // Trim for best drive: sample poses across the sail's own swing limits, then
                // choose the winner from the WHOLE set with a rule that does not depend on the order
                // they were tried in. See PickTrim: an order-dependent rule cannot be symmetric,
                // which is what put a wedge in the ring at the bearings nearest the bow.
                float minTrim, maxTrim;
                ResolveTrimLimits(sail, state, out minTrim, out maxTrim);

                // The center of effort is captured WITH each pose, so the force and the point it
                // acts at always come from the same trim. It used to be read after the loop, which
                // left the sail at whatever the last iteration set, always the upper limit.
                //
                // That is not a small error. windcenter hangs off the sail root (Sail.cs:186 gives
                // the scaled subtree a nonzero local X offset), so swinging the root sweeps it
                // athwartships. In boat axes the yaw reduces to -arm.x * forwardForce, and forward
                // force is the largest force in the rig, so a meter of bogus athwartships offset
                // fabricates thousands of Nm. Worse, every sail was left at the SAME end of its
                // swing, so the errors shared a sign and accumulated rather than cancelling.
                // The pose the trim is imposed on top of comes from the SNAPSHOT where there is one,
                // so every bearing in a sweep swings from the same rest attitude. The restore in the
                // finally block still writes back sRot, the LIVE rotation read on entry: putting the
                // snapshot back instead would have this mod quietly undoing whatever another
                // component did to the sail while the sweep was running.
                Vector3 euler = (state.Valid ? state.LocalRotation : sRot).eulerAngles;
                float waterline = body.position.y;

                // A fixed slice of what THIS sail could make at full power, computed once and held
                // constant across every trim, so the tie window means the same thing whether the
                // sail is driving hard or barely working. Deliberately independent of the forces
                // actually measured: scaling it to the best force found so far collapses the window
                // to nothing exactly where the forces are near zero, which is the head to wind case
                // the tie-break exists for.
                float tieTolerance = sail.GetRealSailPower() * CategoryMultiplier(sail.category)
                    * 50f * SyntheticWindSpeed * TieFraction;

                float low = Mathf.Min(minTrim, maxTrim);
                float high = Mathf.Max(minTrim, maxTrim);

                TrimCandidates.Clear();
                float coarseBestTrim = 0f;
                float coarseHalfStep = 0f;

                // Two passes: a coarse scan of the whole swing, then a refinement inside the winning
                // bracket. Ten evaluations land the trim within about twelve degrees, where the
                // original three (hard over, centerd, hard over) were a fifty degree grid on a
                // hundred degree swing.
                //
                // That grid was not a rounding error, it was a systematic one. Drive against trim is
                // a smooth hump whose peak walks steadily aft as the wind goes aft, so the three
                // fixed poses were near the true optimum close hauled and badly wrong on a reach.
                // Being wrong by a DIFFERENT amount at each bearing is what makes it serious here:
                // it does not just shift the level of the readout, it bends the shape of the ring,
                // which is the one thing the ring exists to show.
                for (int pass = 0; pass < 2; pass++)
                {
                    float from = low, to = high;
                    int samples = CoarseTrimSamples;

                    if (pass == 1)
                    {
                        if (coarseHalfStep < 0.01f) break;      // no swing to refine
                        from = Mathf.Max(low, coarseBestTrim - coarseHalfStep);
                        to = Mathf.Min(high, coarseBestTrim + coarseHalfStep);
                        samples = RefineTrimSamples;
                    }

                    // Generated about the bracket's CENTRE rather than up from its lower end, so a
                    // bracket and its exact reflection sample the reflected trims bit for bit. From
                    // the lower end, from + span*k/(n-1) and its mirror are equal in algebra and not
                    // always in float, and a last-bit difference in trim is a last-bit difference in
                    // force, which is all a tie needs to fall one way on one tack and the other way
                    // on the other.
                    float center = (from + to) * 0.5f;
                    float half = (to - from) * 0.5f;
                    if (half < 0.005f)
                    {
                        // A sail with no swing at all: one pose, and no second pass to run.
                        samples = 1;
                        if (pass == 1) break;
                    }

                    int firstOfPass = TrimCandidates.Count;

                    for (int k = 0; k < samples; k++)
                    {
                        // -1 to +1 about the center, so u(k) is the exact negation of u(n-1-k).
                        float u = samples <= 1 ? 0f : 2f * k / (samples - 1) - 1f;
                        float trim = center + half * u;

                        sail.transform.localRotation = Quaternion.Euler(euler.x, euler.y, trim);
                        MUpdateForce.Invoke(sail, null);

                        Vector3 ceHere = sail.windcenter.position;
                        float unampF = ToFloat(FUnampFwd.GetValue(sail));
                        float unampS = ToFloat(FUnampSide.GetValue(sail));
                        float scale = sail.GetRealSailPower() * CategoryMultiplier(sail.category) * 50f
                            * CradleHeightCorrection(ceHere.y, waterline);

                        float sideShare = ToFloat(FOutSideShare?.GetValue(sail));
                        object abackHere = FPushingBack?.GetValue(sail);
                        bool aback = abackHere is bool && (bool)abackHere;

                        // The pose is dead abeam, so vanilla just decided the sign of a zero and it
                        // decides the other way on the other tack. Take the answer that does not
                        // depend on that sign: a force with no component along the bow is not driving
                        // her. See AthwartshipsShare. Only the +1 branch is corrected, because the
                        // -0.33 branch is already the value this settles on, so applying it twice
                        // would undo it.
                        bool clamped = false;
                        if (sideShare >= AthwartshipsShare && !aback)
                        {
                            unampF *= AbackDriveMultiplier;
                            clamped = true;
                        }

                        TrimCandidates.Add(new TrimCandidate
                        {
                            Trim = trim,
                            Fwd = unampF * scale,
                            Side = unampS * scale * 1.5f,   // the sideways component carries an extra 1.5x
                            Ce = ceHere,
                            SideShare = sideShare,
                            Aback = aback,
                            DriveClamped = clamped,
                        });
                    }

                    if (pass == 0)
                    {
                        coarseHalfStep = samples <= 1 ? 0f : half * 2f / (samples - 1);
                        int coarseWinner = PickTrim(firstOfPass, TrimCandidates.Count - firstOfPass,
                            tieTolerance, windLateral);
                        if (coarseWinner < 0) break;
                        coarseBestTrim = TrimCandidates[coarseWinner].Trim;
                    }
                }

                int winner = PickTrim(0, TrimCandidates.Count, tieTolerance, windLateral);
                if (winner < 0) return false;

                float bestFwd = TrimCandidates[winner].Fwd;
                float bestSide = TrimCandidates[winner].Side;
                Vector3 ce = TrimCandidates[winner].Ce;
                Vector3 armLocal = boat.InverseTransformVector(ce - body.position);  // about the HULL ORIGIN

                // Boat axes: x starboard, z forward. Unity's cross product about up reduces to
                // arm.z * force.x - arm.x * force.z, which is exactly these two terms. Taken in the
                // boat's own frame rather than through a world-space cross product so the two halves
                // are separable, and so they provably sum to the same number.
                float yawFromSide = armLocal.z * bestSide;
                float yawFromThrust = -armLocal.x * bestFwd;

                c.Sail = sail;
                c.Name = sail.sailName;
                c.CenterOfEffort = ce;
                c.HeightAboveWaterline = ce.y - waterline;
                c.ForwardForce = bestFwd;
                c.SideForce = bestSide;
                c.LeverFromOrigin = armLocal.z;
                c.YawMoment = yawFromSide + yawFromThrust;
                c.YawFromSide = yawFromSide;
                c.YawFromThrust = yawFromThrust;
                c.CeTracksResizes = CeTracksResizes(sail);
                c.Unroll = unroll;
                c.Trim = TrimCandidates[winner].Trim;
                c.SideShare = TrimCandidates[winner].SideShare;
                c.Aback = TrimCandidates[winner].Aback;
                c.DriveClamped = TrimCandidates[winner].DriveClamped;
                c.Arm = armLocal;
                c.MinTrim = minTrim;
                c.MaxTrim = maxTrim;
                c.RestEuler = euler;
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
                RestoreShadowCol(sail, sShadow);
            }
        }

        /// <summary>
        /// The swing limits to trim between, with vanilla's own fallback when the sail has none yet.
        ///
        /// Sail.minAngle and Sail.maxAngle are back-filled from the HingeJoint's limits in Sail.Start
        /// (Sail.cs:322-329), and Start never runs on a sail the player is previewing:
        /// ShipyardSailInstaller.AddNewSail sets selectedSail.enabled = false in the same call that
        /// instantiates the prefab (ShipyardSailInstaller.cs:55), which is exactly why the next line
        /// hand-calls SetSailArea, the other thing Start does. Every other writer of those fields
        /// runs at install time or later.
        ///
        /// Left alone, all three trim iterations then evaluated the identical dead-center pose, so a
        /// not-yet-bought sail got no trim search at all and was sheeted flat amidships. For a
        /// fore-and-aft sail that is close to its worst pose, because Sail.GetSailForceDirection puts
        /// the force along the sail's own normal, so a new headsail barely registered in the side
        /// force and barely moved the balance. The bias was one way: every unbought sail was
        /// under-weighted against every fitted one, and "what does adding this jib do to the helm" is
        /// the question the mod exists to answer.
        /// </summary>
        private static void ResolveTrimLimits(Sail sail, SailState state,
            out float minTrim, out float maxTrim)
        {
            // From the sweep's snapshot when there is one. These are not constants: Mast
            // CheckInitialAngles rewrites them at the end of a collision sweep spanning many
            // FixedUpdates, so reading them live makes one bearing's swing range differ from the
            // next one's for reasons that have nothing to do with the wind.
            minTrim = state.Valid ? state.MinAngle : sail.minAngle;
            maxTrim = state.Valid ? state.MaxAngle : sail.maxAngle;
            if (minTrim != 0f && maxTrim != 0f) return;

            // Each limit tested on its own, exactly as vanilla does at Sail.cs:322-329. Requiring
            // BOTH to be zero before reaching for the hinge would leave a sail authored with, say,
            // minAngle -80 and maxAngle 0 stuck with no upper limit, so half its swing would never
            // be explored: the same one-way under-weighting the fallback exists to remove.
            var joint = sail.GetComponent<HingeJoint>();
            if (joint != null)
            {
                if (minTrim == 0f) minTrim = joint.limits.min;
                if (maxTrim == 0f) maxTrim = joint.limits.max;
            }

            if (minTrim == 0f && maxTrim == 0f)
            {
                WarnOnce("trim:" + sail.sailName, $"[RigModel] '{sail.sailName}' has no swing limits "
                    + "and no hinge to take them from, so it is judged sheeted amidships. Its "
                    + "contribution to the balance will read low.");
            }
        }

        /// <summary>
        /// Cancels the shipyard cradle's height out of the game's wind-height multiplier.
        ///
        /// Sail.cs:411 multiplies every sail's force by GetWindHeightMult(windcenter.position.y),
        /// which is Lerp(0.8, 1.4, InverseLerp(0, 22, worldY)) (Sail.cs:401-405): it assumes sea
        /// level is Y = 0. Shipyard.MoveShip parks the hull on a cradle several meters above that,
        /// and how many meters is a property of which island the player is standing on.
        ///
        /// The error is not a common scale factor that would divide out, because the curve
        /// saturates. At a cradle 6.8 m up, a low staysail at a true 4 m is evaluated at 10.8 m and
        /// gains about 20 percent, while a topgallant at a true 20 m is evaluated past the top of
        /// the curve and gains 4. Low sails gain weight against tall ones, and low and tall sails do
        /// not sit at the same station fore and aft, so the combined center moves. The same boat
        /// with the same rig read differently at two different shipyards with nothing on screen to
        /// explain it.
        ///
        /// Correcting the ratio rather than moving the boat works because the whole force is linear
        /// in totalWindForce: Sail.cs:415 lerps between totalWindForce * upwindEfficiency and
        /// totalWindForce, which is totalWindForce times a factor of the angle alone, and every
        /// later step scales it or picks a sign from an angle.
        /// </summary>
        private static float CradleHeightCorrection(float centerWorldY, float waterlineWorldY)
        {
            float asEvaluated = WindHeightMult(centerWorldY);
            if (asEvaluated < 0.0001f) return 1f;
            return WindHeightMult(centerWorldY - waterlineWorldY) / asEvaluated;
        }

        /// <summary>Sail.GetWindHeightMult, which is private (Sail.cs:401-405).</summary>
        private static float WindHeightMult(float worldY) =>
            Mathf.Lerp(0.8f, 1.4f, Mathf.InverseLerp(0f, 22f, worldY));

        private static void RestoreShadowCol(Sail sail, object saved)
        {
            if (FShadowCol == null) return;
            try { FShadowCol.SetValue(sail, saved); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] could not restore a sail's shadow collider: " + e.Message);
            }
        }

        /// <summary>
        /// Whether this sail's center of effort actually moves when the sail is resized.
        ///
        /// The transform a resize scales is <c>cloth.transform.parent</c>. That is vanilla's own:
        /// Sail.ChangeScale takes it at Sail.cs:140 and writes its localScale at Sail.cs:185, and
        /// Sail.UpdateScaledInstallHeight reads it back at Sail.cs:202-206. Shipyard Expansion
        /// scales the same object by a different route (SailScaler.cs:36 finds it as the Animator's
        /// transform, SailScaler.cs:187 scales it) and then calls vanilla's UpdateScaledInstallHeight
        /// at SailScaler.cs:189, which only works because it is the same transform.
        ///
        /// So the question is simply whether windcenter is inside it. It is, on every stock sail.
        /// It was not on Shipyard Expansion's three added lug sails up to SE v0.10.1, where
        /// CopySail reparented the center of effort out to the sail root (SE Util.cs:348) and it
        /// stayed frozen through a resize. Reported as NANDbrew/ShipyardExpansion#30 and fixed in
        /// SE v0.11.0 by deleting the reparenting outright, vanilla's sail prefabs having been
        /// restructured at some point so that windcenter already lives inside the scaled subtree.
        ///
        /// The check is unchanged by that, and deliberately: it measures parentage per sail at
        /// runtime rather than testing an SE version, so it reports the condition wherever it turns
        /// up and stops reporting it when the sail is fixed.
        /// </summary>
        private static bool CeTracksResizes(Sail sail)
        {
            try
            {
                if (sail.cloth == null || sail.cloth.transform.parent == null)
                {
                    // Nothing to compare against. Say so rather than quietly claiming it tracks.
                    WarnOnce("cloth:" + sail.sailName, $"[RigModel] '{sail.sailName}' has no cloth parent; "
                        + "cannot tell whether its center of effort follows a resize.");
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
        /// boat rather than a flat number of meters. Half a meter of offset is nothing on a brig
        /// and most of the boat on a dinghy, so a fixed threshold necessarily lies to one of them.
        ///
        /// Rig geometry is excluded, and it has to be excluded explicitly. SailShadowCol.Awake
        /// leaves the shadow mesh a NON-trigger collider for staysails only (SailShadowCol.cs:21-24),
        /// so every jib's shadow collider is a solid collider hanging off the bowsprit, ahead of the
        /// stem. Counting those grew the measured hull, which widened the balanced band, at the exact
        /// moment the player added the sails most likely to unbalance the rig. They also set the
        /// ring's radius, so the ring visibly grew for a change that never touched the hull. The
        /// masts' walk capsules are the same trap wearing a different coat, and are excluded by
        /// reference rather than by parentage. So are the shipyard's fitted parts, the house and
        /// cabin and keel options, because a hull does not get shorter when a house goes on it:
        /// see FittedPartRoots.
        ///
        /// Extent is taken from each collider's OWN box, through its own transform, rather than by
        /// projecting the world-space AABB. A world AABB of a yawed hull is bigger than the hull:
        /// projecting its two extreme corners onto the boat's fore-aft axis overshoots the true half
        /// length by half the beam times sin(2 * yaw), which is worst at 45 degrees and zero on an
        /// axis. Every shipyard cradle sits at its own heading, so the same boat measured differently
        /// at different yards and the balanced band moved with it.
        /// </summary>
        private static float MeasureHullLength(GameObject boatRoot, Transform boat,
            out Vector3 hullCenterLocal)
        {
            hullCenterLocal = Vector3.zero;

            try
            {
                Collider[] colliders = boatRoot.GetComponentsInChildren<Collider>();
                HashSet<Transform> mastWalkCols = MastWalkColliders(boatRoot);
                HashSet<Transform> fittedParts = FittedPartRoots(boatRoot);

                HullSpan span = SpanOf(colliders, boat, true, mastWalkCols, fittedParts);
                string basis = HullAloneBasis;

                if (!Sane(span.Length))
                {
                    // A boat built entirely out of part options would have nothing left. Counting
                    // its parts is still a better answer than counting its rig, so that is the next
                    // step down, not the last one.
                    span = SpanOf(colliders, boat, true, mastWalkCols, null);
                    basis = "the hull and its fitted parts";
                    if (Sane(span.Length))
                        WarnOnce("hull:parts", "[RigModel] nothing was left of the hull once its "
                            + "fitted parts were set aside; hull length "
                            + $"{span.Length:F1} m includes them, so adding or removing a part can "
                            + "resize the ring.");
                }

                if (!Sane(span.Length))
                {
                    // Nothing left after filtering. Better a hull length that includes the rig than
                    // the fixed default, but say so, because it widens the balanced band.
                    span = SpanOf(colliders, boat, false, mastWalkCols, null);
                    basis = "the whole boat, rig included";
                    if (Sane(span.Length))
                        WarnOnce("hull:filtered", "[RigModel] no hull colliders were found apart "
                            + $"from rig geometry; hull length {span.Length:F1} m includes the "
                            + "masts and sails.");
                }

                if (Sane(span.Length))
                {
                    hullCenterLocal = span.Center;
                    LogHullExtent(boatRoot, colliders, boat, mastWalkCols, fittedParts, span, basis);
                    return span.Length;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] hull length could not be measured: " + e.Message);
            }

            hullCenterLocal = Vector3.zero;
            WarnOnce("hull:default", "[RigModel] the hull's colliders gave nothing measurable, so the "
                + $"balanced band is scaled to a nominal {DefaultHullLength:F0} m hull.");
            return DefaultHullLength;
        }

        private static bool Sane(float length) => length > 1f && length < 500f;

        private const string HullAloneBasis = "the hull alone";

        /// <summary>
        /// A measured box, and which collider set each of the ends a player would recognize, so the
        /// log can say what the ring is sized by instead of only how big it came out.
        /// </summary>
        private struct HullSpan
        {
            public float Length;
            public Vector3 Center;
            public Collider Bow, Stern, Top;
            public float BowZ, SternZ, TopY;
        }

        /// <summary>The boat and length last logged, so a steady measurement logs once.</summary>
        private static int _loggedHullBoat;
        private static float _loggedHullLength;
        private static string _loggedHullBasis;

        /// <summary>Forget the last hull report, so the next shipyard visit logs its own.</summary>
        public static void ResetHullLog()
        {
            _loggedHullBoat = 0;
            _loggedHullLength = 0f;
            _loggedHullBasis = null;
        }

        /// <summary>
        /// Says what the hull was measured from and what set its ends, when that changes by more
        /// than ten centimeters. Two open questions hang on it. A player reported the ring shrinking
        /// when a house was fitted to a junk, which a hull measurement should never do, and the
        /// v0.15.10 breadcrumb put that junk's measured box center 9.4 m above the boat root, which
        /// no hull is tall enough to explain. Both are "something that is not the hull is being
        /// measured as hull", and naming the colliders at the ends answers that from one log.
        ///
        /// When fitted parts were set aside, the length they would have given is logged too, so
        /// the effect of this very filter is visible rather than assumed.
        /// </summary>
        private static void LogHullExtent(GameObject boatRoot, Collider[] colliders, Transform boat,
            HashSet<Transform> mastWalkCols, HashSet<Transform> fittedParts, HullSpan span, string basis)
        {
            int id = boatRoot.GetInstanceID();
            if (id == _loggedHullBoat && basis == _loggedHullBasis
                && Mathf.Abs(span.Length - _loggedHullLength) <= 0.1f) return;

            _loggedHullBoat = id;
            _loggedHullLength = span.Length;
            _loggedHullBasis = basis;

            string withParts = "";
            if (basis == HullAloneBasis && fittedParts != null && fittedParts.Count > 0)
            {
                HullSpan counted = SpanOf(colliders, boat, true, mastWalkCols, null);
                withParts = Mathf.Abs(counted.Length - span.Length) > 0.05f
                    ? $" Counting the fitted parts would have made it {counted.Length:F1} m "
                        + $"(bow '{NameOf(counted.Bow, boat)}, stern '{NameOf(counted.Stern, boat)}, "
                        + $"top '{NameOf(counted.Top, boat)} at {counted.TopY:F1} m)."
                    : " The fitted parts do not change it.";
            }

            string byName = "";
            foreach (Collider collider in colliders)
            {
                if (collider == null || collider.isTrigger || !NamedAsRig(collider)) continue;
                if (collider.GetComponentInParent<Sail>() != null) continue;
                if (collider.GetComponentInParent<Mast>() != null) continue;
                byName += (byName.Length == 0 ? " Set aside by name: '" : ", '") + NameOf(collider, boat);
            }
            if (byName.Length > 0) byName += ".";

            Plugin.Log.LogInfo($"[RigModel] hull {span.Length:F1} m, measured from {basis}: bow "
                + $"'{NameOf(span.Bow, boat)} at {span.BowZ:F1} m, stern '{NameOf(span.Stern, boat)} "
                + $"at {span.SternZ:F1} m, top '{NameOf(span.Top, boat)} at {span.TopY:F1} m above "
                + "the root." + withParts + byName);
        }

        /// <summary>
        /// A collider's path back to the boat, with its layer, which is what a filter has to be
        /// written against.
        ///
        /// The parent's name alone was not enough. It named the collider that puts the junk's
        /// measured box 20 m in the air, 'structure_container/sail capsule col', and that is as far
        /// as it got: the name is not in the decompiled scripts, so it is scene geometry, and
        /// whether it can be excluded by layer, by parentage or only by name cannot be told from a
        /// name. Rig geometry has now hidden from this filter three times, under a shadow collider,
        /// a mast's walk capsule and this, so the log says enough to write the next filter without
        /// another round trip.
        /// </summary>
        private static string NameOf(Collider collider, Transform boat)
        {
            if (collider == null) return "?";

            string path = collider.transform.name;
            for (Transform t = collider.transform.parent; t != null && t != boat; t = t.parent)
                path = t.name + "/" + path;

            int layer = collider.gameObject.layer;
            string layerName = LayerMask.LayerToName(layer);
            return $"{path}' (layer {layer}"
                + (string.IsNullOrEmpty(layerName) ? "" : " '" + layerName + "'")
                + $", {collider.GetType().Name})";
        }

        /// <summary>
        /// Every mast's walk collider, which is rig geometry that the parent walk below cannot see.
        /// A mast's walking surface does not hang off the mast: Mast.walkColMast is a reference to a
        /// capsule that lives in the boat's separate walk-collider hierarchy, registered from the
        /// other end (Mast.cs:195), so GetComponentInParent&lt;Mast&gt; on it returns null and it was
        /// being measured as hull. It is solid, it is as tall as the mast, and every mast edit moves
        /// and rescales it, which is the one property a hull measurement must not have.
        /// </summary>
        private static HashSet<Transform> MastWalkColliders(GameObject boatRoot)
        {
            var walkCols = new HashSet<Transform>();

            try
            {
                var refs = boatRoot.GetComponent<BoatRefs>();
                if (refs == null || refs.masts == null) return walkCols;

                foreach (Mast mast in refs.masts)
                {
                    if (mast == null || mast.walkColMast == null) continue;
                    walkCols.Add(mast.walkColMast);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] the masts' walk colliders could not be listed: "
                    + e.Message);
            }

            return walkCols;
        }

        /// <summary>
        /// Everything the shipyard's parts menu can add or swap: every option of every part, its
        /// child objects, and its walk collider, all of which BoatPart.SetOptionEnabled switches on
        /// and off together (BoatPart.cs:21-34). Houses, cabins, rails, bowsprits and keel options
        /// are all of this kind.
        ///
        /// They are set aside because the hull length is meant to be a property of the hull, and
        /// these are not. Counted, they made the measurement depend on what was fitted: a player
        /// reported the ring shrinking when a house went onto a junk, which can only happen if the
        /// option the house replaced reached further fore or aft than the house does. The ring's
        /// radius and the balanced band both scale with this number, so the verdict moved with the
        /// furniture.
        ///
        /// The boat root and the boat's walk-collider root are never added, whatever an option
        /// claims, because excluding either would exclude the hull with it.
        /// </summary>
        private static HashSet<Transform> FittedPartRoots(GameObject boatRoot)
        {
            var roots = new HashSet<Transform>();

            try
            {
                var parts = boatRoot.GetComponent<BoatCustomParts>();
                if (parts == null || parts.availableParts == null) return roots;

                var refs = boatRoot.GetComponent<BoatRefs>();
                Transform walkRoot = refs != null ? refs.walkCol : null;

                foreach (BoatPart part in parts.availableParts)
                {
                    if (part == null || part.partOptions == null) continue;

                    foreach (BoatPartOption option in part.partOptions)
                    {
                        if (option == null) continue;
                        AddPartRoot(roots, option.transform, boatRoot.transform, walkRoot);
                        if (option.walkColObject != null)
                            AddPartRoot(roots, option.walkColObject.transform, boatRoot.transform, walkRoot);
                        if (option.childOptions == null) continue;
                        foreach (GameObject child in option.childOptions)
                            if (child != null) AddPartRoot(roots, child.transform, boatRoot.transform, walkRoot);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] the boat's fitted parts could not be listed: "
                    + e.Message);
            }

            return roots;
        }

        private static void AddPartRoot(HashSet<Transform> roots, Transform part, Transform boat,
            Transform walkRoot)
        {
            if (part == null || part == boat || part == walkRoot) return;
            roots.Add(part);
        }

        private static HullSpan SpanOf(Collider[] colliders, Transform boat, bool hullOnly,
            HashSet<Transform> mastWalkCols, HashSet<Transform> fittedParts)
        {
            float minZ = float.MaxValue, maxZ = float.MinValue;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            var span = new HullSpan();

            foreach (Collider collider in colliders)
            {
                if (collider == null || collider.isTrigger) continue;
                if (hullOnly && IsRigGeometry(collider, mastWalkCols)) continue;
                if (IsUnder(collider.transform, fittedParts)) continue;

                Bounds box;
                Transform frame;
                LocalBoxOf(collider, out box, out frame);

                Vector3 center = box.center, extents = box.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);

                    // A null frame means the box is already in world space.
                    Vector3 point = center + offset;
                    if (frame != null) point = frame.TransformPoint(point);

                    Vector3 local = boat.InverseTransformPoint(point);
                    if (local.z < minZ) { minZ = local.z; span.Stern = collider; }
                    if (local.z > maxZ) { maxZ = local.z; span.Bow = collider; }
                    if (local.x < minX) minX = local.x;
                    if (local.x > maxX) maxX = local.x;
                    if (local.y < minY) minY = local.y;
                    if (local.y > maxY) { maxY = local.y; span.Top = collider; }
                }
            }

            // The midpoint of the hull's own box, in boat axes. Kept alongside the span because the
            // two answer different questions: the span sizes the ring and the balanced band, and the
            // midpoint says where the hull actually IS relative to the transform it hangs off, which
            // the first field report proved is not the same place on every boat.
            if (maxZ > minZ)
            {
                span.Length = maxZ - minZ;
                span.Center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
                span.BowZ = maxZ;
                span.SternZ = minZ;
                span.TopY = maxY;
            }

            return span;
        }

        /// <summary>
        /// Layer 24 is where SailShadowCol puts itself (SailShadowCol.cs:20); the parent walk catches
        /// everything else hanging off a mast or a sail. The masts' walk colliders are passed in
        /// because they hang off neither: see MastWalkColliders.
        /// </summary>
        private static bool IsRigGeometry(Collider collider, HashSet<Transform> mastWalkCols)
        {
            if (collider.gameObject.layer == 24) return true;
            if (collider.GetComponentInParent<Sail>() != null) return true;
            if (collider.GetComponentInParent<Mast>() != null) return true;
            if (IsUnder(collider.transform, mastWalkCols)) return true;
            return NamedAsRig(collider);
        }

        /// <summary>
        /// Rig words a collider's own name may carry. The last resort, and it exists because the
        /// measurement asked the log what was topping the junk's hull box and the answer was
        /// 'medi medium new/structure_container/sail capsule col', a solid capsule 20.4 m up. It
        /// hangs off the hull container rather than off a sail or a mast, so parentage does not
        /// catch it, and nothing else about it separates it from hull either: it sits on layer 0,
        /// Default, which half the game shares, and its being a capsule means nothing because some
        /// hulls have a genuine capsule of their own (BoatDamage takes one off the boat root).
        ///
        /// Its name is the only thing that distinguishes it, so the name is what is used, and every
        /// collider set aside this way is named in the log so a wrong guess is visible rather than
        /// silent. Matched on the collider's own name and its parent's, both lowercased.
        /// </summary>
        private static readonly string[] RigWords = { "sail", "mast", "spar", "boom", "gaff" };

        internal static bool NamedAsRig(Collider collider)
        {
            if (collider == null) return false;

            if (NameCarriesRigWord(collider.transform.name)) return true;

            Transform parent = collider.transform.parent;
            return parent != null && NameCarriesRigWord(parent.name);
        }

        private static bool NameCarriesRigWord(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            string lowered = name.ToLowerInvariant();
            foreach (string word in RigWords)
                if (lowered.Contains(word)) return true;

            return false;
        }

        /// <summary>True if the transform is one of the roots or anywhere beneath one.</summary>
        private static bool IsUnder(Transform transform, HashSet<Transform> roots)
        {
            if (roots == null || roots.Count == 0) return false;

            for (Transform t = transform; t != null; t = t.parent)
                if (roots.Contains(t)) return true;

            return false;
        }

        /// <summary>
        /// A collider's own box in its own frame, so the caller can transform its corners rather
        /// than a world-space AABB that has already lost the rotation. A null frame means the box
        /// came back in world space, which is the last resort for a collider with no readable
        /// local shape.
        /// </summary>
        private static void LocalBoxOf(Collider collider, out Bounds local, out Transform frame)
        {
            var box = collider as BoxCollider;
            if (box != null)
            {
                local = new Bounds(box.center, box.size);
                frame = box.transform;
                return;
            }

            var mesh = collider as MeshCollider;
            if (mesh != null && mesh.sharedMesh != null)
            {
                local = mesh.sharedMesh.bounds;
                frame = mesh.transform;
                return;
            }

            var sphere = collider as SphereCollider;
            if (sphere != null)
            {
                local = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                frame = sphere.transform;
                return;
            }

            var capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                var size = Vector3.one * (capsule.radius * 2f);
                if (capsule.direction == 0) size.x = capsule.height;
                else if (capsule.direction == 1) size.y = capsule.height;
                else size.z = capsule.height;

                local = new Bounds(capsule.center, size);
                frame = capsule.transform;
                return;
            }

            // Terrain or a wheel collider: nothing local to read, so fall back to the world box and
            // accept the yaw inflation for that one collider.
            local = collider.bounds;
            frame = null;
        }

        /// <summary>Used only when the hull's colliders give nothing sane to measure.</summary>
        public const float DefaultHullLength = 20f;

        private static float CategoryMultiplier(SailCategory category)
        {
            if (category == SailCategory.junk) return 0.75f;
            if (category == SailCategory.gaff) return 0.85f;
            return 1f;
        }

        /// <summary>
        /// One pose tried during the trim search, kept so the winner can be chosen from the whole
        /// set rather than by a comparison against whatever has won so far.
        /// </summary>
        private struct TrimCandidate
        {
            public float Trim;
            public float Fwd;
            public float Side;
            public Vector3 Ce;

            /// <summary>
            /// Vanilla's own two readings at this pose, carried along purely so the mirror audit can
            /// report them for the WINNING pose rather than for whichever pose the loop happened to
            /// leave the sail in.
            ///
            /// Sail.cs:443 sets outSidewayShare = num3/90, the angle between the sail's force and the
            /// boat's bow over ninety, so 1 means the force is dead abeam. Sail.cs:431 sets
            /// pushingBoatBackwards from num3 > 90, the test that also drops the drive multiplier
            /// from 1 to -0.33 (Sail.cs:427-429).
            ///
            /// Those two are the same crossing, and drive is DISCONTINUOUS across it while the share
            /// is not. Where no trim drives at all, the largest drive available is therefore found
            /// hard against that step, at a pose whose side force is close to maximal. A pose with a
            /// share near 1 and the flag just barely clear is the signature of a reading decided by
            /// the step rather than by the rig, and there is no way to see that from a force alone.
            /// </summary>
            public float SideShare;
            public bool Aback;

            /// <summary>
            /// Set when this pose was dead abeam and its drive was resolved against vanilla's noisy
            /// branch. Diagnostic: it is how the audit shows the correction firing where it should
            /// and, more usefully, shows it firing on BOTH tacks rather than one.
            /// </summary>
            public bool DriveClamped;
        }

        /// <summary>
        /// Scratch for the trim search, reused across sails and bearings: the ring runs this ten
        /// times a sail, thirty six times a rebuild, and a fresh list each time is garbage the
        /// shipyard does not need. Never held across a call.
        /// </summary>
        private static readonly List<TrimCandidate> TrimCandidates = new List<TrimCandidate>(16);

        /// <summary>
        /// How close two trims have to be in magnitude before they count as one pose and its own
        /// reflection, in degrees. Far below the resolution of the search, far above the last bit of
        /// building the grid.
        /// </summary>
        private const float TrimTieEpsilon = 0.001f;

        /// <summary>
        /// The winning pose out of the whole set of trims tried: most drive, ties resolved toward
        /// the centerd pose, and a tie between a pose and its own reflection resolved toward the one
        /// whose side force pushes to LEEWARD.
        ///
        /// A rule over the whole set, not a comparison against a running best, and that is the
        /// entire point. Samples are generated in ascending trim order, so a running comparison
        /// answers differently depending on visit order; a mirrored wind produces the same forces in
        /// REVERSE order, so an order-dependent rule CANNOT return mirrored poses for mirrored
        /// winds.
        ///
        /// That is what put a wedge in the ring at the four bearings nearest the bow. With the
        /// apparent wind close to the boat's own axis, Sail.cs:416 zeroes the force below 13 degrees
        /// of relative wind angle and Sail.cs:427-429 turns the rest of the swing negative, so the
        /// maximum is a flat plateau with exact ties in it. The two ends of the swing tie, and the
        /// old magnitude test was STRICT, so the second-visited end could never displace the first:
        /// both tacks came away braced the same way round, at one physical yard angle, so the forces
        /// genuinely differed rather than mirroring. Bearing 5 read a 78 percent side-force share
        /// against 95 percent at 355, identically in five consecutive sweeps.
        ///
        /// Leeward as the last resort, rather than a fixed sign: it is the pose a crew would
        /// actually be in, the two tied poses have equal drive so it costs nothing, and unlike any
        /// constant it FLIPS with the wind. That is what makes the whole rule odd under mirroring,
        /// which is the property the ring needs and the property the old rule did not have.
        /// </summary>
        private static int PickTrim(int from, int count, float tieTolerance, float windLateral)
        {
            if (count <= 0) return -1;

            float maxFwd = float.NegativeInfinity;
            for (int i = from; i < from + count; i++)
                if (TrimCandidates[i].Fwd > maxFwd) maxFwd = TrimCandidates[i].Fwd;

            int best = -1;
            for (int i = from; i < from + count; i++)
            {
                TrimCandidate c = TrimCandidates[i];
                if (c.Fwd < maxFwd - tieTolerance) continue;      // not in the tie set at all
                if (best < 0) { best = i; continue; }

                TrimCandidate b = TrimCandidates[best];
                float da = Mathf.Abs(c.Trim), db = Mathf.Abs(b.Trim);

                if (da < db - TrimTieEpsilon) { best = i; continue; }
                if (da > db + TrimTieEpsilon) continue;

                // Same drive, same trim magnitude: one pose and its own reflection, and nothing
                // measured separates them. Take the one pushing to leeward.
                if (windLateral == 0f) continue;
                bool cLee = c.Side * windLateral > 0f;
                bool bLee = b.Side * windLateral > 0f;
                if (cLee && !bLee) best = i;
            }

            return best;
        }

        /// <summary>What one sail was at the instant a sweep started.</summary>
        public struct SailState
        {
            public float Unroll;
            public float MinAngle;
            public float MaxAngle;
            public Quaternion LocalRotation;
            public bool Valid;

            /// <summary>
            /// The shipyard's collision check for this sail was still sweeping when the rig was
            /// captured, so MinAngle and MaxAngle are not its final limits. See LogSwingLimits.
            /// </summary>
            public bool Checking;
        }

        /// <summary>
        /// Every input to the model that some other component on the boat can move between frames,
        /// captured in one instant.
        ///
        /// The ring evaluates thirty six bearings at roughly one per frame, so without this the
        /// picture it draws is not of ONE rig: the bearings on one bow are read from a rig up to
        /// thirty frames older than the bearings on the other. On a live boat that is not
        /// hypothetical. Five consecutive readings at an unchanged 267 degrees alternated between
        /// -2.31 m and -1.69 m with nothing touched, a strict period-two flip, and one sweep
        /// straddled it, taking its port half from one state and its starboard half from the other.
        ///
        /// Only fields with another owner are captured. Sail.currentUnroll is rewritten every frame
        /// by RopeControllerMirror.Update (RopeControllerMirror.cs:22); minAngle and maxAngle are
        /// written by Mast.CheckInitialAngles (Mast.cs:174-176) at the end of a collision sweep that
        /// runs over many FixedUpdates; and the HingeJoint limits the trim range falls back to are
        /// rewritten every frame by SquareAngleMaster and JibAngleMaster, the latter with
        /// Time.deltaTime, Wind.currentWind and Random.Range in it. Everything else the model reads
        /// is either constant for the session or already cancelled.
        /// </summary>
        public sealed class RigSnapshot
        {
            public readonly Dictionary<Sail, SailState> Sails = new Dictionary<Sail, SailState>();
        }

        /// <summary>Freezes the rig so a whole sweep describes one boat rather than thirty.</summary>
        public static RigSnapshot CaptureRig(GameObject boatRoot)
        {
            var snapshot = new RigSnapshot();
            if (boatRoot == null) return snapshot;

            var refs = boatRoot.GetComponent<BoatRefs>();
            if (refs == null || refs.masts == null) return snapshot;

            foreach (Mast mast in refs.masts)
            {
                if (mast == null || mast.sails == null) continue;
                if (!mast.gameObject.activeInHierarchy) continue;

                foreach (GameObject sailObject in mast.sails)
                {
                    if (sailObject == null) continue;
                    var sail = sailObject.GetComponent<Sail>();
                    if (sail == null || sail.windcenter == null) continue;

                    snapshot.Sails[sail] = new SailState
                    {
                        Unroll = sail.currentUnroll,
                        MinAngle = sail.minAngle,
                        MaxAngle = sail.maxAngle,
                        LocalRotation = sail.transform.localRotation,
                        Valid = true,
                        Checking = CollisionCheckRunning(sail),
                    };
                }
            }

            return snapshot;
        }

        private static bool CollisionCheckRunning(Sail sail)
        {
            try
            {
                var connections = sail.GetComponent<SailConnections>();
                return connections != null && connections.colChecker != null && connections.colChecker.checking;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A sail's name with the mast it hangs on, since a rig can carry two of a name.</summary>
        internal static string SailLabel(Sail sail, string fallbackName)
        {
            string name = sail != null && !string.IsNullOrEmpty(sail.sailName) ? sail.sailName : fallbackName;
            Mast mast = sail != null ? sail.GetComponentInParent<Mast>() : null;
            return mast != null ? $"'{name}' (mast {mast.orderIndex})" : $"'{name}'";
        }

        private static string _loggedLimits;

        /// <summary>Forget the last swing limit report, so the next shipyard visit logs its own.</summary>
        public static void ResetLimitsLog() => _loggedLimits = null;

        /// <summary>
        /// Logs every sail's swing limits, whenever the set of them changes.
        ///
        /// Added to explain a ring whose port and starboard halves disagreed badly on a fourteen-sail
        /// rig. The mirror audit showed the sails making the same force on both tacks but acting in
        /// different places, and the sails it named were not swinging symmetrically: a square yard
        /// bracing to -77.9 on one tack and only -66.8 on the other where it should reach +77.9. The
        /// trim search can only use the limits the sail has, so a lopsided limit is a lopsided ring.
        ///
        /// This answers whether that is the boat or the mod. A sail's limits come
        /// from the shipyard's collision check (ShipyardSailColChecker), which swings a probe through
        /// the sail's range in 5 degree steps over many physics frames and cuts the range at the first
        /// collision on each side. A crowded rig can genuinely collide sooner on one side. But the
        /// limits are only written back to the sail when that sweep ends (Mast.CheckInitialAngles,
        /// Mast.cs:166-176), and AttachSailToMast copies the checker's in-progress values straight
        /// across (Mast.cs:192-193), so a rig captured mid-check carries limits that are about to
        /// change. Each sail is flagged when its check was still running, and when its range is not
        /// symmetric, so one log line says which of the two is happening.
        /// </summary>
        public static void LogSwingLimits(RigSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Sails.Count == 0) return;

            try
            {
                var lines = new List<string>();
                foreach (KeyValuePair<Sail, SailState> entry in snapshot.Sails)
                {
                    if (entry.Key == null) continue;
                    SailState state = entry.Value;

                    string line = $"{SailLabel(entry.Key, entry.Key.name)}: {state.MinAngle:+0;-0;0} to "
                        + $"{state.MaxAngle:+0;-0;0} deg";

                    float lopsided = Mathf.Abs(state.MinAngle + state.MaxAngle);
                    if (lopsided >= 1f) line += $", reaches {lopsided:F0} deg further one way than the other";
                    if (state.Checking) line += ", COLLISION CHECK STILL RUNNING, these limits are not final";

                    lines.Add(line);
                }

                lines.Sort(StringComparer.Ordinal);
                string table = string.Join("\n", lines.ToArray());
                if (table == _loggedLimits) return;
                _loggedLimits = table;

                Plugin.Log.LogInfo($"[Limits] swing limits for {lines.Count} sail(s), as this sweep uses them:");
                foreach (string line in lines) Plugin.Log.LogInfo("[Limits]   " + line);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Limits] could not report swing limits: " + e.Message);
            }
        }

        /// <summary>
        /// Reports any sail whose limits or collision check state changed while a sweep was running,
        /// because that sweep was then computed from limits the sail no longer has. Called when the
        /// sweep finishes, against the snapshot it started from.
        /// </summary>
        public static void LogLimitsChangedDuringSweep(RigSnapshot snapshot)
        {
            if (snapshot == null) return;

            try
            {
                foreach (KeyValuePair<Sail, SailState> entry in snapshot.Sails)
                {
                    Sail sail = entry.Key;
                    if (sail == null) continue;
                    SailState was = entry.Value;

                    bool checking = CollisionCheckRunning(sail);
                    if (Mathf.Approximately(was.MinAngle, sail.minAngle)
                        && Mathf.Approximately(was.MaxAngle, sail.maxAngle)
                        && was.Checking == checking) continue;

                    Plugin.Log.LogWarning($"[Limits] {SailLabel(sail, sail.name)} changed during the sweep: "
                        + $"{was.MinAngle:+0;-0;0} to {was.MaxAngle:+0;-0;0} deg"
                        + (was.Checking ? " (check running)" : "")
                        + $" became {sail.minAngle:+0;-0;0} to {sail.maxAngle:+0;-0;0} deg"
                        + (checking ? " (check running)" : "")
                        + ". The ring just drawn used the old limits; the next rig change redraws it.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Limits] could not compare swing limits: " + e.Message);
            }
        }

        private static float ToFloat(object o) => o is float f ? f : 0f;

        private static void Restore(FieldInfo field, object target, object value)
        {
            if (field == null || value == null) return;
            try { field.SetValue(target, value); } catch { }
        }
    }
}
