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

        /// <summary>The size of this sail's horizontal force. The arrows, their labels and the center of effort all use it.</summary>
        public float ForceMagnitude => new Vector2(SideForce, ForwardForce).magnitude;

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
        /// The fraction of this sail's force taken away by other sails upwind of it, 0 for clear air.
        /// Every force and moment in this struct already has it applied. See RigModel.ApplySailShading.
        /// </summary>
        public float Shading;

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
        /// Signed, positive to starboard. Printed in the debug summary.
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

        /// <summary>
        /// The yaw the heel adds, Nm, included in YawMoment. Zero for an upright reading. With the
        /// boat heeled, the rudder must also hold the part of the sails' pitching moment that now turns
        /// her about the vertical, which is the drive acting off to leeward. See Hydrostatics.
        /// </summary>
        public float YawFromHeel;

        /// <summary>
        /// The sails' and hull windage's whole moment about the boat origin, in boat axes, Nm, at
        /// RigModel.SyntheticWindSpeed. x pitches her, y is the yaw, z heels her.
        /// </summary>
        public Vector3 SailMoment;

        /// <summary>
        /// How she sits in the water for this reading. Not Valid when heel is not modeled, which is an
        /// upright boat with her origin taken as the waterline.
        /// </summary>
        public SeaAttitude Attitude;

        /// <summary>
        /// The heel search ran out of rounds before forces and attitude agreed, so the reading is at
        /// the closest heel it found rather than at a balance. Shown as "?" in the sweep line.
        /// </summary>
        public bool HeelUnsettled;

        /// <summary>The wind speed the attitude was solved for, m/s, or zero for an upright reading.</summary>
        public float WindSpeed;

        /// <summary>
        /// Where the sails pull, in world space: each sail's windcenter weighted by the force it
        /// makes. This is the magenta marker, and it is a real place on the rig on every bearing. It
        /// does not predict the helm by itself, because the helm also depends on thrust acting off
        /// the centerline and on the hull's windage. HelmMeters carries that reading.
        /// </summary>
        public Vector3 CombinedCenterOfEffort;
        public Vector3 HullOrigin;

        /// <summary>
        /// Where the boat's rigidbody says the boat is, relative to where its transform says it is,
        /// in boat axes. Zero whenever physics is in step with the transform, which is nearly always.
        /// Nothing is measured from it; it is carried so the gizmo can report when it is not zero.
        /// See the pivot note on TryEvaluate.
        /// </summary>
        public Vector3 BodyOffsetLocal;
        public bool AnySailHasFrozenCe;
        public float HullLength;         // m, along the boat's own fore-aft axis
        public float HullTopHeight;      // m above the boat root; zero unless this call measured the hull
        public float WindAngle;          // degrees off the bow this was evaluated at

        /// <summary>How long the helm lever may get before it counts as off the scale, in meters.
        /// The helm bar is capped at the same length.</summary>
        public float OffScaleLever =>
            RigModel.OffScaleLeverFraction * Mathf.Max(HullLength, RigModel.DefaultHullLength);

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
        /// the single number the helm bar, the ring and the reef track's color all read; it used to
        /// be recomputed from YawMoment and TotalSideForce at three separate call sites, which is
        /// three chances for the sign convention to drift apart.
        ///
        /// It is a length and not a location. On a 61 m2 junk with the wind on the quarter the sail
        /// pulls about 3 m forward of the pivot while this works out to about 2.4 m aft, because
        /// thrust acting off the centerline and the hull's windage outweigh the sail's side force. It was
        /// once drawn as the magenta marker's position, which put the center of effort behind the
        /// only sail on the boat, so it now has its own bar on the centerline.
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
        /// zero here on purpose, so nothing downstream reads a clamped ratio as a measurement; the
        /// DIRECTION lives in RawLever's sign, and the ring and the helm bar paint it at full
        /// saturation.
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
    /// Two inputs the game reads are wrong in a shipyard, so each is replaced with what it would be
    /// at sea, because a pre-purchase reading has to depend on the rig and nothing else:
    ///  - the mutual sail-shading factor, which the game aims along the live outdoor wind and which
    ///    would make the same rig read differently as the weather changed. It is cleared for the
    ///    force calls and worked out again along the arrow's wind (see ApplySailShading);
    ///  - the wind-height multiplier, which is taken off raw world Y and so is inflated by however
    ///    high the cradle at this particular shipyard holds the boat (see CradleHeightCorrection).
    ///
    /// The moment is taken about the HULL ORIGIN, not the center of mass. In lateral-force
    /// equilibrium the hull's lateral drag exactly opposes the sails' side force and acts at the
    /// origin (Crest BoatProbes.cs:220-223 applies both the lateral and the forward drag at
    /// rb.position + _forceHeightOffset * up, and a purely vertical offset cannot change a yaw
    /// moment), so the two side-force terms collapse to a single lever about that point. That is
    /// what makes a pre-purchase number possible: it needs no leeway and no wind speed.
    ///
    /// The origin is read off the boat's transform, not off the rigidbody, although the game
    /// applies the drag at rb.position. On the boat root the two are the same point whenever
    /// physics is in step with the transform, so for the game there is no difference. For this
    /// model there is one when they are briefly out of step, because every sail position it
    /// measures FROM that point comes from a transform (windcenter.position), and mixing a
    /// transform position with a rigidbody position measures the gap between them as if it were
    /// rig geometry. That happened: a fourteen-sail rig drew a ring whose two halves disagreed by
    /// up to 12.5 m of helm, and every sail in the mirror audit sat about 1.1 m to port on BOTH
    /// tacks, the jib included, sheeted amidships on the centerline stay. A sideways offset of the
    /// origin hands every sail's forward drive a false lever with the same sign on both tacks,
    /// while the side force it is divided by changes sign, so the error flips between the halves
    /// and grows as drive outgrows side force: small head to wind, largest on a broad reach, which
    /// is what the ring showed. The next session, with the same rig, read symmetric. Measuring
    /// from the transform keeps the origin in the same frame as the positions, and the rigidbody's
    /// offset is carried in BodyOffsetLocal so the gizmo can say when it happens.
    ///
    /// Hull windage IS in the helm reading and NOT in the center of effort. See AddHullWindage.
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
        /// The trim search: a coarse scan of the whole swing, one refinement inside the winning
        /// bracket, then two more passes that each halve the spacing, and a last pose at the top of
        /// the parabola through the best pose and its two neighbors. A pose already tried is not tried
        /// again, which comes to about twelve calls into the game's force function per sail. Drive
        /// against trim is close to a true parabola, since it is the product of two terms linear in
        /// trim, so that last pose lands within a small fraction of a degree of the peak and moves
        /// smoothly with the wind.
        ///
        /// It stopped at the first refinement for a long time, which left an eighteen degree grid on
        /// that swing, and that was far too loose. Drive is nearly flat around its peak, but side
        /// force and the athwartships arm are not: on a 61 m2 junk at 125 degrees, drive stays between
        /// 319 and 320 N from 55 to 70 degrees of trim while side force halves from 238 to 121 N. So
        /// when the winner stepped to the next grid point, the lever jumped by up to 2.9 m for one
        /// degree of arrow movement, and a swing limit that moved the grid could change the reading on
        /// a bearing where that limit was not even binding. This takes that step to about 0.5 to 0.75
        /// m, measured offline against a brute force search of the same port of the model.
        /// </summary>
        private const int CoarseTrimSamples = 5;
        private const int RefineTrimSamples = 5;
        private const int NarrowTrimSamples = 3;
        private const int TrimPasses = 4;

        /// <summary>
        /// How far from the pivot the helm lever may land, as a fraction of the hull, before it is
        /// treated as off the scale. See RigBalance.OffScaleLever.
        /// </summary>
        public const float OffScaleLeverFraction = 0.25f;

        /// <summary>
        /// Below this unroll a sail counts as furled when deciding whether the rig has any canvas set.
        ///
        /// A furled sail keeps 1.5 percent of its force as bare-yard windage (Sail.cs:415), so a fully
        /// furled rig still makes a little force, and the hull's windage then decides the lever: about
        /// 3 m of confident weather helm on a fourteen-sail rig with nothing set. Measuring output
        /// against the rig's own best cannot catch that, because a furled rig's best is equally small.
        /// </summary>
        private const float FurledUnroll = 0.05f;

        /// <summary>
        /// How close two trims' drive has to be before they count as a tie and the centerd one wins,
        /// as a fraction of what the sail could make at full power. A hundred thousandth: about a
        /// hundred times float noise on a typical sail, and measured against the sail's OWN capacity
        /// rather than against whatever force this particular evaluation happened to produce, so the
        /// window does not vanish when the forces do.
        ///
        /// This window exists for real ties, the flat plateaus head to wind. It has to stay that
        /// narrow because drive is so flat around its peak. At a thousandth it spanned about five
        /// degrees either side of the peak, and always picking the centered end of that added about
        /// 20 percent to a sail's side force at no measured cost in drive, moving every reading
        /// toward lee helm: 1.54 m against a true 2.31 m on a one-sail junk at 125 degrees.
        /// </summary>
        private const float TieFraction = 0.00001f;

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
        /// average changes, and moving one weight in a weighted average walks along a straight line.
        ///
        /// Any sail whose unroll is chained to this one by a RopeControllerMirror is overridden
        /// with it, because the game will move them together whatever the model does.
        /// </param>
        /// <param name="knownHullLength">
        /// A hull length already measured for this boat this rebuild. Measuring walks every collider
        /// on the boat, and the ring's sweep calls this a dozen times for a number that cannot change
        /// between them, so the caller passes it in. Zero means measure.
        /// </param>
        /// <param name="windSpeed">
        /// The wind to heel her in, m/s. Zero, or heel switched off in the config, or a hull whose
        /// buoyancy cannot be read, gives the upright reading.
        /// </param>
        /// <summary>
        /// Judge every sail as fully set, whatever its real furl state. Set by the hull showcase,
        /// where every boat sits in port with her sails furled and would otherwise read no wind at
        /// all. Only the model's unroll changes; the sails themselves are not touched.
        /// </summary>
        public static bool AllSailsSet { get; set; }

        public static bool TryEvaluate(GameObject boatRoot, float windAngleDegrees, out RigBalance result,
            Sail overrideSail = null, float overrideUnroll = 1f, float knownHullLength = 0f,
            RigSnapshot snapshot = null, float windSpeed = 0f)
        {
            result = default(RigBalance);
            if (boatRoot == null || !Available) return false;

            bool heelWanted = windSpeed > 0f
                && (SailwindRigBalance.Config.Settings.ModelHeel == null
                    || SailwindRigBalance.Config.Settings.ModelHeel.Value);

            SeaAttitude calm = default(SeaAttitude);
            Hydrostatics.HullModel hull = heelWanted ? ShipyardHull(boatRoot, out calm) : null;
            if (hull == null)
                return TryEvaluateAt(boatRoot, windAngleDegrees, out result, overrideSail, overrideUnroll,
                    knownHullLength, snapshot, default(SeaAttitude));

            // The sails' force depends on how far she heels, through the angle the wind meets each
            // sail at and through how high each sail sits, and the heel depends on the force. So the
            // two are taken in turns until the heel stops moving: forces at this attitude, the
            // attitude those forces would hold, and again. In a moderate wind that settles in two or
            // three, because a degree of heel changes the force by a fraction of a percent.
            //
            // In a hard wind it does not settle at all, it swings. Upright, the sails lay her over
            // to 51 degrees; at 51 degrees they spill so much that the balance is 14; at 14 they lay
            // her over to 51 again. With a fixed number of rounds the reading was whichever end the
            // last round landed on, and since the game shades the two tacks a little differently
            // (FINDINGS 20) the two tacks landed on different ends: 51 against 14 degrees at 75 and
            // 285, helm levers 7 m apart at 125 and 235 (2026-11-14 log, 14-sail brig, 30 m/s).
            //
            // The balance is where the heel the forces would hold equals the heel they were taken
            // at. More heel always means less force, so that difference falls steadily with heel
            // and has one crossing. Once one round has overshot it and another fallen short, it is
            // found between them by false position, held back from either end so it cannot stall,
            // which needs no more than a handful of rounds and lands on the same answer from either
            // tack.
            //
            // The sails' force is linear in wind speed (Sail.cs:410 starts from apparentWind.magnitude
            // and nothing after it squares it), so the evaluation stays at SyntheticWindSpeed, where
            // every threshold in the model was tuned, and only the moment the balance has to hold is
            // scaled to the wind being judged. The helm lever is a ratio of moments to forces and so
            // does not depend on that scale at all; the heel it is read at does.
            float scale = windSpeed / SyntheticWindSpeed;
            SeaAttitude attitude = calm;
            if (!attitude.Valid)
                return TryEvaluateAt(boatRoot, windAngleDegrees, out result, overrideSail, overrideUnroll,
                    knownHullLength, snapshot, default(SeaAttitude));
            bool ok = false;

            // The tightest rounds on each side of the crossing: below it the forces want more heel
            // than they were taken at (gap > 0), above it less.
            bool haveBelow = false, haveAbove = false;
            SeaAttitude below = default(SeaAttitude), above = default(SeaAttitude);
            float belowGap = 0f, aboveGap = 0f;
            bool settled = false, lastRound = false, closed = false;
            SeaAttitude next = default(SeaAttitude);

            for (int pass = 0; pass < HeelPasses; pass++)
            {
                ok = TryEvaluateAt(boatRoot, windAngleDegrees, out result, overrideSail, overrideUnroll,
                    knownHullLength, snapshot, attitude);
                if (!ok) return false;

                next = Hydrostatics.Solve(hull, result.SailMoment * scale, attitude);
                float gap = next.Heel - attitude.Heel;
                settled = Mathf.Abs(gap) < HeelSettledDegrees
                    && Mathf.Abs(next.Pitch - attitude.Pitch) < HeelSettledDegrees
                    && Mathf.Abs(next.OriginHeight - attitude.OriginHeight) < 0.02f;
                // A last round is only an answer once it has been evaluated, which is this pass; one
                // set on the final allowed pass never is, and stays marked unsettled below.
                if (settled || lastRound) { closed = true; break; }

                if (gap > 0f)
                {
                    if (!haveBelow || attitude.Heel > below.Heel) { below = attitude; belowGap = gap; haveBelow = true; }
                }
                else if (!haveAbove || attitude.Heel < above.Heel)
                {
                    above = attitude; aboveGap = gap; haveAbove = true;
                }

                if (haveBelow && haveAbove)
                {
                    // False position, kept off the ends so a curved stretch cannot pin it to one.
                    float t = Mathf.Clamp(belowGap / (belowGap - aboveGap), 0.15f, 0.85f);
                    attitude = Between(below, above, t);
                    // Narrower than the settle tolerance: this round's reading is the answer.
                    if (above.Heel - below.Heel < HeelSettledDegrees * 2f) lastRound = true;
                }
                else
                {
                    // Toward the heel the forces want, but never more than MaxHeelStep at a time, so
                    // she is walked over from upright and stops at the first balance she meets, as a
                    // boat does. A full jump could land past it: upright forces that would lay her
                    // over put the next round at the heel limit, and a tack whose forces there still
                    // exceed the hull's greatest righting moment stayed there as laid over, while its
                    // mirror, a few percent different through shading, came back and found 18
                    // degrees (2026-11-14 log, 65 and 75 against 295 and 285 at 30 m/s).
                    float t = Mathf.Abs(gap) > MaxHeelStep ? MaxHeelStep / Mathf.Abs(gap) : 1f;
                    attitude = t < 1f ? Between(attitude, next, t) : next;
                }
            }

            // At a balance the solve's own verdict stands. Without one, at the heel limit with the
            // forces still wanting more, she is laid over, which the solve also says.
            result.Attitude.Overpowered = next.Overpowered;
            result.HeelUnsettled = !closed;
            result.WindSpeed = windSpeed;
            return ok;
        }

        /// <summary>
        /// The hull as the shipyard judges it, and how she floats with no wind, or null when heel is
        /// not modeled for this boat.
        ///
        /// The shipyard judges the rig, not the loading. Cargo or the player standing to one side
        /// moves the rigidbody's center of mass sideways (BoatMass.cs:66-117), which lists her a
        /// degree or two at no wind, so one tack was read at more heel than the other and the ring
        /// came out lopsided (2026-11-13 log: @2 against @0 at 1 m/s on a brig). The weight is kept,
        /// since how deep she sits is real; only its sideways position is centered, over the
        /// buoyancy points so she floats upright. HeelCheck reads the real loading at sea.
        ///
        /// Kept for the boat, her mass and her center of mass, which is all it is read from, so a
        /// sweep's 36 bearings and a drag's frames reuse it instead of reading and floating her again
        /// on every evaluation.
        /// </summary>
        internal static Hydrostatics.HullModel ShipyardHull(GameObject boatRoot, out SeaAttitude calm)
        {
            calm = default(SeaAttitude);
            var body = boatRoot == null ? null : boatRoot.GetComponent<Rigidbody>();
            if (body == null) return null;

            int id = boatRoot.GetInstanceID();
            Vector3 com = boatRoot.transform.InverseTransformDirection(body.worldCenterOfMass - boatRoot.transform.position);
            if (_hullCache != null && id == _hullCacheBoat && body.mass == _hullCacheMass
                && (com - _hullCacheCom).sqrMagnitude < 1e-8f)
            {
                calm = _hullCacheCalm;
                return _hullCache;
            }

            Hydrostatics.HullModel hull = Hydrostatics.Read(boatRoot, body);
            if (hull != null)
            {
                hull.CenterOfMass.x = Hydrostatics.BuoyancyCenterX(hull);
                calm = Hydrostatics.Float(hull);
            }
            _hullCache = hull;
            _hullCacheBoat = id;
            _hullCacheMass = body.mass;
            _hullCacheCom = com;
            _hullCacheCalm = calm;
            return hull;
        }

        private static Hydrostatics.HullModel _hullCache;
        private static int _hullCacheBoat;
        private static float _hullCacheMass;
        private static Vector3 _hullCacheCom;
        private static SeaAttitude _hullCacheCalm;

        private static SeaAttitude Between(SeaAttitude a, SeaAttitude b, float t)
        {
            return new SeaAttitude
            {
                Valid = true,
                Heel = Mathf.Lerp(a.Heel, b.Heel, t),
                Pitch = Mathf.Lerp(a.Pitch, b.Pitch, t),
                OriginHeight = Mathf.Lerp(a.OriginHeight, b.OriginHeight, t),
            };
        }

        /// <summary>
        /// Most rounds of forces against attitude before the heel is taken as found. Two in a moderate
        /// wind; in a hard one up to five steps of MaxHeelStep to walk over to the crossing and five or
        /// six more to close in on it.
        /// </summary>
        private const int HeelPasses = 12;

        /// <summary>The most one round may move the heel before a crossing is bracketed, degrees.</summary>
        private const float MaxHeelStep = 12f;

        /// <summary>A heel that moves less than this between rounds has settled.</summary>
        private const float HeelSettledDegrees = 0.25f;

        private static bool TryEvaluateAt(GameObject boatRoot, float windAngleDegrees, out RigBalance result,
            Sail overrideSail, float overrideUnroll, float knownHullLength, RigSnapshot snapshot,
            SeaAttitude attitude)
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

            // A heeled boat is evaluated without moving the boat: the wind is turned into the frame of
            // a boat at that attitude instead. Everything vanilla's force function reads is an angle
            // between the wind and the sail's or the hull's own axes, so turning the wind the other
            // way is the same thing. The one exception, the wind-height multiplier, reads a height,
            // and SeaFrame supplies that from the attitude.
            if (attitude.Valid) windLocal = Quaternion.Inverse(attitude.Rotation) * windLocal;
            Vector3 windWorld = boat.TransformVector(windLocal);
            var frame = new SeaFrame(boat, attitude);

            HashSet<Sail> overridden = overrideSail == null ? null : UnrollGroupOf(boatRoot, overrideSail);

            var sails = new List<SailContribution>();
            float totalSide = 0f, totalFwd = 0f;
            float yawFromSide = 0f, yawFromThrust = 0f;
            float weightSum = 0f;
            float sideMagSum = 0f;
            Vector3 ceAccum = Vector3.zero;
            bool anyFrozen = false;
            bool anySailSet = false;

            // Every sail on the boat at the unroll the model is judging it at, including the ones
            // dropped below for making no force, because a furled sail still stands in the way of the
            // shading rays (vanilla takes the first hit). Also what tells this boat's sails apart from
            // another boat's moored within reach of the rays.
            var unrollBySail = new Dictionary<Sail, float>();

            // BoatRefs.masts is a sparse array indexed by Mast.orderIndex, 48 long since Sailwind 0.39 and 31
            // before it, so it is walked as a sparse array and never against a fixed bound.
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
                        : AllSailsSet ? 1f
                        : (state.Valid ? state.Unroll : sail.currentUnroll);
                    if (unroll >= FurledUnroll) anySailSet = true;
                    unrollBySail[sail] = unroll;

                    if (!TryEvaluateSail(sail, boat, body, windWorld, windLocal.x, unroll, state, frame, out c))
                        continue;

                    // A sail producing no force cannot affect the balance by definition, and on a
                    // brig three of them turn up parked at the boat origin, presumably not yet
                    // placed on a mast. They were already weighted zero; dropping them here keeps
                    // the sail count and the log honest.
                    if (Mathf.Abs(c.ForwardForce) + Mathf.Abs(c.SideForce) < 0.001f) continue;

                    sails.Add(c);
                }
            }

            // After every sail has its trim, because how much one sail shades another depends on
            // where both of them are swung to. Skipped when the game's own shading could not be cleared,
            // because then it is already in every force and this would take it off twice.
            if (FShadowCol != null
                && (SailwindRigBalance.Config.Settings.ModelSailShading == null
                    || SailwindRigBalance.Config.Settings.ModelSailShading.Value))
                ApplySailShading(sails, unrollBySail, windWorld);

            Vector3 moment = Vector3.zero;
            Vector3 rootScale = boat.lossyScale;
            foreach (SailContribution c in sails)
            {
                totalSide += c.SideForce;
                totalFwd += c.ForwardForce;
                sideMagSum += Mathf.Abs(c.SideForce);
                yawFromSide += c.YawFromSide;
                yawFromThrust += c.YawFromThrust;
                if (!c.CeTracksResizes) anyFrozen = true;

                // Vanilla pushes along the boat's forward and right axes only (Sail.cs:461-470), so
                // the force has no up component in boat axes. Its y is the yaw above; x is the drive
                // acting at a height, which pitches her; z is the side force at a height, which heels her.
                // In meters: Arm is in the root's local units, and the buoyancy the heel is solved
                // against is measured in meters (Hydrostatics.Read). The same on every boat root in
                // the game, which are all unscaled (level24, 2026-11-14), and right on one that is not.
                moment += Vector3.Cross(Vector3.Scale(c.Arm, rootScale), new Vector3(c.SideForce, 0f, c.ForwardForce));

                float w = c.ForceMagnitude;
                ceAccum += c.CenterOfEffort * w;
                weightSum += w;
            }

            // The hull's own windage, added last because it is not a sail and does not belong in the
            // sail list or in the drawn center's weighting.
            AddHullWindage(boatRoot, boat, body, windWorld, ref totalSide, ref totalFwd,
                ref yawFromSide, ref yawFromThrust, ref moment);

            // Heeled, the rudder holds more than the yaw about her own mast: see Hydrostatics.RudderYaw.
            // Upright this is exactly zero, since the rotation is the identity.
            float yawFromHeel = attitude.Valid
                ? Hydrostatics.RudderYaw(moment, attitude) - moment.y
                : 0f;

            float totalYaw = yawFromSide + yawFromThrust + yawFromHeel;

            result.Sails = sails;
            result.TotalSideForce = totalSide;
            result.TotalForwardForce = totalFwd;
            result.YawMoment = totalYaw;
            result.YawFromSide = yawFromSide;
            result.YawFromThrust = yawFromThrust;
            result.YawFromHeel = yawFromHeel;
            result.SailMoment = moment;
            result.Attitude = attitude;
            result.HullOrigin = boat.position;
            result.BodyOffsetLocal = boat.InverseTransformVector(body.position - boat.position);
            result.AnySailHasFrozenCe = anyFrozen;
            Vector3 hullCenterLocal = Vector3.zero;
            float hullTopHeight = 0f;
            result.HullLength = knownHullLength > 0f
                ? knownHullLength
                : MeasureHullLength(boatRoot, boat, out hullCenterLocal, out hullTopHeight);
            result.HullCenterLocal = hullCenterLocal;
            result.HullTopHeight = hullTopHeight;
            result.WindAngle = windAngleDegrees;
            result.TotalOutput = Mathf.Abs(totalFwd) + Mathf.Abs(totalSide);

            // Side force has to be a real share of what the rig is producing before the ratio it
            // sits underneath means anything. A twentieth is generous: at a beam reach the two are
            // comparable, and by the time the side force is below this the boat is running and
            // there is nothing to balance.
            // Positive = weather helm. See HelmMeters.
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
                anySailSet
                && sails.Count > 0
                && Mathf.Abs(totalSide) > 0.15f * result.TotalOutput
                && Mathf.Abs(totalSide) > 0.01f
                && Mathf.Abs(lever) < result.OffScaleLever;

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
                && anySailSet
                && sails.Count > 0
                && Mathf.Abs(totalSide) > 0.15f * result.TotalOutput
                && Mathf.Abs(totalSide) > 0.01f
                && sideMagSum > 0.0001f
                && Mathf.Abs(totalSide) >= 0.7f * sideMagSum;

            // Gated on the SAME predicate the bar and every color read, never on a threshold of
            // its own: a reading that is meaningful but has no number, or has a number nothing else
            // trusts, is how a confident "balanced" gets reported for a yaw moment it never
            // reproduced.
            result.HelmMeters = result.HelmMeaningful ? lever : 0f;

            result.CombinedCenterOfEffort = weightSum > 0.0001f ? ceAccum / weightSum : boat.position;

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
        /// It is fixed per hull, and fitted parts do not change it: BoatWind reads no part of the
        /// boat's customization, so a cabin adds nothing here. Its size against the rig varies a lot.
        /// The lever it adds is about 0.09 m under a large rig, and about a meter on a one-sail 61 m2
        /// junk with the wind on the quarter, where it makes up about half of the net yaw.
        ///
        /// The sails' own list and the center of effort's weighting are untouched: this is a force
        /// on the hull, not a sail, so it moves the helm reading and not the magenta marker.
        /// </summary>
        private static void AddHullWindage(GameObject boatRoot, Transform boat, Rigidbody body,
            Vector3 windWorld, ref float totalSide, ref float totalFwd,
            ref float yawFromSide, ref float yawFromThrust, ref Vector3 moment)
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

                // A force, so turned into the boat's axes without the root's scale; the arm stays in
                // local units for the yaw, like the sails', and goes to meters for the heel moment.
                Vector3 forceLocal = boat.InverseTransformDirection(windWorld * magnitude);
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

                // In the moment the heel is solved for, with the up component dropped like the sails'
                // own: what the hull's drag cancels is the boat-axis forward and side parts.
                moment += Vector3.Cross(Vector3.Scale(armLocal, boat.lossyScale), new Vector3(forceLocal.x, 0f, forceLocal.z));
            }
            catch (Exception e)
            {
                WarnOnce("windage", "[RigModel] the hull's own windage could not be read, so the "
                    + "balance reading leaves out a small weather-helm term: " + e.Message);
            }
        }

        // Vanilla's shading constants, SailShadowCol.cs:51-65.
        private const float ShadeRayLength = 18f;
        private const float ShadeFullAt = ShadeRayLength * 0.15f;     // 2.7 m and closer shades fully
        private const int ShadeLayerMask = 1 << 24;                   // 16777216, the shadow colliders' layer

        private static readonly RaycastHit[] ShadeHits = new RaycastHit[32];
        private static readonly Dictionary<Mesh, Vector3[]> ShadeVertices = new Dictionary<Mesh, Vector3[]>();

        /// <summary>
        /// Takes away the force each sail loses to the sails upwind of it, the way the game does at sea.
        ///
        /// Vanilla's version is SailShadowCol.CheckShadow (SailShadowCol.cs:49-74). Every sail carries a
        /// shadow mesh of nine vertices on layer 24. From each vertex it casts one ray upwind, 18 m long,
        /// and if the first thing the ray meets is ANOTHER sail's shadow mesh, that vertex counts as
        /// shaded by the other sail's unroll times InverseLerp(18, 2.7, distance): nothing at 18 m,
        /// full at 2.7 m and closer. The average over the nine vertices is the share of force lost,
        /// and Sail.cs:413 multiplies the sail's whole force by one minus it.
        ///
        /// The game's own cached value cannot be used here. It is aimed along the live outdoor wind,
        /// not the wind arrow, so it is cleared for the force calls (see TryEvaluateSail) and this
        /// recomputes it the same way along the arrow's wind, with every sail swung to the trim the
        /// search chose for it. That makes it a second pass: how much one sail shades another depends
        /// on where both are swung, so it cannot be known while the sails are still being trimmed one
        /// at a time. The trims themselves are kept. Shading scales a sail's force by one factor at
        /// every trim it could take, so it cannot change which trim drives hardest, except through
        /// the sail's own swing moving its rays, which this leaves out.
        ///
        /// Three deliberate differences from vanilla, all in the direction of reading the rig and
        /// nothing else:
        ///  - Rays aim along the arrow's wind. At sea vanilla aims them along the TRUE wind
        ///    (Wind.currentWind), while the sails feel the apparent wind, so on a moving boat the
        ///    shading pattern belongs to a wind somewhat further aft than the arrow. At the shipyard
        ///    the boat is still and the two are the same.
        ///  - Hits on another boat's sails are passed over. Vanilla would let a moored neighbor shade
        ///    this rig, which says nothing about the rig.
        ///  - The occluder's unroll is the one the model is judging it at, so the reef track and the
        ///    furl button see the shading change with the sail they furl.
        /// </summary>
        private static void ApplySailShading(List<SailContribution> sails, Dictionary<Sail, float> unrollBySail,
            Vector3 windWorld)
        {
            if (sails.Count < 2 || windWorld.sqrMagnitude < 1e-6f) return;

            Vector3 upwind = -windWorld.normalized;
            var saved = new Quaternion[sails.Count];
            var shading = new float[sails.Count];
            int posed = 0;
            bool ok = false;

            try
            {
                for (; posed < sails.Count; posed++)
                {
                    SailContribution c = sails[posed];
                    saved[posed] = c.Sail.transform.localRotation;
                    c.Sail.transform.localRotation = Quaternion.Euler(c.RestEuler.x, c.RestEuler.y, c.Trim);
                }

                // Gameplay runs with autoSyncTransforms on (StartMenu.cs:433), so this is normally
                // redundant. The pause menu turns it off (StartMenu.cs:420), and a rebuild that lands
                // then must not cast its rays at where the sails were a moment ago.
                Physics.SyncTransforms();

                for (int i = 0; i < sails.Count; i++)
                    shading[i] = ShadeOf(sails[i].Sail, upwind, unrollBySail);

                ok = true;
            }
            catch (Exception e)
            {
                WarnOnce("shading", "[RigModel] sail shading could not be worked out, so every sail is "
                    + "judged in clear air: " + e.Message);
            }
            finally
            {
                for (int i = 0; i < posed; i++)
                {
                    Sail sail = sails[i].Sail;
                    if (sail != null) sail.transform.localRotation = saved[i];
                }
                Physics.SyncTransforms();
            }

            if (!ok) return;

            for (int i = 0; i < sails.Count; i++)
            {
                if (shading[i] <= 0f) continue;

                SailContribution c = sails[i];
                float keep = 1f - shading[i];
                c.ForwardForce *= keep;
                c.SideForce *= keep;
                c.YawMoment *= keep;
                c.YawFromSide *= keep;
                c.YawFromThrust *= keep;
                c.Shading = shading[i];
                sails[i] = c;
            }
        }

        /// <summary>One sail's share of force lost to the sails upwind of it, SailShadowCol.CheckShadow's way.</summary>
        private static float ShadeOf(Sail sail, Vector3 upwind, Dictionary<Sail, float> unrollBySail)
        {
            // The collider the sail registered for itself (Sail.RegisterShadowCol, called from
            // SailShadowCol.Awake). A sail without one is never shaded in vanilla either: Sail.cs:266-269
            // returns a multiplier of 1.
            SailShadowCol own = FShadowCol?.GetValue(sail) as SailShadowCol;
            if (own == null) own = sail.GetComponentInChildren<SailShadowCol>(true);
            if (own == null) return 0f;

            MeshFilter filter = own.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return 0f;

            Vector3[] vertices;
            if (!ShadeVertices.TryGetValue(mesh, out vertices))
            {
                vertices = mesh.vertices;
                ShadeVertices[mesh] = vertices;

                // Once per mesh, the shape the rays start from, in the shadow collider's own axes. The
                // 2026-11-13 log had the same sail shaded 43% on one tack and 11% on the other; if the
                // mesh is bellied to one side it does not flip with the tack, and vanilla shades it the
                // same lopsided way at sea. This says whether that is the cause.
                if (vertices != null && vertices.Length > 0)
                {
                    Bounds b = new Bounds(vertices[0], Vector3.zero);
                    foreach (Vector3 p in vertices) b.Encapsulate(p);
                    Vector3 s = own.transform.lossyScale;
                    Plugin.Log.LogInfo($"[Shade] '{sail.sailName}' shadow mesh '{mesh.name}': {vertices.Length} vert(s), "
                        + $"local center ({b.center.x:F2}, {b.center.y:F2}, {b.center.z:F2}), "
                        + $"size ({b.size.x:F2}, {b.size.y:F2}, {b.size.z:F2}), scale ({s.x:F2}, {s.y:F2}, {s.z:F2}), "
                        + $"collider {(own.GetComponent<Collider>() is Collider col ? (col.isTrigger ? "trigger" : "solid") : "none")}");
                }
            }
            if (vertices == null || vertices.Length == 0) return 0f;

            float total = 0f;
            Transform from = own.transform;
            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 origin = from.TransformPoint(vertices[v]);

                // UseGlobal, as vanilla's Physics.Raycast does by default, so whichever colliders the
                // game's own rays can see, these see too: every shadow mesh but a staysail's is a
                // trigger (SailShadowCol.cs:21-28).
                int count = Physics.RaycastNonAlloc(origin, upwind, ShadeHits, ShadeRayLength,
                    ShadeLayerMask, QueryTriggerInteraction.UseGlobal);

                // Vanilla takes only the first thing the ray meets. RaycastNonAlloc returns hits in
                // no order, so the nearest is found by hand, passing over anything that is not one of
                // this boat's sails.
                Sail nearest = null;
                float nearestDistance = float.MaxValue;
                for (int h = 0; h < count; h++)
                {
                    Collider hitCollider = ShadeHits[h].collider;
                    if (hitCollider == null) continue;
                    SailShadowCol other = hitCollider.GetComponent<SailShadowCol>();
                    Sail otherSail = other != null ? other.GetSail() : null;
                    if (otherSail == null || !unrollBySail.ContainsKey(otherSail)) continue;

                    if (ShadeHits[h].distance < nearestDistance)
                    {
                        nearestDistance = ShadeHits[h].distance;
                        nearest = otherSail;
                    }
                }

                // A ray whose first hit is the sail's own mesh shades nothing, as in vanilla.
                if (nearest == null || nearest == sail) continue;

                total += unrollBySail[nearest] * Mathf.InverseLerp(ShadeRayLength, ShadeFullAt, nearestDistance);
            }

            return Mathf.Clamp01(total / vertices.Length);
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
            Vector3 windWorld, float windLateral, float unroll, SailState state, SeaFrame frame,
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
                // (Sail.cs:266-269), so clearing it here gives the force in clear air, and
                // ApplySailShading takes the shading off afterwards along the arrow's wind.
                if (FShadowCol != null) FShadowCol.SetValue(sail, null);
                else WarnOnce("shadowCol", "[RigModel] Sail.shadowCol did not resolve, so the "
                    + "balance reading includes whatever sail shading today's weather produces in place "
                    + "of shading along the wind arrow.");

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

                // A fixed slice of what THIS sail could make at full power, computed once and held
                // constant across every trim, so the tie window means the same thing whether the
                // sail is driving hard or barely working. Deliberately independent of the forces
                // actually measured: scaling it to the best force found so far collapses the window
                // to nothing exactly where the forces are near zero, which is the head to wind case
                // the tie-break exists for.
                float tieTolerance = sail.GetRealSailPower() * CategoryMultiplier(sail.category)
                    * 50f * SyntheticWindSpeed * TieFraction
                    * Mathf.Max(Debugger.instance.debugYardMult, unroll);   // as Sail.cs:415 scales the force

                float low = Mathf.Min(minTrim, maxTrim);
                float high = Mathf.Max(minTrim, maxTrim);

                TrimCandidates.Clear();
                float bestTrimSoFar = 0f;
                float spacing = 0f;     // between the poses the last pass tried

                // A coarse scan of the whole swing, then narrowing around the leader. See
                // CoarseTrimSamples for what the resolution has to be and why.
                //
                // Drive against trim is a smooth hump whose peak walks steadily aft as the wind goes
                // aft, so a coarse grid is near the optimum on some bearings and well off it on the
                // next. Being wrong by a DIFFERENT amount at each bearing is what makes it serious
                // here: it does not just shift the level of the readout, it bends the shape of the
                // ring, which is the one thing the ring exists to show.
                for (int pass = 0; pass < TrimPasses; pass++)
                {
                    float from = low, to = high;
                    int samples = CoarseTrimSamples;

                    if (pass > 0)
                    {
                        if (spacing < 0.01f) break;      // no swing to refine

                        // The first refinement fills the whole bracket. After that the poses a full
                        // spacing either side have been tried already, so half that reach is what
                        // adds new ones, and three samples halve the spacing for two new evaluations.
                        float reach = pass == 1 ? spacing : spacing * 0.5f;
                        from = Mathf.Max(low, bestTrimSoFar - reach);
                        to = Mathf.Min(high, bestTrimSoFar + reach);
                        samples = pass == 1 ? RefineTrimSamples : NarrowTrimSamples;
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
                        if (pass > 0) break;
                    }

                    for (int k = 0; k < samples; k++)
                    {
                        // -1 to +1 about the center, so u(k) is the exact negation of u(n-1-k).
                        float u = samples <= 1 ? 0f : 2f * k / (samples - 1) - 1f;
                        float trim = center + half * u;
                        if (!AlreadyTried(trim))
                            TrimCandidates.Add(EvaluatePose(sail, euler, trim, frame));
                    }

                    spacing = samples <= 1 ? 0f : half * 2f / (samples - 1);

                    // Over everything tried so far, with the same whole-set rule as the final pick,
                    // so a mirrored wind narrows toward the mirrored pose. See PickTrim.
                    int leader = PickTrim(0, TrimCandidates.Count, tieTolerance, windLateral);
                    if (leader < 0) break;
                    bestTrimSoFar = TrimCandidates[leader].Trim;
                }

                // Only ever one more candidate for the same whole-set pick, so wherever the drive is
                // not a smooth hump, at vanilla's aback step or on a plateau, it simply does not win.
                float peak;
                if (TryFindDrivePeak(windLateral, out peak) && !AlreadyTried(peak))
                    TrimCandidates.Add(EvaluatePose(sail, euler, peak, frame));

                int winner = PickTrim(0, TrimCandidates.Count, tieTolerance, windLateral);
                if (winner < 0) return false;

                float bestFwd = TrimCandidates[winner].Fwd;
                float bestSide = TrimCandidates[winner].Side;
                Vector3 ce = TrimCandidates[winner].Ce;
                // About the hull origin, taken off the transform like the center of effort itself,
                // so both ends of the arm are in one frame. See the pivot note on the class.
                Vector3 armLocal = boat.InverseTransformVector(ce - boat.position);

                // Boat axes: x starboard, z forward. Unity's cross product about up reduces to
                // arm.z * force.x - arm.x * force.z, which is exactly these two terms. Taken in the
                // boat's own frame rather than through a world-space cross product so the two halves
                // are separable, and so they provably sum to the same number.
                float yawFromSide = armLocal.z * bestSide;
                float yawFromThrust = -armLocal.x * bestFwd;

                c.Sail = sail;
                c.Name = sail.sailName;
                c.CenterOfEffort = ce;
                c.HeightAboveWaterline = frame.HeightOf(ce);
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
        /// One call into the game's force function with the sail at this trim, read back in newtons.
        /// The caller restores everything this disturbs.
        /// </summary>
        private static TrimCandidate EvaluatePose(Sail sail, Vector3 euler, float trim, SeaFrame frame)
        {
            sail.transform.localRotation = Quaternion.Euler(euler.x, euler.y, trim);
            MUpdateForce.Invoke(sail, null);

            Vector3 ceHere = sail.windcenter.position;
            float unampF = ToFloat(FUnampFwd.GetValue(sail));
            float unampS = ToFloat(FUnampSide.GetValue(sail));
            float scale = sail.GetRealSailPower() * CategoryMultiplier(sail.category) * 50f
                * CradleHeightCorrection(ceHere.y, frame.HeightOf(ceHere));

            float sideShare = ToFloat(FOutSideShare?.GetValue(sail));
            object abackHere = FPushingBack?.GetValue(sail);
            bool aback = abackHere is bool && (bool)abackHere;

            // The pose is dead abeam, so vanilla just decided the sign of a zero and it decides the
            // other way on the other tack. Take the answer that does not depend on that sign: a force
            // with no component along the bow is not driving her. See AthwartshipsShare. Only the +1
            // branch is corrected, because the -0.33 branch is already the value this settles on, so
            // applying it twice would undo it.
            bool clamped = false;
            if (sideShare >= AthwartshipsShare && !aback)
            {
                unampF *= AbackDriveMultiplier;
                clamped = true;
            }

            return new TrimCandidate
            {
                Trim = trim,
                Fwd = unampF * scale,
                Side = unampS * scale * 1.5f,   // the sideways component carries an extra 1.5x
                Ce = ceHere,
                SideShare = sideShare,
                Aback = aback,
                DriveClamped = clamped,
            };
        }

        /// <summary>
        /// True when a pose this close to the given trim has been evaluated already this search.
        /// </summary>
        private static bool AlreadyTried(float trim)
        {
            for (int i = 0; i < TrimCandidates.Count; i++)
                if (Mathf.Abs(TrimCandidates[i].Trim - trim) < TrimTieEpsilon) return true;

            return false;
        }

        /// <summary>
        /// The top of the parabola through the pose with the most drive and the nearest tried pose
        /// either side of it. False when that pose is at the end of what was tried, which is what a
        /// swing limit looks like, or when all three are level, which is what a plateau looks like.
        ///
        /// Taken from the highest drive and not from PickTrim, whose answer is the most centered pose
        /// in the tie window. With the peak nearly midway between two poses, that can be the lower of
        /// the two, and the three points would not make a hump around it. An exact tie still resolves
        /// the way PickTrim resolves one, centered first and then to leeward, so mirrored winds fit
        /// mirrored peaks.
        /// </summary>
        private static bool TryFindDrivePeak(float windLateral, out float peak)
        {
            peak = 0f;

            int top = -1;
            for (int i = 0; i < TrimCandidates.Count; i++)
            {
                if (top < 0) { top = i; continue; }

                TrimCandidate c = TrimCandidates[i], t = TrimCandidates[top];
                if (c.Fwd != t.Fwd)
                {
                    if (c.Fwd > t.Fwd) top = i;
                    continue;
                }

                float da = Mathf.Abs(c.Trim), db = Mathf.Abs(t.Trim);
                if (da < db - TrimTieEpsilon) top = i;
                else if (da <= db + TrimTieEpsilon && c.Side * windLateral > 0f && t.Side * windLateral <= 0f)
                    top = i;
            }
            if (top < 0) return false;

            float x1 = TrimCandidates[top].Trim, y1 = TrimCandidates[top].Fwd;
            int below = -1, above = -1;
            for (int i = 0; i < TrimCandidates.Count; i++)
            {
                float t = TrimCandidates[i].Trim;
                if (t < x1 - TrimTieEpsilon && (below < 0 || t > TrimCandidates[below].Trim)) below = i;
                if (t > x1 + TrimTieEpsilon && (above < 0 || t < TrimCandidates[above].Trim)) above = i;
            }
            if (below < 0 || above < 0) return false;

            float x0 = TrimCandidates[below].Trim, y0 = TrimCandidates[below].Fwd;
            float x2 = TrimCandidates[above].Trim, y2 = TrimCandidates[above].Fwd;

            // Measured from the top pose, which keeps the arithmetic well conditioned and makes the
            // formula odd under mirroring, so mirrored winds get mirrored peaks. Neither neighbor is
            // higher, so this is always a hump unless both are level, and the test below turns that away.
            float a = x0 - x1, b = x2 - x1;
            float p = y0 - y1, q = y2 - y1;

            float denominator = 2f * (p * b - q * a);
            if (Mathf.Abs(denominator) < 1e-9f) return false;

            float offset = (p * b * b - q * a * a) / denominator;
            if (!(offset > a && offset < b)) return false;      // written to turn away NaN as well

            peak = x1 + offset;
            return true;
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
        private static float CradleHeightCorrection(float centerWorldY, float heightAboveSea)
        {
            float asEvaluated = WindHeightMult(centerWorldY);
            if (asEvaluated < 0.0001f) return 1f;
            return WindHeightMult(heightAboveSea) / asEvaluated;
        }

        /// <summary>
        /// How high a point on the boat would be above the sea, for the wind-height multiplier.
        ///
        /// Upright, the boat origin is taken as the waterline, as the mod always did. With heel
        /// modeled, the height comes from the solved attitude: how deep she floats, and how far a sail
        /// on the low side drops toward the water as she lies over.
        /// </summary>
        private readonly struct SeaFrame
        {
            private readonly Transform _boat;
            private readonly SeaAttitude _attitude;
            private readonly float _waterline;

            public SeaFrame(Transform boat, SeaAttitude attitude)
            {
                _boat = boat;
                _attitude = attitude;
                _waterline = boat.position.y;
            }

            public float HeightOf(Vector3 world) => _attitude.Valid
                ? _attitude.HeightOf(_boat.InverseTransformDirection(world - _boat.position))
                : world.y - _waterline;
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
            out Vector3 hullCenterLocal, out float hullTopHeight)
        {
            hullCenterLocal = Vector3.zero;
            hullTopHeight = 0f;

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
                    hullTopHeight = span.TopY;
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

        /// <summary>
        /// The hull's own solid colliders, by the same filter that measures it: no rig geometry, no
        /// mast walk capsules, no fitted parts. What the waterline is cut from.
        ///
        /// With switchedOffOnly, the same filter over colliders on objects switched off in the scene
        /// instead, which is where the large junk keeps her hull: under 'junk large (3)/structure'
        /// every collider sits on an inactive object (level24, dumped 2026-11-14), and she floats on
        /// her probes, not on a collider. The rig test then walks the parents itself, because
        /// GetComponentInParent skips inactive objects on this Unity (2019.1).
        /// </summary>
        internal static List<Collider> HullColliders(GameObject boatRoot, bool switchedOffOnly = false)
        {
            var hull = new List<Collider>();
            if (boatRoot == null) return hull;

            try
            {
                HashSet<Transform> mastWalkCols = MastWalkColliders(boatRoot);
                HashSet<Transform> fittedParts = FittedPartRoots(boatRoot);
                foreach (Collider collider in boatRoot.GetComponentsInChildren<Collider>(switchedOffOnly))
                {
                    if (collider == null || collider.isTrigger) continue;
                    if (switchedOffOnly)
                    {
                        if (collider.gameObject.activeInHierarchy) continue;
                        if (UnderRigParent(collider.transform, boatRoot.transform)) continue;
                    }
                    if (IsRigGeometry(collider, mastWalkCols)) continue;
                    if (IsUnder(collider.transform, fittedParts)) continue;
                    hull.Add(collider);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[RigModel] could not list the hull's colliders: " + e.Message);
            }

            return hull;
        }

        /// <summary>A Sail or Mast on the object or a parent up to the boat, found by GetComponent,
        /// which, unlike GetComponentInParent here, also sees objects that are switched off.</summary>
        private static bool UnderRigParent(Transform t, Transform boat)
        {
            for (; t != null && t != boat; t = t.parent)
                if (t.GetComponent<Sail>() != null || t.GetComponent<Mast>() != null) return true;
            return false;
        }

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
                if (TooBigForHull(collider, box, frame, boat)) continue;

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

        /// <summary>No boat part is this long or this far from her origin. A collider past it is not hull, whatever it is.</summary>
        private const float MaxPartMeters = 150f;

        /// <summary>
        /// A collider whose own box is longer than any boat. The large junk carries at least one: its
        /// hull came out over 500 m, fell back to a nominal 20 m, and the hull showcase framed an
        /// empty stretch of sea between the boat and the world origin. Logged once per collider by
        /// name and size, so what it is can be read off a log.
        /// </summary>
        internal static bool TooBigForHull(Collider collider, Bounds box, Transform frame, Transform boat)
        {
            Vector3 size = box.size;
            if (frame != null) size = Vector3.Scale(size, Abs(frame.lossyScale));
            Vector3 center = frame != null ? frame.TransformPoint(box.center) : box.center;
            if (BoatSized(size, center, boat)) return false;
            float away = (center - boat.position).magnitude;

            var mesh = collider as MeshCollider;
            WarnOnce("toobig:" + collider.GetInstanceID(), $"[RigModel] set aside '{NameOf(collider, boat)}"
                + $"{(mesh != null && mesh.sharedMesh != null ? ", mesh '" + mesh.sharedMesh.name + "'" : "")}, "
                + $"{(collider.enabled ? "enabled" : "disabled")}: its box is {size.x:F0} x {size.y:F0} x {size.z:F0} m "
                + $"and its middle {away:F0} m from the boat's origin, too big or too far away to be part of her.");
            return true;
        }

        /// <summary>
        /// Whether a box, in meters with its middle in world space, could be part of this boat: no
        /// side longer than MaxPartMeters and its middle no further than that from her origin. The one
        /// test the hull measurement, the waterline and the hull showcase all use.
        /// </summary>
        internal static bool BoatSized(Vector3 sizeMeters, Vector3 centerWorld, Transform boat)
        {
            return sizeMeters.x <= MaxPartMeters && sizeMeters.y <= MaxPartMeters && sizeMeters.z <= MaxPartMeters
                && (centerWorld - boat.position).magnitude <= MaxPartMeters;
        }

        internal static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

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

        internal static float CategoryMultiplier(SailCategory category)
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
        /// Scratch for the trim search, reused across sails and bearings: the ring runs this about
        /// twelve times a sail, thirty six times a rebuild, and a fresh list each time is garbage the
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
