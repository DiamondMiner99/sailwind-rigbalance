using System;
using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// A ring around the boat, colored by what the helm would do at every point of sail.
    ///
    /// The single close-hauled reading answers "is she balanced right now". It cannot answer the
    /// question that actually bites: does she keep her manners all the way round, or does the helm
    /// reverse somewhere between close-hauled and a reach. That happens because upwindEfficiency is
    /// a per-sail field (Sail.cs:33) and Sail.cs:415 drags each sail's force toward its own value
    /// as the wind closes on the bow. On a brig the squares hold 0.250 while the gaffs and
    /// staysails hold 0.480 and 0.500, and those sit at different ends of the boat, so coming up on
    /// the wind quietly reweights the rig fore and aft.
    ///
    /// Read it exactly like the markers: a ring that is green or blue all the way round is a boat
    /// that behaves; a ring with red in it is a boat that develops lee helm on some heading, and
    /// the colored arc tells you which.
    ///
    /// Built as one mesh with interpolated vertex colors rather than a line renderer per segment.
    /// LineRenderer's gradient tops out at eight keys, far short of the resolution this needs, and
    /// two dozen renderers to draw one ring would be silly. The unlit shader the markers already
    /// use multiplies by vertex color, so the same shader works unchanged.
    ///
    /// It gets its OWN material, never the helm link's. The link's material carries the current
    /// verdict color in _Color, and Sprites/Default multiplies _Color into the vertex color, so
    /// sharing it multiplied every one of the ring's verdicts by the verdict at the one angle the
    /// arrow happened to point at. A lee-helm reading painted the entire ring red, which is exactly
    /// the failure the gray guard below was added to prevent, and the two were indistinguishable.
    /// </summary>
    public class BalanceRing
    {
        /// <summary>
        /// Bearings the rig is actually evaluated at, every ten degrees. This is the expensive
        /// number: each one drives the game's force function ten times per sail, and the whole sweep
        /// is spread across frames because in one go it measured 189 ms.
        /// </summary>
        private const int Segments = 36;

        /// <summary>
        /// Quads the ring is drawn from, every five degrees, with color interpolated between the
        /// evaluated bearings. Geometry is nearly free, so the drawn resolution is decoupled from
        /// the sampled one: the ring reads as a continuous wash rather than a color wheel, without
        /// paying for an evaluation per quad.
        /// </summary>
        private const int MeshSegments = 72;

        /// <summary>
        /// How far past the balanced band the helm has to go before the color saturates, in
        /// multiples of the band. Inside the band it is flat green, because that is what balanced
        /// means; beyond it the color ramps so the ring shows HOW one-handed she is, not merely
        /// that she is. Three bands is about the point where a rig stops being a trim problem.
        /// </summary>
        private const float SaturateAtBands = 3f;

        /// <summary>
        /// How much of the rig's best output an angle must produce before the ring will offer a
        /// verdict on it. Below this the sails are not drawing and there is nothing to balance.
        ///
        /// This used to be a fixed 16 degrees off the bow, justified by Sail.cs:416 ramping the
        /// force to nothing below 16. That threshold is real but it is not measured in degrees off
        /// the BOW: relativeWindAngle (Sail.cs:409, :495-516) is the angle between the apparent wind
        /// and the sail's own axis, taken off windcenter and scaled by a verticality factor, so it
        /// depends on how the sail is trimmed, which is the very thing the model sweeps over. A
        /// square rigger with its yards braced round draws hard at 10 degrees off the bow and the
        /// ring painted it gray; a fore-and-aft rig sheeted flat can be under the ramp at 30 and the
        /// ring painted a confident verdict on forces of order a newton.
        ///
        /// Comparing each angle's output against the best output anywhere on the sweep asks the
        /// question directly, in the rig's own terms, and needs no assumption about geometry.
        /// </summary>
        internal const float DeadOutputFraction = 0.10f;

        private static readonly Color DeadColor = new Color(0.45f, 0.45f, 0.45f, 0.55f);

        private GameObject _go;
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private Vector3 _centerLocal;

        /// <summary>
        /// The ring's height in the boat's frame, held apart from the centered point so that a
        /// horizontal offset cannot become a vertical one on a cradle that is not level.
        /// </summary>
        private float _heightLocal;

        /// <summary>True while the measured hull center is being clamped, so it is logged once.</summary>
        private bool _centerCapped;
        private float _radius = 10f;


        // Wind streaks. See AnimateStreaks.
        private const int StreakCount = 18;
        private const float StreakSeconds = 6f;
        private GameObject _streakGo;
        private readonly LineRenderer[] _streaks = new LineRenderer[StreakCount];
        private readonly float[] _streakLateral = new float[StreakCount];
        private readonly float[] _streakPhase = new float[StreakCount];
        private readonly float[] _streakHeight = new float[StreakCount];
        private readonly System.Random _streakRandom = new System.Random(20260911);
        private float _streakUntil;
        private float _streakFade;
        private float _streakAngle = float.NaN;

        private GameObject _arrowGo;
        private Mesh _arrowMesh;
        private bool _dragging;
        private bool _angleChanged;

        /// <summary>
        /// The best total output found anywhere on the last sweep. The markers and the helm link
        /// evaluate one angle only and so cannot work this out for themselves; taking it from here
        /// is what keeps the ring and the panel from contradicting each other about whether there is
        /// a verdict to give. Zero until the first rebuild, which reads as "no opinion yet".
        /// </summary>
        public float MaxOutput { get; private set; }

        /// <summary>
        /// Where the wind is coming from, in degrees off the bow, positive to port. This is the
        /// angle every marker is evaluated at; the ring behind it shows what the other angles would
        /// give.
        /// </summary>
        public float WindAngle { get; private set; } = 45f;

        public void SetWindAngle(float degrees)
        {
            WindAngle = Mathf.Repeat(degrees, 360f);
        }

        /// <summary>True once after the arrow has been dragged, so the caller can recompute.</summary>
        public bool ConsumeAngleChanged()
        {
            if (!_angleChanged) return false;
            _angleChanged = false;
            return true;
        }

        public bool Dragging => _dragging;

        public bool Exists => _go != null;

        public void SetVisible(bool visible)
        {
            if (_go != null && _go.activeSelf != visible) _go.SetActive(visible);
            if (_arrowGo != null && _arrowGo.activeSelf != visible) _arrowGo.SetActive(visible);

            // A drag in progress holds the shipyard camera still every frame. Hiding the ring
            // mid-drag would leave that suppression running with nothing on screen to explain it.
            if (!visible) _dragging = false;
        }

        public void Destroy()
        {
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_arrowMesh != null) UnityEngine.Object.Destroy(_arrowMesh);
            _streakGo = null;
            for (int i = 0; i < StreakCount; i++) _streaks[i] = null;
            _streakFade = 0f;
            _streakAngle = float.NaN;
            if (_arrowGo != null) UnityEngine.Object.Destroy(_arrowGo);
            _mesh = null;
            _go = null;
            _filter = null;
            _renderer = null;
            _arrowMesh = null;
            _arrowGo = null;
            _dragging = false;
            _centerCapped = false;
            _loggedAudit = null;
            MaxOutput = 0f;
        }

        /// <summary>
        /// Keeps the ring on the boat without parenting to it, as the markers do.
        ///
        /// The height is taken from the boat's own reference plane and not from the centered point,
        /// which is the second field report: the ring sank a little with every sail edit until it
        /// was under the ground at Fort Aestrin and under a cog, while at an Emerald Archipelago
        /// yard a different player watched it climb into the sky. Opposite directions, same cause. A
        /// shipyard sets the boat down at its cradle's own rotation (Shipyard.MoveShip lerps to
        /// editedShipPosition.rotation), and those cradles are not all level, so once the ring
        /// centered on the measured hull rather than on the root, every meter the center walked
        /// fore or aft was also a lever: a meter of walk on a cradle holding her five degrees down
        /// by the head is nine centimeters of height, in whichever direction that yard happens to
        /// tilt. Centering horizontally is what the first field report asked for and it stays;
        /// levering the picture out of the water with it was never part of the intent.
        /// </summary>
        public void Follow(Transform boat)
        {
            if (_go == null || boat == null) return;

            Vector3 center = boat.TransformPoint(_centerLocal);
            center.y = boat.TransformPoint(new Vector3(0f, _heightLocal, 0f)).y;

            _go.transform.SetPositionAndRotation(center, boat.rotation);
            if (_arrowGo != null)
                _arrowGo.transform.SetPositionAndRotation(_go.transform.position, _go.transform.rotation);

            if (_go.activeSelf) AnimateStreaks();
        }

        /// <summary>
        /// Lets the player swing the wind round by dragging the arrow.
        ///
        /// Deliberately NOT done with a collider and Physics.Raycast. The shipyard raycasts for its
        /// own sail picking, and adding another collider into that is asking to steal clicks from
        /// it. Instead the arrow's world position is projected to the screen and the drag only
        /// starts if the cursor is within a small radius of it, which needs no collider at all and
        /// cannot appear in anybody else's raycast. The arrow sits out beyond the hull, over open
        /// water, so the odds of that small target overlapping something clickable are low.
        /// </summary>
        public void HandleDrag(Camera camera, Transform boat)
        {
            if (_go == null || camera == null || boat == null) return;

            // Hidden means gone, not merely invisible. SetVisible deactivates the object without
            // nulling it, so without this a hidden ring left an invisible grab target in the water
            // that swallowed a click, jumped the wind angle to wherever the cursor was, and held the
            // shipyard camera still with nothing on screen to explain any of it.
            if (!_go.activeSelf) return;

            Vector3 center = _go.transform.position;
            Vector3 handle = center + DirectionFor(WindAngle, boat) * _radius;

            if (!_dragging)
            {
                if (!Input.GetMouseButtonDown(0)) { SetArrowHighlight(false); return; }

                Vector3 screen = camera.WorldToScreenPoint(handle);
                if (screen.z <= 0f) return;   // behind the camera

                if (Vector2.Distance(new Vector2(screen.x, screen.y),
                        new Vector2(Input.mousePosition.x, Input.mousePosition.y)) > GrabRadiusPixels)
                    return;

                _dragging = true;
                SetArrowHighlight(true);
                SuppressCameraLook();
                return;
            }

            if (!Input.GetMouseButton(0))
            {
                _dragging = false;
                SetArrowHighlight(false);

                // One more rebuild on release, so the settled state is evaluated and logged with
                // _dragging already false. Without it the last frame of the drag recomputed while
                // still dragging, which the gizmo suppresses from the log, and then nothing was ever
                // scheduled again: the last line in the log described the angle the arrow was at
                // BEFORE the drag, with nothing to say it had moved.
                _angleChanged = true;
                return;
            }

            // Held off every frame of the drag. ShipyardCameraRotator.Update turns MouseLook on at
            // every left mouse-down (ShipyardCameraRotator.cs:18-22) and off again on mouse-up, so
            // without this the camera swings around while the arrow is being dragged. This runs in
            // LateUpdate, after that Update, so it wins. Nothing needs restoring afterwards: the
            // rotator disables them itself on release, which is their resting state anyway.
            SuppressCameraLook();

            // Where the cursor lands on the ring's own plane.
            var plane = new Plane(boat.up, center);
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            float enter;
            if (!plane.Raycast(ray, out enter)) return;

            Vector3 local = boat.InverseTransformDirection(ray.GetPoint(enter) - center);
            if (local.sqrMagnitude < 0.0001f) return;

            // Inverse of DirectionFor: that maps an angle to (-sin, 0, cos).
            float degrees = Mathf.Repeat(Mathf.Atan2(-local.x, local.z) * Mathf.Rad2Deg, 360f);
            if (Mathf.Abs(Mathf.DeltaAngle(degrees, WindAngle)) < 0.5f) return;

            WindAngle = degrees;
            _angleChanged = true;
            UpdateArrow(boat);
        }

        private const float GrabRadiusPixels = 40f;

        /// <summary>
        /// Stops the shipyard camera looking around while the arrow is being dragged. The rotator
        /// enables MouseLook on the camera and on its first child; both are switched off here.
        /// </summary>
        private static void SuppressCameraLook()
        {
            try
            {
                if (BoatCamera.instance == null) return;

                var own = BoatCamera.instance.GetComponent<MouseLook>();
                if (own != null && own.enabled) own.enabled = false;

                Transform child = BoatCamera.instance.transform.childCount > 0
                    ? BoatCamera.instance.transform.GetChild(0)
                    : null;
                if (child == null) return;

                var childLook = child.GetComponent<MouseLook>();
                if (childLook != null && childLook.enabled) childLook.enabled = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] could not hold the camera still while dragging: " + e.Message);
            }
        }

        private static Vector3 DirectionFor(float degrees, Transform boat)
        {
            float rad = degrees * Mathf.Deg2Rad;
            Vector3 local = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            return boat.TransformDirection(local);
        }

        private void SetArrowHighlight(bool on)
        {
            if (_arrowGo == null) return;
            var r = _arrowGo.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = true;
            // Rebuilt colors are cheap; the mesh is seven vertices.
            BuildArrowMesh(on);
        }

        /// <summary>
        /// Sweeps the apparent wind right round the boat and rebuilds the ring from the verdicts.
        /// Runs on the debounced rebuild, never per frame.
        ///
        /// Two passes over the half circle, not one: the dead-angle test compares each angle's
        /// output against the best found anywhere on the sweep, so every angle has to be evaluated
        /// before any of them can be colored.
        /// </summary>
        public void Rebuild(GameObject boatRoot, Transform boat, RigBalance current,
            float balancedMeters, Material material, int layer, Transform parent)
        {
            try
            {
                if (_go == null) Create(material, layer, parent);
                if (_go == null) return;

                // Centered on the measured hull, not on the boat root. They are usually the same
                // point, but the first field report proved they can walk apart: repeated oversized
                // sail resizes at a cramped cradle stepped the visible hull away from its root a
                // little per edit, about 400 edits deep the ring sat visibly off the ship, and the
                // shipyard camera, anchored to the same root, agreed with the ring rather than the
                // ship. The height still comes from the root, which is where the cradle holds the
                // boat; only the fore-aft and athwartships center follow the hull. Follow applies
                // the height separately, because on a tilted cradle the offset is a lever.
                //
                // Bounded, because an offset this large is no longer a measurement of anything. The
                // walk that produced the first report never exceeded a couple of meters on a 15 m
                // hull; past a quarter of the hull the measured box is being dragged by something
                // that is not the hull, and following it puts the ring somewhere no reading belongs.
                // Clamping keeps the picture on the ship and leaves a line in the log saying so,
                // which is a better failure than a ring in the next bay.
                Vector3 origin = boat.InverseTransformPoint(current.HullOrigin);

                var offset = new Vector2(current.HullCenterLocal.x, current.HullCenterLocal.z);
                float bound = Mathf.Max(current.HullLength * 0.25f, 1f);
                if (offset.magnitude > bound)
                {
                    if (!_centerCapped)
                    {
                        _centerCapped = true;
                        Plugin.Log.LogInfo($"[Ring] the measured hull center is {offset.magnitude:F1} m "
                            + $"from the boat root, past the {bound:F1} m this hull allows; the ring is "
                            + "drawn at the limit rather than where the measurement points.");
                    }
                    offset = offset.normalized * bound;
                }
                else if (_centerCapped)
                {
                    _centerCapped = false;
                    Plugin.Log.LogInfo("[Ring] the measured hull center is back within bounds.");
                }

                _centerLocal = new Vector3(offset.x, origin.y, offset.y);
                _heightLocal = origin.y;

                _radius = Mathf.Max(current.HullLength * 0.55f, 3f);
                float band = Mathf.Max(current.HullLength * 0.030f, 0.25f);

                // Start the sweep; Step does the work, a few bearings per frame.
                //
                // Measured at 189 ms for 36 bearings on a ten-sail square rigger, which as one
                // synchronous burst is a five frame freeze every time a sail is touched. The cost is
                // not reflection overhead that could be optimised away: it is the game's own force
                // function plus a transform write and a world-position read per trim pose, and there
                // are thirty six times ten times ten of those. Making the answer cruder to fit it
                // into one frame would be trading the thing the tool is for against a stutter, so it
                // is spread instead. The ring keeps showing the previous sweep until the new one is
                // complete, about half a second later, which for a background readout nobody is
                // staring at is invisible.
                _pendingRoot = boatRoot;
                _pendingHullLength = current.HullLength;

                // Freeze the rig for the whole sweep. Without this the ring is not a picture of one
                // boat: the bearings on one bow are read from a rig up to thirty frames older than
                // those on the other, and on a live boat something really does toggle between two
                // states on alternate frames. Two sweeps of an untouched rig came back with
                // identical port halves and different starboard ones, one mirroring correctly and
                // one not, because the second sweep straddled the flip.
                _pendingSnapshot = RigModel.CaptureRig(boatRoot);
                _pendingBalancedMeters = balancedMeters;
                _pendingBand = band;
                _pendingSampled = new RigBalance[Segments];
                _pendingEvaluated = new bool[Segments];
                _pendingBest = 0f;
                _pendingIndex = 0;
                _pendingMilliseconds = 0f;

                Step(boat);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] rebuild failed: " + e.Message);
                _pendingIndex = -1;
            }
        }

        // Sweep in progress. Negative index means idle.
        private GameObject _pendingRoot;
        private RigBalance[] _pendingSampled;
        private bool[] _pendingEvaluated;
        private float _pendingHullLength;
        private RigModel.RigSnapshot _pendingSnapshot;
        private float _pendingBalancedMeters;
        private float _pendingBand;
        private float _pendingBest;
        private float _pendingMilliseconds;
        private int _pendingIndex = -1;

        /// <summary>
        /// Milliseconds of sweep to do per frame. Small enough to disappear into a frame at 30 fps,
        /// large enough that a full sweep completes in well under a second.
        /// </summary>
        private const float FrameBudgetMs = 4f;

        public bool Sweeping => _pendingIndex >= 0;

        /// <summary>
        /// Advances an in-progress sweep by up to one frame's budget, and finishes it when the last
        /// bearing is in. Safe to call every frame; does nothing when idle.
        /// </summary>
        public void Step(Transform boat)
        {
            if (_pendingIndex < 0) return;

            if (_pendingRoot == null || boat == null)
            {
                // The boat went away mid-sweep, which a customization does. Drop it rather than
                // finish a ring built from destroyed sails; the next dirty flag starts a fresh one.
                _pendingIndex = -1;
                return;
            }

            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                while (_pendingIndex < Segments)
                {
                    int i = _pendingIndex++;
                    float angle = 360f * (i + 0.5f) / Segments;

                    RigBalance balance;
                    // The hull cannot change length between bearings, so it is measured once by the
                    // caller and passed in.
                    _pendingEvaluated[i] = RigModel.TryEvaluate(_pendingRoot, angle, out balance,
                        null, 1f, _pendingHullLength, _pendingSnapshot);
                    _pendingSampled[i] = balance;

                    if (_pendingEvaluated[i] && balance.TotalOutput > _pendingBest)
                        _pendingBest = balance.TotalOutput;

                    if (clock.Elapsed.TotalMilliseconds >= FrameBudgetMs) break;
                }

                _pendingMilliseconds += (float)clock.Elapsed.TotalMilliseconds;
                if (_pendingIndex < Segments) return;

                Finish(boat);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] sweep failed: " + e.Message);
                _pendingIndex = -1;
            }
        }

        private void Finish(Transform boat)
        {
            try
            {
                MaxOutput = _pendingBest;

                var colors = new Color[Segments];
                for (int i = 0; i < Segments; i++)
                {
                    colors[i] = _pendingEvaluated[i]
                        ? VerdictFor(_pendingSampled[i], _pendingBest, _pendingBalancedMeters)
                        : DeadColor;
                }

                BuildMesh(_radius, _pendingBand, colors);
                UpdateArrow(boat);

                LogSweep(_pendingSampled, _pendingEvaluated, _pendingBest,
                    _pendingBalancedMeters, _pendingMilliseconds);

                LogMirrorAudit(_pendingSampled, _pendingEvaluated);

            }
            finally
            {
                _pendingIndex = -1;
                _pendingSampled = null;
                _pendingEvaluated = null;
                _pendingRoot = null;
                _pendingSnapshot = null;
                _sweepCompleted = true;
            }
        }

        private bool _sweepCompleted;

        /// <summary>
        /// True once after a sweep finishes, so the caller can re-color the markers.
        ///
        /// MaxOutput is the yardstick the "no verdict" test measures against, and it is only known
        /// once every bearing has been evaluated. With the sweep spread over frames, the markers and
        /// the helm link are first drawn against the PREVIOUS sweep's yardstick, or against none at
        /// all on the first visit of a session. This is the nudge that makes them catch up. It
        /// cannot loop: the caller only starts a sweep when the RIG changed, and this is not that.
        /// </summary>
        public bool ConsumeSweepCompleted()
        {
            if (!_sweepCompleted) return false;
            _sweepCompleted = false;
            return true;
        }

        /// <summary>
        /// The whole sweep on one line, at Info, once per rig change.
        ///
        /// A ring is a picture, and a picture cannot be argued with. When it looks wrong the only
        /// question worth asking is what the numbers behind each arc actually were, and screenshots
        /// cannot answer it. One line per rebuild is cheap, it never fires during a drag, and it
        /// turns "the ring has gone red" into a reading anyone can check.
        ///
        /// Per segment: the bearing, the verdict letter, the lever in meters, and the side force as
        /// a percentage of total output, which is the quantity that decides whether there is a
        /// verdict at all. B balanced, W weather, L lee, lowercase w or l off the scale (the lever
        /// printed is the raw one), dash no verdict, x failed to evaluate.
        /// </summary>
        private static void LogSweep(RigBalance[] sampled, bool[] evaluated, float maxOutput,
            float balancedMeters, float milliseconds)
        {
            try
            {
                var line = new System.Text.StringBuilder(
                    $"[Ring] sweep of {Segments} bearings in {milliseconds:F1} ms, "
                    + $"band {balancedMeters:F2} m, best output {maxOutput:F0} N:");

                for (int i = 0; i < Segments; i++)
                {
                    float angle = 360f * (i + 0.5f) / Segments;
                    if (!evaluated[i])
                    {
                        line.Append($" {angle:F0}:x");
                        continue;
                    }

                    RigBalance b = sampled[i];
                    char verdict;
                    float meters = b.HelmMeters;
                    if (IsDead(b, maxOutput)) verdict = '-';
                    else if (b.HelmOffScale)
                    {
                        verdict = b.RawLever > 0f ? 'w' : 'l';
                        meters = b.RawLever;
                    }
                    else if (Mathf.Abs(b.HelmMeters) < balancedMeters) verdict = 'B';
                    else verdict = b.HelmMeters > 0f ? 'W' : 'L';

                    float share = b.TotalOutput > 0.0001f
                        ? 100f * Mathf.Abs(b.TotalSideForce) / b.TotalOutput
                        : 0f;

                    line.Append($" {angle:F0}:{verdict}{meters:F1}/{share:F0}%");
                }

                Plugin.Log.LogInfo(line.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] could not log the sweep: " + e.Message);
            }
        }

        /// <summary>
        /// How far two mirror-paired readings may differ before the audit calls it asymmetric, as a
        /// fraction of the larger. One percent: far above the last bits of a sum of a dozen floats,
        /// far below any difference that could bend the ring where a player would see it.
        /// </summary>
        private const float MirrorTolerance = 0.01f;

        /// <summary>The bearing a sample stands for: the center of its arc, not its lower edge.</summary>
        private static float Bearing(int index)
        {
            return 360f * (index + 0.5f) / Segments;
        }

        /// <summary>
        /// Relative gap between two readings, or zero when both are too small in newtons to compare.
        /// Absolute rather than relative below one newton, because near head to wind the quantities
        /// being compared are sums that cancel to almost nothing and a relative test there reports
        /// enormous disagreement between two numbers that are both, physically, no force at all.
        /// </summary>
        private static float RelativeGap(float a, float b)
        {
            float scale = Mathf.Max(Mathf.Abs(a), Mathf.Abs(b));
            if (scale < 1f) return 0f;
            return Mathf.Abs(a - b) / scale;
        }

        /// <summary>
        /// Checks the one property a sweep cannot be argued out of, and names what breaks it.
        ///
        /// A hull is symmetric and so is its rig, so a wind from bearing b and a wind from 360-b are
        /// one situation and its reflection port for starboard. Every quantity the model produces has
        /// to either match exactly or negate exactly: forward force matches, side force negates, the
        /// chosen trim negates, a center of effort keeps its height and its station along the boat and
        /// negates its offset from the centerline. The helm lever, a ratio of two quantities that both
        /// negate, matches.
        ///
        /// Sample i covers bearing 10i+5, so its reflection at 355-10i is sample Segments-1-i. No
        /// extra evaluation is needed: the finished sweep already holds every sail's contribution at
        /// every bearing, so this is arithmetic over numbers that were going to be thrown away.
        ///
        /// This exists because three separate fixes for a lopsided ring were each argued from the
        /// source, each looked right, and each changed nothing measurable. A screenshot cannot say
        /// which sail disagrees, and neither can a helm lever that has already averaged ten of them
        /// together. This can, and it reports the per-sail detail only when something is actually
        /// wrong, so a healthy rig costs one line.
        ///
        /// Logged when the answer changes, not on every sweep. The brig topsail's residual at 175/185
        /// (FINDINGS section 14) is known, documented, and invisible on screen because both bearings
        /// draw gray, but it was printed as four warnings per sweep: 52 of 52 warnings in one
        /// ordinary shipyard session. Players are asked to send their logs, and a log whose every
        /// warning is the same known residual is one where a real warning gets scrolled past. What
        /// identifies an answer is which pairs disagree, not by how much, so a rig edit that moves
        /// the same residual from 44.6 to 25.2 percent stays quiet and a new pair going wrong does not.
        /// </summary>
        private static string _loggedAudit;

        private static void LogMirrorAudit(RigBalance[] sampled, bool[] evaluated)
        {
            try
            {
                if (sampled == null || evaluated == null) return;

                int pairs = 0, broken = 0, worst = -1;
                float worstGap = 0f;
                var signature = new System.Text.StringBuilder();

                for (int i = 0; i < Segments / 2; i++)
                {
                    int j = Segments - 1 - i;
                    if (!evaluated[i] || !evaluated[j]) continue;
                    pairs++;

                    // Total output and side force, not the lever: the lever is a ratio and can agree
                    // by luck while both of its terms are wrong, and it is suppressed outright wherever
                    // the reading is judged meaningless, which is exactly where the trouble is.
                    float gap = Mathf.Max(
                        RelativeGap(sampled[i].TotalOutput, sampled[j].TotalOutput),
                        RelativeGap(Mathf.Abs(sampled[i].TotalSideForce),
                                    Mathf.Abs(sampled[j].TotalSideForce)));

                    if (gap <= MirrorTolerance) continue;

                    broken++;
                    signature.Append(i).Append(',');
                    if (gap > worstGap) { worstGap = gap; worst = i; }
                }

                string answer = $"{pairs}:{signature}";
                if (answer == _loggedAudit) return;
                _loggedAudit = answer;

                if (broken == 0 || worst < 0)
                {
                    Plugin.Log.LogInfo($"[Ring] mirror audit: {pairs} pairs, symmetric.");
                    return;
                }

                Plugin.Log.LogWarning($"[Ring] mirror audit: {broken} of {pairs} pairs asymmetric, "
                    + $"worst {Bearing(worst):F0}/{Bearing(Segments - 1 - worst):F0} off by "
                    + $"{worstGap * 100f:F1} percent.");

                DumpMirrorPair(sampled[worst], sampled[Segments - 1 - worst],
                    Bearing(worst), Bearing(Segments - 1 - worst));

                Plugin.Log.LogInfo("[Ring] mirror audit: reported again only if a different set of "
                    + "pairs disagrees.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] mirror audit failed: " + e.Message);
            }
        }

        /// <summary>
        /// The full disagreement for one mirror pair: the totals, then every sail that does not
        /// reflect, with what it should have been. Sails are matched by identity rather than by name,
        /// because a rig carries several sails with the same name and a mismatch between two of THOSE
        /// is the interesting case, not something to be hidden by pairing them wrongly.
        /// </summary>
        private static void DumpMirrorPair(RigBalance a, RigBalance b,
            float bearingA, float bearingB)
        {
            Plugin.Log.LogWarning($"[Ring] mirror {bearingA:F0} vs {bearingB:F0}: "
                + $"output {a.TotalOutput:F0} vs {b.TotalOutput:F0} N, "
                + $"side {a.TotalSideForce:F0} vs {b.TotalSideForce:F0} N (want negated), "
                + $"yawSide {a.YawFromSide:F0} vs {b.YawFromSide:F0}, "
                + $"yawThrust {a.YawFromThrust:F0} vs {b.YawFromThrust:F0}, "
                + $"lever {a.HelmMeters:F2} vs {b.HelmMeters:F2} m");

            if (a.Sails == null || b.Sails == null) return;

            for (int k = 0; k < a.Sails.Count; k++)
            {
                SailContribution ca = a.Sails[k];

                int m = -1;
                for (int n = 0; n < b.Sails.Count; n++)
                {
                    if (ReferenceEquals(b.Sails[n].Sail, ca.Sail)) { m = n; break; }
                }

                if (m < 0)
                {
                    // Dropped by the no-force test in TryEvaluate on one tack and not the other, which
                    // would change the sail SET between two readings that are supposed to be one.
                    Plugin.Log.LogWarning($"[Ring]   '{ca.Name}': counted at {bearingA:F0}, "
                        + $"dropped at {bearingB:F0}");
                    continue;
                }

                SailContribution cb = b.Sails[m];

                // The arm each sail was evaluated at, in boat axes, taken at its OWN instant. Not the
                // world center of effort transformed here: the two readings are up to thirty six
                // frames apart and the shipyard cradle is still lerping the hull into place across
                // them, so transforming both by one late transform measures the cradle's travel and
                // reported a fictitious 3.6 m disagreement in every sail. See SailContribution.Arm.
                Vector3 pa = ca.Arm;
                Vector3 pb = cb.Arm;

                bool trimOk = Mathf.Abs(ca.Trim + cb.Trim) < 0.05f;
                bool fwdOk = RelativeGap(ca.ForwardForce, cb.ForwardForce) <= MirrorTolerance;
                bool sideOk = RelativeGap(ca.SideForce, -cb.SideForce) <= MirrorTolerance;
                bool ceOk = Mathf.Abs(pa.x + pb.x) < 0.02f && Mathf.Abs(pa.z - pb.z) < 0.02f;

                if (trimOk && fwdOk && sideOk && ceOk) continue;

                Plugin.Log.LogWarning($"[Ring]   '{ca.Name}': "
                    + $"trim {ca.Trim:F1} vs {cb.Trim:F1}{(trimOk ? "" : $" WANT {-ca.Trim:F1}")}, "
                    + $"fwd {ca.ForwardForce:F0} vs {cb.ForwardForce:F0}{(fwdOk ? "" : " WANT EQUAL")}, "
                    + $"side {ca.SideForce:F0} vs {cb.SideForce:F0}{(sideOk ? "" : $" WANT {-ca.SideForce:F0}")}, "
                    + $"ce.x {pa.x:F2} vs {pb.x:F2}{(ceOk ? "" : $" WANT {-pa.x:F2}")}, "
                    + $"ce.z {pa.z:F2} vs {pb.z:F2}, "
                    // Vanilla's own reading at the chosen pose. A share near 1 with the backwards
                    // flag clear means the search settled hard against the drive step at
                    // Sail.cs:427, where the largest available drive lives when nothing drives, and
                    // where the side force is close to maximal. That is a reading produced by the
                    // step rather than by the rig, and it is the one case a force comparison alone
                    // cannot identify.
                    + $"sideShare {ca.SideShare:F2} vs {cb.SideShare:F2}, "
                    + $"aback {ca.Aback} vs {cb.Aback}, "
                    + $"clamped {ca.DriveClamped} vs {cb.DriveClamped}");
            }

            for (int n = 0; n < b.Sails.Count; n++)
            {
                bool seen = false;
                for (int k = 0; k < a.Sails.Count; k++)
                {
                    if (ReferenceEquals(a.Sails[k].Sail, b.Sails[n].Sail)) { seen = true; break; }
                }

                if (!seen)
                {
                    Plugin.Log.LogWarning($"[Ring]   '{b.Sails[n].Name}': dropped at {bearingA:F0}, "
                        + $"counted at {bearingB:F0}");
                }
            }
        }

        /// <summary>
        /// The verdict color for one evaluated angle. Shared with the helm link through
        /// <see cref="IsDead"/> so the ring behind the markers cannot disagree with them.
        /// </summary>
        internal static Color VerdictFor(RigBalance balance, float maxOutput, float balancedMeters)
        {
            if (IsDead(balance, maxOutput)) return DeadColor;

            // Off the scale is a verdict, not an absence of one. The lever bound in the model
            // refuses to put a NUMBER on a center of effort more than a quarter hull from the
            // pivot, and that stands; but when the side force is real and coherent, the direction
            // is solid and the magnitude is "at least a dozen bands", which on this ramp is simply
            // the saturated end. Painting it the same gray as a furled rig is what taught a player
            // that their monstrously lopsided lateen "had no power".
            if (balance.HelmOffScale)
                return balance.RawLever > 0f
                    ? ShipyardGizmo.WeatherHelmColor
                    : ShipyardGizmo.LeeHelmColor;

            return HelmColor(balance.HelmMeters, balancedMeters);
        }

        /// <summary>
        /// The helm as a continuous color rather than one of three.
        ///
        /// Green at balanced, ramping out to blue for weather helm and red for lee helm as the
        /// lever leaves the band. A diverging scale with the safe state in the middle, because the
        /// quantity is signed and zero is the good value: the two directions are genuinely different
        /// failures, not two ends of one axis.
        ///
        /// Three flat colors threw away the magnitude, which is most of what a player wants. A rig
        /// half a meter outside the band and a rig five meters outside it both painted the same red,
        /// so there was no way to see whether an edit had helped unless it crossed a threshold. The
        /// ramp makes an improvement visible while it is still an improvement.
        ///
        /// Used by the ring, the helm link and the reef track alike, so the three cannot drift into
        /// disagreeing about what a color means.
        /// </summary>
        internal static Color HelmColor(float helmMeters, float balancedMeters)
        {
            if (balancedMeters <= 0.0001f) return ShipyardGizmo.BalancedColor;

            float bands = Mathf.Abs(helmMeters) / balancedMeters;

            // Square-rooted, so the hue commits early and then deepens. A linear ramp spends most of
            // its range near the neutral color, which hides the first and most useful part of the
            // signal: the point where a rig starts to go one-handed, not the point where it is
            // hopeless. This is most of the way to blue or red by a quarter of the range.
            float t = Mathf.Sqrt(Mathf.InverseLerp(1f, SaturateAtBands, bands));

            Color extreme = helmMeters > 0f
                ? ShipyardGizmo.WeatherHelmColor
                : ShipyardGizmo.LeeHelmColor;

            return Color.Lerp(ShipyardGizmo.BalancedColor, extreme, t);
        }

        /// <summary>
        /// Whether this reading carries no verdict at all: either the sails are barely drawing, or
        /// there is so little side force that the ratio underneath the verdict is noise.
        /// </summary>
        internal static bool IsDead(RigBalance balance, float maxOutput)
        {
            if (maxOutput > 0f && balance.TotalOutput < DeadOutputFraction * maxOutput) return true;
            return !balance.HelmMeaningful && !balance.HelmOffScale;
        }

        internal static Color NoVerdictColor => DeadColor;

        /// <summary>
        /// Rebuilds the arrow so it sits at the current wind bearing, pointing inward at the boat,
        /// because that is the direction the wind blows. It is drawn in the ring's own local space
        /// and rotated with the boat, so it needs no transform of its own beyond following.
        /// </summary>
        private void UpdateArrow(Transform boat)
        {
            if (_arrowGo == null || _arrowMesh == null) return;
            BuildArrowMesh(_dragging);
        }

        private void BuildArrowMesh(bool highlighted)
        {
            if (_arrowMesh == null) return;

            float rad = WindAngle * Mathf.Deg2Rad;
            Vector3 d = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 side = Vector3.Cross(Vector3.up, d).normalized;

            float head = Mathf.Max(_radius * 0.16f, 0.8f);
            float halfWidth = head * 0.55f;
            float shaft = head * 1.5f;
            float shaftHalf = halfWidth * 0.30f;

            Vector3 at = d * _radius;                 // on the ring
            Vector3 tip = at - d * head;              // pointing in toward the boat
            Vector3 tail = at + d * shaft;

            var vertices = new Vector3[]
            {
                tip,                                  // 0 head
                at + side * halfWidth,                // 1
                at - side * halfWidth,                // 2
                at + side * shaftHalf,                // 3 shaft
                at - side * shaftHalf,                // 4
                tail + side * shaftHalf,              // 5
                tail - side * shaftHalf,              // 6
            };

            var triangles = new[]
            {
                0, 1, 2,
                3, 5, 6,
                3, 6, 4,
            };

            Color color = highlighted
                ? new Color(1f, 1f, 1f, 1f)
                : new Color(0.98f, 0.98f, 0.92f, 0.90f);

            var colors = new Color[vertices.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = color;

            _arrowMesh.Clear();
            _arrowMesh.vertices = vertices;
            _arrowMesh.colors = colors;
            _arrowMesh.triangles = triangles;
            _arrowMesh.RecalculateBounds();
        }

        private void Create(Material material, int layer, Transform parent)
        {
            _go = new GameObject("BalanceRing");
            _go.layer = layer;
            if (parent != null) _go.transform.SetParent(parent, false);

            _filter = _go.AddComponent<MeshFilter>();
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _mesh = new Mesh { name = "BalanceRingMesh" };
            _mesh.MarkDynamic();
            _filter.sharedMesh = _mesh;

            _arrowGo = new GameObject("WindArrow");
            _arrowGo.layer = layer;
            _arrowGo.transform.SetParent(_go.transform.parent, false);

            var arrowFilter = _arrowGo.AddComponent<MeshFilter>();
            var arrowRenderer = _arrowGo.AddComponent<MeshRenderer>();
            arrowRenderer.sharedMaterial = material;

            // The arrow and the band are two coplanar transparent meshes sharing one material, so
            // Unity orders them by distance from the camera to each mesh's bounds center. The band's
            // center is the boat; the arrow's is out at its bearing. Orbit the camera and whichever
            // center happens to be nearer wins, which had the arrow ducking underneath the band on
            // one side of the boat and riding on top on the other. Sorting order outranks distance
            // for transparent renderers, so this pins the arrow above the band from every angle.
            arrowRenderer.sortingOrder = 1;

            arrowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            arrowRenderer.receiveShadows = false;
            arrowRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            arrowRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _arrowMesh = new Mesh { name = "WindArrowMesh" };
            _arrowMesh.MarkDynamic();
            arrowFilter.sharedMesh = _arrowMesh;

            _streakGo = new GameObject("WindStreaks");
            _streakGo.layer = layer;
            _streakGo.transform.SetParent(_go.transform, false);
            for (int i = 0; i < StreakCount; i++)
            {
                var go = new GameObject("Streak");
                go.layer = layer;
                go.transform.SetParent(_streakGo.transform, false);

                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.positionCount = 2;
                line.numCapVertices = 2;
                line.sharedMaterial = material;
                line.sortingOrder = 2;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                _streaks[i] = line;

                // Spread along the path from the start, so they do not all enter together.
                RespawnStreak(i, (float)_streakRandom.NextDouble());
            }
            _streakGo.SetActive(false);

            // Shown on arriving in the yard, then left to fade. See AnimateStreaks.
            _streakUntil = Time.unscaledTime + StreakSeconds;
        }

        private void RespawnStreak(int i, float phase)
        {
            _streakLateral[i] = (float)(_streakRandom.NextDouble() * 1.8 - 0.9);
            _streakHeight[i] = (float)_streakRandom.NextDouble();
            _streakPhase[i] = phase;
        }

        /// <summary>
        /// Faint lines drifting across the ring in the arrow's wind, the synthetic one the ring is
        /// evaluated in, never the weather outside.
        ///
        /// They settle which way the arrow means. A static arrow is ambiguous by nature: a player
        /// asked for it to point into the wind like a weather vane, which is a real convention, and
        /// the arrow's current one, pointing the way the air moves as a weather map does, is a real
        /// convention too. Flipping it would only move the confusion to the other half of the
        /// players. Moving lines have no second reading.
        ///
        /// Shown on arrival and whenever the arrow moves, then faded out after a few seconds, so
        /// they explain the arrow and then get out of the way of the markers. Streaks stay inside
        /// the ring's circle, travel from the upwind side to the downwind side at a steady pace,
        /// and fade in and out along the way so nothing pops.
        /// </summary>
        private void AnimateStreaks()
        {
            if (_streakGo == null) return;

            bool wanted = Config.Settings.ShowWindStreaks == null || Config.Settings.ShowWindStreaks.Value;

            if (!Mathf.Approximately(_streakAngle, WindAngle) || _dragging)
            {
                _streakAngle = WindAngle;
                _streakUntil = Time.unscaledTime + StreakSeconds;
            }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            float target = wanted && Time.unscaledTime < _streakUntil ? 1f : 0f;
            _streakFade = Mathf.MoveTowards(_streakFade, target, dt / 0.8f);

            if (_streakFade <= 0f)
            {
                if (_streakGo.activeSelf) _streakGo.SetActive(false);
                return;
            }
            if (!_streakGo.activeSelf) _streakGo.SetActive(true);

            float rad = WindAngle * Mathf.Deg2Rad;
            Vector3 upwind = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 across = Vector3.Cross(Vector3.up, upwind).normalized;

            float speed = _radius * 0.35f;
            float length = _radius * 0.14f;
            float width = Mathf.Max(_radius * 0.008f, 0.05f);
            float top = _radius * 0.5f;

            for (int i = 0; i < StreakCount; i++)
            {
                LineRenderer line = _streaks[i];
                if (line == null) continue;

                float lateral = _streakLateral[i];
                float halfChord = _radius * Mathf.Sqrt(Mathf.Max(0.0001f, 1f - lateral * lateral));

                _streakPhase[i] += dt * speed / (2f * halfChord);
                if (_streakPhase[i] >= 1f)
                {
                    RespawnStreak(i, 0f);
                    lateral = _streakLateral[i];
                    halfChord = _radius * Mathf.Sqrt(Mathf.Max(0.0001f, 1f - lateral * lateral));
                }

                float phase = _streakPhase[i];
                Vector3 head = across * (lateral * _radius)
                    + upwind * (halfChord - 2f * halfChord * phase)
                    + Vector3.up * (0.5f + _streakHeight[i] * top);
                Vector3 tail = head + upwind * length;

                line.SetPosition(0, tail);
                line.SetPosition(1, head);
                line.startWidth = width;
                line.endWidth = width;

                float alpha = 0.45f * _streakFade * Mathf.Sin(Mathf.PI * phase);
                line.startColor = new Color(0.92f, 0.96f, 1f, 0f);
                line.endColor = new Color(0.92f, 0.96f, 1f, alpha);
            }
        }

        /// <summary>
        /// One quad per drawn segment, each edge carrying the color interpolated for its own
        /// bearing, so the hardware washes the color along the ring instead of stepping it.
        ///
        /// The quads used to be flat, deliberately, so that neighboring verdicts could not blend.
        /// That was the right call while a verdict was one of three colors and blending two of them
        /// would have invented a fourth. It stopped being right when the color became a continuous
        /// function of the helm: with a ramp, the value between two sampled bearings really is
        /// between the two, and drawing it as a hard step is the lie. Twenty four flat bands also
        /// hid the magnitude entirely, so a rig just outside the band and a rig hopelessly outside
        /// it looked identical.
        ///
        /// At 72 quads that is 288 vertices, which is still nothing, and the sampling cost is
        /// unchanged because geometry and evaluation are now separate numbers.
        /// </summary>
        private void BuildMesh(float radius, float band, Color[] colors)
        {
            FillRing(_mesh, radius, band, colors);
        }

        private static void FillRing(Mesh mesh, float radius, float band, Color[] colors)
        {
            float inner = radius - band * 0.5f;
            float outer = radius + band * 0.5f;

            var vertices = new Vector3[MeshSegments * 4];
            var vertexColors = new Color[MeshSegments * 4];
            var triangles = new int[MeshSegments * 6];

            for (int i = 0; i < MeshSegments; i++)
            {
                // Bearing the wind comes FROM. RigModel puts the wind on the port bow, so angle
                // grows to port: at 0 the source is dead ahead, at 90 it is on the port beam.
                float degrees0 = 360f * i / MeshSegments;
                float degrees1 = 360f * (i + 1) / MeshSegments;

                float a0 = degrees0 * Mathf.Deg2Rad;
                float a1 = degrees1 * Mathf.Deg2Rad;

                Vector3 d0 = new Vector3(-Mathf.Sin(a0), 0f, Mathf.Cos(a0));
                Vector3 d1 = new Vector3(-Mathf.Sin(a1), 0f, Mathf.Cos(a1));

                Color c0 = ColorAt(colors, degrees0);
                Color c1 = ColorAt(colors, degrees1);

                int v = i * 4;
                vertices[v + 0] = d0 * inner;
                vertices[v + 1] = d0 * outer;
                vertices[v + 2] = d1 * outer;
                vertices[v + 3] = d1 * inner;

                vertexColors[v + 0] = c0;
                vertexColors[v + 1] = c0;
                vertexColors[v + 2] = c1;
                vertexColors[v + 3] = c1;

                int t = i * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.colors = vertexColors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
        }

        /// <summary>
        /// The color for an arbitrary bearing, interpolated between the two nearest evaluated ones.
        ///
        /// Sample i stands for the CENTRE of its arc, at 360*(i+0.5)/Segments, which is why the
        /// index is offset by half a step before interpolating. Wrapping is explicit and handles
        /// negative indices, so the seam at dead ahead is as smooth as everywhere else rather than
        /// being the one hard edge left in the ring.
        /// </summary>
        private static Color ColorAt(Color[] colors, float degrees)
        {
            int count = colors.Length;
            if (count == 0) return DeadColor;
            if (count == 1) return colors[0];

            float position = degrees / 360f * count - 0.5f;
            int lower = Mathf.FloorToInt(position);
            float fraction = position - lower;

            int a = ((lower % count) + count) % count;
            int b = ((lower + 1) % count + count) % count;

            return Color.Lerp(colors[a], colors[b], fraction);
        }
    }
}
