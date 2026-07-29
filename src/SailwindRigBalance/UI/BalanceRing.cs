using System;
using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// A ring around the boat, coloured by what the helm would do at every point of sail.
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
    /// the coloured arc tells you which.
    ///
    /// Built as one mesh with interpolated vertex colours rather than a line renderer per segment.
    /// LineRenderer's gradient tops out at eight keys, far short of the resolution this needs, and
    /// two dozen renderers to draw one ring would be silly. The unlit shader the markers already
    /// use multiplies by vertex colour, so the same shader works unchanged.
    ///
    /// It gets its OWN material, never the helm link's. The link's material carries the current
    /// verdict colour in _Color, and Sprites/Default multiplies _Color into the vertex colour, so
    /// sharing it multiplied every one of the ring's verdicts by the verdict at the one angle the
    /// arrow happened to point at. A lee-helm reading painted the entire ring red, which is exactly
    /// the failure the grey guard below was added to prevent, and the two were indistinguishable.
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
        /// Quads the ring is drawn from, every five degrees, with colour interpolated between the
        /// evaluated bearings. Geometry is nearly free, so the drawn resolution is decoupled from
        /// the sampled one: the ring reads as a continuous wash rather than a colour wheel, without
        /// paying for an evaluation per quad.
        /// </summary>
        private const int MeshSegments = 72;

        /// <summary>
        /// How far past the balanced band the helm has to go before the colour saturates, in
        /// multiples of the band. Inside the band it is flat green, because that is what balanced
        /// means; beyond it the colour ramps so the ring shows HOW one-handed she is, not merely
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
        /// ring painted it grey; a fore-and-aft rig sheeted flat can be under the ramp at 30 and the
        /// ring painted a confident verdict on forces of order a newton.
        ///
        /// Comparing each angle's output against the best output anywhere on the sweep asks the
        /// question directly, in the rig's own terms, and needs no assumption about geometry.
        /// </summary>
        internal const float DeadOutputFraction = 0.10f;

        private static readonly Color DeadColour = new Color(0.45f, 0.45f, 0.45f, 0.55f);

        private GameObject _go;
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private Vector3 _centreLocal;
        private float _radius = 10f;

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
            if (_arrowGo != null) UnityEngine.Object.Destroy(_arrowGo);
            _mesh = null;
            _go = null;
            _filter = null;
            _renderer = null;
            _arrowMesh = null;
            _arrowGo = null;
            _dragging = false;
            MaxOutput = 0f;
        }

        /// <summary>Keeps the ring on the boat without parenting to it, as the markers do.</summary>
        public void Follow(Transform boat)
        {
            if (_go == null || boat == null) return;
            _go.transform.SetPositionAndRotation(boat.TransformPoint(_centreLocal), boat.rotation);
            if (_arrowGo != null)
                _arrowGo.transform.SetPositionAndRotation(_go.transform.position, _go.transform.rotation);
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

            Vector3 centre = _go.transform.position;
            Vector3 handle = centre + DirectionFor(WindAngle, boat) * _radius;

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
            var plane = new Plane(boat.up, centre);
            Ray ray = camera.ScreenPointToRay(Input.mousePosition);
            float enter;
            if (!plane.Raycast(ray, out enter)) return;

            Vector3 local = boat.InverseTransformDirection(ray.GetPoint(enter) - centre);
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
            // Rebuilt colours are cheap; the mesh is seven vertices.
            BuildArrowMesh(on);
        }

        /// <summary>
        /// Sweeps the apparent wind right round the boat and rebuilds the ring from the verdicts.
        /// Runs on the debounced rebuild, never per frame.
        ///
        /// Two passes over the half circle, not one: the dead-angle test compares each angle's
        /// output against the best found anywhere on the sweep, so every angle has to be evaluated
        /// before any of them can be coloured.
        /// </summary>
        public void Rebuild(GameObject boatRoot, Transform boat, RigBalance current,
            float balancedMetres, Material material, int layer, Transform parent)
        {
            try
            {
                if (_go == null) Create(material, layer, parent);
                if (_go == null) return;

                _centreLocal = boat.InverseTransformPoint(current.HullOrigin);

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
                _pendingBalancedMetres = balancedMetres;
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
        private float _pendingBalancedMetres;
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

                var colours = new Color[Segments];
                for (int i = 0; i < Segments; i++)
                {
                    colours[i] = _pendingEvaluated[i]
                        ? VerdictFor(_pendingSampled[i], _pendingBest, _pendingBalancedMetres)
                        : DeadColour;
                }

                BuildMesh(_radius, _pendingBand, colours);
                UpdateArrow(boat);

                LogSweep(_pendingSampled, _pendingEvaluated, _pendingBest,
                    _pendingBalancedMetres, _pendingMilliseconds);

                LogMirrorAudit(_pendingSampled, _pendingEvaluated, boat);
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
        /// True once after a sweep finishes, so the caller can re-colour the markers.
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
        /// Per segment: the bearing, the verdict letter, the lever in metres, and the side force as
        /// a percentage of total output, which is the quantity that decides whether there is a
        /// verdict at all. B balanced, W weather, L lee, dash no verdict, x failed to evaluate.
        /// </summary>
        private static void LogSweep(RigBalance[] sampled, bool[] evaluated, float maxOutput,
            float balancedMetres, float milliseconds)
        {
            try
            {
                var line = new System.Text.StringBuilder(
                    $"[Ring] sweep of {Segments} bearings in {milliseconds:F1} ms, "
                    + $"band {balancedMetres:F2} m, best output {maxOutput:F0} N:");

                for (int i = 0; i < Segments; i++)
                {
                    float angle = 360f * (i + 0.5f) / Segments;
                    if (!evaluated[i])
                    {
                        line.Append($" {angle:F0}:x");
                        continue;
                    }

                    RigBalance b = sampled[i];
                    char verdict = IsDead(b, maxOutput) ? '-'
                        : (Mathf.Abs(b.HelmMetres) < balancedMetres ? 'B'
                        : (b.HelmMetres > 0f ? 'W' : 'L'));

                    float share = b.TotalOutput > 0.0001f
                        ? 100f * Mathf.Abs(b.TotalSideForce) / b.TotalOutput
                        : 0f;

                    line.Append($" {angle:F0}:{verdict}{b.HelmMetres:F1}/{share:F0}%");
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

        /// <summary>The bearing a sample stands for: the centre of its arc, not its lower edge.</summary>
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
        /// chosen trim negates, a centre of effort keeps its height and its station along the boat and
        /// negates its offset from the centreline. The helm lever, a ratio of two quantities that both
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
        /// </summary>
        private static void LogMirrorAudit(RigBalance[] sampled, bool[] evaluated, Transform boat)
        {
            try
            {
                if (boat == null || sampled == null || evaluated == null) return;

                int pairs = 0, broken = 0, worst = -1;
                float worstGap = 0f;

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
                    if (gap > worstGap) { worstGap = gap; worst = i; }
                }

                if (broken == 0 || worst < 0)
                {
                    Plugin.Log.LogInfo($"[Ring] mirror audit: {pairs} pairs, symmetric.");
                    return;
                }

                Plugin.Log.LogWarning($"[Ring] mirror audit: {broken} of {pairs} pairs asymmetric, "
                    + $"worst {Bearing(worst):F0}/{Bearing(Segments - 1 - worst):F0} off by "
                    + $"{worstGap * 100f:F1} percent.");

                DumpMirrorPair(sampled[worst], sampled[Segments - 1 - worst], boat,
                    Bearing(worst), Bearing(Segments - 1 - worst));
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
        private static void DumpMirrorPair(RigBalance a, RigBalance b, Transform boat,
            float bearingA, float bearingB)
        {
            Plugin.Log.LogWarning($"[Ring] mirror {bearingA:F0} vs {bearingB:F0}: "
                + $"output {a.TotalOutput:F0} vs {b.TotalOutput:F0} N, "
                + $"side {a.TotalSideForce:F0} vs {b.TotalSideForce:F0} N (want negated), "
                + $"yawSide {a.YawFromSide:F0} vs {b.YawFromSide:F0}, "
                + $"yawThrust {a.YawFromThrust:F0} vs {b.YawFromThrust:F0}, "
                + $"lever {a.HelmMetres:F2} vs {b.HelmMetres:F2} m");

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

                // Boat-local, so "negates" and "matches" mean what they say regardless of the hull's
                // heading in the world. Both are read through the same transform, and the hull does
                // not move in a shipyard, so this comparison is not itself a source of error.
                Vector3 pa = boat.InverseTransformPoint(ca.CentreOfEffort);
                Vector3 pb = boat.InverseTransformPoint(cb.CentreOfEffort);

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
                    + $"ce.z {pa.z:F2} vs {pb.z:F2}");
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
        /// The verdict colour for one evaluated angle. Shared with the helm link through
        /// <see cref="IsDead"/> so the ring behind the markers cannot disagree with them.
        /// </summary>
        internal static Color VerdictFor(RigBalance balance, float maxOutput, float balancedMetres)
        {
            if (IsDead(balance, maxOutput)) return DeadColour;
            return HelmColour(balance.HelmMetres, balancedMetres);
        }

        /// <summary>
        /// The helm as a continuous colour rather than one of three.
        ///
        /// Green at balanced, ramping out to blue for weather helm and red for lee helm as the
        /// lever leaves the band. A diverging scale with the safe state in the middle, because the
        /// quantity is signed and zero is the good value: the two directions are genuinely different
        /// failures, not two ends of one axis.
        ///
        /// Three flat colours threw away the magnitude, which is most of what a player wants. A rig
        /// half a metre outside the band and a rig five metres outside it both painted the same red,
        /// so there was no way to see whether an edit had helped unless it crossed a threshold. The
        /// ramp makes an improvement visible while it is still an improvement.
        ///
        /// Used by the ring, the helm link and the reef track alike, so the three cannot drift into
        /// disagreeing about what a colour means.
        /// </summary>
        internal static Color HelmColour(float helmMetres, float balancedMetres)
        {
            if (balancedMetres <= 0.0001f) return ShipyardGizmo.BalancedColour;

            float bands = Mathf.Abs(helmMetres) / balancedMetres;

            // Square-rooted, so the hue commits early and then deepens. A linear ramp spends most of
            // its range near the neutral colour, which hides the first and most useful part of the
            // signal: the point where a rig starts to go one-handed, not the point where it is
            // hopeless. This is most of the way to blue or red by a quarter of the range.
            float t = Mathf.Sqrt(Mathf.InverseLerp(1f, SaturateAtBands, bands));

            Color extreme = helmMetres > 0f
                ? ShipyardGizmo.WeatherHelmColour
                : ShipyardGizmo.LeeHelmColour;

            return Color.Lerp(ShipyardGizmo.BalancedColour, extreme, t);
        }

        /// <summary>
        /// Whether this reading carries no verdict at all: either the sails are barely drawing, or
        /// there is so little side force that the ratio underneath the verdict is noise.
        /// </summary>
        internal static bool IsDead(RigBalance balance, float maxOutput)
        {
            if (maxOutput > 0f && balance.TotalOutput < DeadOutputFraction * maxOutput) return true;
            return !balance.HelmMeaningful;
        }

        internal static Color NoVerdictColour => DeadColour;

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

            Color colour = highlighted
                ? new Color(1f, 1f, 1f, 1f)
                : new Color(0.98f, 0.98f, 0.92f, 0.90f);

            var colours = new Color[vertices.Length];
            for (int i = 0; i < colours.Length; i++) colours[i] = colour;

            _arrowMesh.Clear();
            _arrowMesh.vertices = vertices;
            _arrowMesh.colors = colours;
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
            arrowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            arrowRenderer.receiveShadows = false;
            arrowRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            arrowRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _arrowMesh = new Mesh { name = "WindArrowMesh" };
            _arrowMesh.MarkDynamic();
            arrowFilter.sharedMesh = _arrowMesh;
        }

        /// <summary>
        /// One quad per drawn segment, each edge carrying the colour interpolated for its own
        /// bearing, so the hardware washes the colour along the ring instead of stepping it.
        ///
        /// The quads used to be flat, deliberately, so that neighbouring verdicts could not blend.
        /// That was the right call while a verdict was one of three colours and blending two of them
        /// would have invented a fourth. It stopped being right when the colour became a continuous
        /// function of the helm: with a ramp, the value between two sampled bearings really is
        /// between the two, and drawing it as a hard step is the lie. Twenty four flat bands also
        /// hid the magnitude entirely, so a rig just outside the band and a rig hopelessly outside
        /// it looked identical.
        ///
        /// At 72 quads that is 288 vertices, which is still nothing, and the sampling cost is
        /// unchanged because geometry and evaluation are now separate numbers.
        /// </summary>
        private void BuildMesh(float radius, float band, Color[] colours)
        {
            float inner = radius - band * 0.5f;
            float outer = radius + band * 0.5f;

            var vertices = new Vector3[MeshSegments * 4];
            var vertexColours = new Color[MeshSegments * 4];
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

                Color c0 = ColourAt(colours, degrees0);
                Color c1 = ColourAt(colours, degrees1);

                int v = i * 4;
                vertices[v + 0] = d0 * inner;
                vertices[v + 1] = d0 * outer;
                vertices[v + 2] = d1 * outer;
                vertices[v + 3] = d1 * inner;

                vertexColours[v + 0] = c0;
                vertexColours[v + 1] = c0;
                vertexColours[v + 2] = c1;
                vertexColours[v + 3] = c1;

                int t = i * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.colors = vertexColours;
            _mesh.triangles = triangles;
            _mesh.RecalculateBounds();
        }

        /// <summary>
        /// The colour for an arbitrary bearing, interpolated between the two nearest evaluated ones.
        ///
        /// Sample i stands for the CENTRE of its arc, at 360*(i+0.5)/Segments, which is why the
        /// index is offset by half a step before interpolating. Wrapping is explicit and handles
        /// negative indices, so the seam at dead ahead is as smooth as everywhere else rather than
        /// being the one hard edge left in the ring.
        /// </summary>
        private static Color ColourAt(Color[] colours, float degrees)
        {
            int count = colours.Length;
            if (count == 0) return DeadColour;
            if (count == 1) return colours[0];

            float position = degrees / 360f * count - 0.5f;
            int lower = Mathf.FloorToInt(position);
            float fraction = position - lower;

            int a = ((lower % count) + count) % count;
            int b = ((lower + 1) % count + count) % count;

            return Color.Lerp(colours[a], colours[b], fraction);
        }
    }
}
