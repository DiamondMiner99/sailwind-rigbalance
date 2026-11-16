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
    /// It gets its OWN material, never the helm bar's. The bar's material carries the current
    /// verdict color in _Color, and Sprites/Default multiplies _Color into the vertex color, so
    /// sharing it multiplied every one of the ring's verdicts by the verdict at the one angle the
    /// arrow happened to point at. A lee-helm reading painted the entire ring red, which is exactly
    /// the failure the gray guard below was added to prevent, and the two were indistinguishable.
    /// </summary>
    public class BalanceRing
    {
        /// <summary>
        /// Bearings the rig is actually evaluated at, every ten degrees. This is the expensive
        /// number: each one drives the game's force function about twelve times per sail, and the whole sweep
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
        /// Where the color ramp reaches its middle stop (orange or light blue), in multiples of
        /// the balanced band. Inside the band it is flat green, because that is what balanced
        /// means; beyond it the color ramps so the ring shows HOW one-handed she is, not merely
        /// that she is.
        /// </summary>
        internal const float MidAtBands = 3f;

        /// <summary>
        /// Where the ramp reaches its far stop (red or dark blue). This used to be 3 bands with a
        /// square-root ease, which put a rig at 1.5 bands halfway to full color; players reported
        /// the ring reading as plain green, blue or red, with helm the rudder holds without trouble
        /// already painted at the extreme (Discord, 2026-11-09). Eight bands is a judgment, not a
        /// measurement: the rudder's authority depends on boat speed (Rudder.cs:55-57), which the
        /// shipyard does not know.
        /// </summary>
        internal const float FarAtBands = 8f;

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



        private GameObject _arrowGo;
        private Mesh _arrowMesh;
        private bool _dragging;
        private bool _angleChanged;

        /// <summary>
        /// The best total output found anywhere on the last sweep. The markers and the helm bar
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

        /// <summary>
        /// The wind speed the boat is heeled in, m/s. The arrow's length shows it and the knob on its
        /// outer end sets it. The helm lever does not depend on it upright, because every sail force
        /// is linear in the wind (Sail.cs:410); it matters through how far she heels.
        /// </summary>
        public float WindSpeed { get; private set; } = RigModel.SyntheticWindSpeed;

        public void SetWindSpeed(float metersPerSecond)
        {
            WindSpeed = Mathf.Clamp(metersPerSecond, Config.RigBalanceLimits.MinWindSpeed,
                Config.RigBalanceLimits.MaxWindSpeed);
        }

        /// <summary>True once after the arrow has been dragged, so the caller can recompute.</summary>
        public bool ConsumeAngleChanged()
        {
            if (!_angleChanged) return false;
            _angleChanged = false;
            return true;
        }

        /// <summary>
        /// True once when a wind speed drag ends. The ring's colors depend on the speed through the
        /// heel at every bearing, so unlike the angle, a new speed needs a new sweep; it is asked for
        /// once on release rather than every frame of the drag.
        /// </summary>
        public bool ConsumeSpeedSettled()
        {
            if (!_speedSettled) return false;
            _speedSettled = false;
            return true;
        }

        private bool _speedSettled;

        /// <summary>True while the knob, not the arrow, is being dragged.</summary>
        private bool _draggingSpeed;

        public bool Dragging => _dragging;

        /// <summary>The knob on the arrow's outer end, in world space, for the speed label.</summary>
        public Vector3 KnobWorld
        {
            get
            {
                if (_go == null) return Vector3.zero;
                return _go.transform.TransformPoint(ArrowDirectionLocal * KnobDistance);
            }
        }

        /// <summary>Whether the ring and its arrow are on screen.</summary>
        public bool Visible => _go != null && _go.activeSelf;

        public bool Exists => _go != null;

        public void SetVisible(bool visible)
        {
            if (_go != null && _go.activeSelf != visible) _go.SetActive(visible);
            if (_arrowGo != null && _arrowGo.activeSelf != visible) _arrowGo.SetActive(visible);

            // A drag in progress holds the shipyard camera still every frame. Hiding the ring
            // mid-drag would leave that suppression running with nothing on screen to explain it.
            if (!visible)
            {
                _dragging = false;
                _draggingSpeed = false;
                _focus = ArrowPart.None;
            }
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
            _draggingSpeed = false;
            _focus = ArrowPart.None;
            _speedSettled = false;
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

            if (!_dragging)
            {
                // Whichever part the cursor is over lights up, and the other fades, before any
                // click, so it is plain which one a press will take. Without that the two targets
                // overlapped unseen and a press meant to turn the wind could set its speed
                // (Discord, 2026-11-15).
                ArrowPart under = PartUnderCursor(camera, center, boat);
                SetFocus(under);
                if (under == ArrowPart.None || !Input.GetMouseButtonDown(0)) return;

                _dragging = true;
                _draggingSpeed = under == ArrowPart.Knob;
                SuppressCameraLook();
                return;
            }

            if (!Input.GetMouseButton(0))
            {
                _dragging = false;
                SetFocus(ArrowPart.None);

                // One more rebuild on release, so the settled state is evaluated and logged with
                // _dragging already false. Without it the last frame of the drag recomputed while
                // still dragging, which the gizmo suppresses from the log, and then nothing was ever
                // scheduled again: the last line in the log described the angle the arrow was at
                // BEFORE the drag, with nothing to say it had moved.
                _angleChanged = true;
                if (_draggingSpeed) _speedSettled = true;
                _draggingSpeed = false;
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

            if (_draggingSpeed)
            {
                // Only the distance along the arrow counts, so the bearing cannot wander while the
                // speed is being set. Rounded to half a meter per second so the label settles.
                float along = Vector3.Dot(local, ArrowDirectionLocal);
                float speed = SpeedForKnobDistance(along);
                speed = Mathf.Round(speed * 2f) * 0.5f;
                speed = Mathf.Clamp(speed, Config.RigBalanceLimits.MinWindSpeed, Config.RigBalanceLimits.MaxWindSpeed);
                if (Mathf.Abs(speed - WindSpeed) < 0.01f) return;

                WindSpeed = speed;
                _angleChanged = true;
                UpdateArrow(boat);
                return;
            }

            // Inverse of DirectionFor: that maps an angle to (-sin, 0, cos).
            float degrees = Mathf.Repeat(Mathf.Atan2(-local.x, local.z) * Mathf.Rad2Deg, 360f);
            if (Mathf.Abs(Mathf.DeltaAngle(degrees, WindAngle)) < 0.5f) return;

            WindAngle = degrees;
            _angleChanged = true;
            UpdateArrow(boat);
        }

        private const float GrabRadiusPixels = 40f;

        /// <summary>How far past the knob's drawn edge a press still takes it, in pixels.</summary>
        private const float KnobSlackPixels = 14f;

        /// <summary>The two things on the arrow a press can take: the arrow turns the wind, the knob sets its speed.</summary>
        private enum ArrowPart { None, Arrow, Knob }

        /// <summary>The part lit up: the one under the cursor, or the one being dragged.</summary>
        private ArrowPart _focus;

        /// <summary>
        /// The part of the arrow a press here would take, or None.
        ///
        /// The arrow's target is the whole arrow, tip to tail, projected to the screen, because a
        /// single point on the ring lands on the head in one arrow style and far from it in the
        /// other. The knob's is its drawn disc plus a little slack. They meet at the arrow's outer
        /// end, where the knob sits, so where both reach the nearer one wins, measured to the
        /// arrow's line and to the knob's edge. Inside the disc that is always the knob; on the
        /// shaft just short of it, the arrow. The old rule tested a fixed 22 pixels round the knob's
        /// center first, which took the end of the shaft for the knob when it was drawn small and
        /// gave the arrow a press just off a knob drawn large.
        /// </summary>
        private ArrowPart PartUnderCursor(Camera camera, Vector3 center, Transform boat)
        {
            var mouse = new Vector2(Input.mousePosition.x, Input.mousePosition.y);
            Vector3 direction = DirectionFor(WindAngle, boat);

            float toArrow = float.MaxValue;
            Vector3 inner = camera.WorldToScreenPoint(center + direction * (_radius - ArrowHeadLength));
            Vector3 outer = camera.WorldToScreenPoint(center + direction * (_radius + ArrowShaftLength));
            if (inner.z > 0f && outer.z > 0f)   // not behind the camera
                toArrow = DistanceToSegment(mouse, new Vector2(inner.x, inner.y), new Vector2(outer.x, outer.y));

            float toKnob = float.MaxValue;
            Vector3 knobWorld = KnobWorld;
            Vector3 knob = camera.WorldToScreenPoint(knobWorld);
            if (knob.z > 0f)
            {
                // Measured across the arrow, which the camera foreshortens least.
                Vector3 across = Vector3.Cross(boat.up, direction).normalized;
                Vector3 rim = camera.WorldToScreenPoint(knobWorld + across * KnobRadius);
                float knobPixels = Vector2.Distance(new Vector2(knob.x, knob.y), new Vector2(rim.x, rim.y));
                toKnob = Mathf.Max(0f, Vector2.Distance(mouse, new Vector2(knob.x, knob.y)) - knobPixels);
            }

            bool arrowInReach = toArrow <= GrabRadiusPixels;
            bool knobInReach = toKnob <= KnobSlackPixels;
            if (knobInReach && (!arrowInReach || toKnob <= toArrow)) return ArrowPart.Knob;
            return arrowInReach ? ArrowPart.Arrow : ArrowPart.None;
        }

        /// <summary>
        /// Stops the shipyard camera looking around while the arrow is being dragged. The rotator
        /// enables MouseLook on the camera and on its first child; both are switched off here.
        /// </summary>
        private static void SuppressCameraLook()
        {
            // The showcase has its own camera and no shipyard rotator is fighting the drag, so the
            // game's camera components are left exactly as they are.
            if (HullShowcase.Active) return;

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

        private void SetFocus(ArrowPart part)
        {
            if (_arrowGo == null) return;
            _focus = part;
            var r = _arrowGo.GetComponent<MeshRenderer>();
            if (r != null) r.enabled = true;
            // Rebuilt every frame the arrow is idle, as it always was, so it keeps up with a bearing,
            // speed or style set from elsewhere. Two dozen vertices.
            BuildArrowMesh();
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
                // are thirty six times twelve per sail of those. Making the answer cruder to fit it
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
                RigModel.LogSwingLimits(_pendingSnapshot);
                _pendingBalancedMeters = balancedMeters;
                _pendingBand = band;

                // The whole sweep heels her in one wind, the one set when it started, so a drag of the
                // knob mid-sweep cannot paint half the ring at one speed and half at another.
                _pendingWindSpeed = WindSpeed;
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
        private float _pendingWindSpeed;
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
                        null, 1f, _pendingHullLength, _pendingSnapshot, _pendingWindSpeed);
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
                var ticks = new float[Segments];
                for (int i = 0; i < Segments; i++)
                {
                    colors[i] = _pendingEvaluated[i]
                        ? VerdictFor(_pendingSampled[i], _pendingBest, _pendingBalancedMeters)
                        : DeadColor;
                    ticks[i] = _pendingEvaluated[i]
                        ? TickFor(_pendingSampled[i], _pendingBest, _pendingBalancedMeters)
                        : 0f;
                }

                BuildMesh(_radius, _pendingBand, colors, ticks);
                UpdateArrow(boat);

                LogSweep(_pendingSampled, _pendingEvaluated, _pendingBest,
                    _pendingBalancedMeters, _pendingMilliseconds);

                LogMirrorAudit(_pendingSampled, _pendingEvaluated, _pendingBalancedMeters);
                RigModel.LogLimitsChangedDuringSweep(_pendingSnapshot);

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
        /// the helm bar are first drawn against the PREVIOUS sweep's yardstick, or against none at
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

                    // The heel this bearing was read at, where heel is modeled. A plus marks a
                    // bearing where she found no balance and lies over further than the model follows;
                    // a question mark one where the heel search ran out of rounds.
                    if (b.Attitude.Valid)
                        line.Append($"@{Mathf.Abs(b.Attitude.Heel):F0}{(b.Attitude.Overpowered ? "+" : "")}"
                            + (b.HeelUnsettled ? "?" : ""));
                }

                RigBalance any = default(RigBalance);
                for (int i = 0; i < Segments; i++) if (evaluated[i]) { any = sampled[i]; break; }
                line.Append(any.Attitude.Valid
                    ? $" (heeled in {any.WindSpeed:F1} m/s; @ is degrees of heel, + laid over, ? heel not settled)"
                    : " (upright)");

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

        /// <summary>
        /// Whether a mirror pair differs only because the game shades the two tacks differently.
        ///
        /// Every sail's shadow collider is one flat single-sided plane (wind_shadow_col_mesh) and the
        /// game's physics settings have queriesHitBackfaces off, so a shading ray is blocked by
        /// another sail only when it meets that sail's front face. Reflected to the other tack, the
        /// same ray meets the back face and passes. SailShadowCol.CheckShadow casts its rays the same
        /// way, so at sea the two tacks really are shaded differently and the ring shows it. Found
        /// 2026-11-13: with heel and trims mirroring exactly, the only remaining difference was
        /// shading (a square shaded 0% against 51%), and the forces divided by (1 - shading) matched.
        ///
        /// True when some sail's shading differs between the two readings and the forces with
        /// shading taken back out agree.
        /// </summary>
        private static bool ShadingOnly(RigBalance a, RigBalance b)
        {
            if (a.Sails == null || b.Sails == null || a.Sails.Count != b.Sails.Count) return false;

            float fwdA = 0f, fwdB = 0f, sideA = 0f, sideB = 0f;
            bool shadingDiffers = false;
            for (int k = 0; k < a.Sails.Count; k++)
            {
                SailContribution ca = a.Sails[k], cb = b.Sails[k];
                float keepA = 1f - ca.Shading, keepB = 1f - cb.Shading;
                if (keepA < 0.05f || keepB < 0.05f) return false;
                if (Mathf.Abs(ca.Shading - cb.Shading) > 0.005f) shadingDiffers = true;
                fwdA += ca.ForwardForce / keepA;
                fwdB += cb.ForwardForce / keepB;
                sideA += Mathf.Abs(ca.SideForce / keepA);
                sideB += Mathf.Abs(cb.SideForce / keepB);
            }

            return shadingDiffers
                && RelativeGap(fwdA, fwdB) <= MirrorTolerance * 2f
                && RelativeGap(sideA, sideB) <= MirrorTolerance * 2f;
        }

        private static void LogMirrorAudit(RigBalance[] sampled, bool[] evaluated, float balancedMeters)
        {
            try
            {
                if (sampled == null || evaluated == null) return;

                int pairs = 0, broken = 0, worst = -1;
                float worstGap = 0f;
                var signature = new System.Text.StringBuilder();

                // The helm, separately. The force test below cannot see a ring whose two halves
                // disagree about the helm while making the same force, which is the case where the
                // sails pull equally hard on both tacks but act in different places, and one sweep
                // of that shape went through this audit with nothing reported: 45 degrees
                // read 1.7 m of weather helm against 5.1 m at 315, and 125 read 4.0 m of lee helm
                // against 8.5 m of weather at 235. The lever is only compared where both halves have
                // a reading, since near a run it is a ratio of vanishing terms, and a gap counts
                // once it is as wide as the balanced band, which is where the ring shows it.
                int shadingOnly = 0;
                int helmBroken = 0, worstHelm = -1;
                float worstHelmGap = 0f;
                float helmTolerance = Mathf.Max(balancedMeters, 0.25f);
                var helmSignature = new System.Text.StringBuilder();

                for (int i = 0; i < Segments / 2; i++)
                {
                    int j = Segments - 1 - i;
                    if (!evaluated[i] || !evaluated[j]) continue;
                    pairs++;

                    // The game's own one-sided shading, not a fault in the model: counted, not warned.
                    if (ShadingOnly(sampled[i], sampled[j])) { shadingOnly++; continue; }

                    float leverI, leverJ;
                    if (LeverOf(sampled[i], out leverI) && LeverOf(sampled[j], out leverJ))
                    {
                        float helmGap = Mathf.Abs(leverI - leverJ);
                        if (helmGap > helmTolerance)
                        {
                            helmBroken++;
                            helmSignature.Append(i).Append(',');
                            if (helmGap > worstHelmGap) { worstHelmGap = helmGap; worstHelm = i; }
                        }
                    }

                    // Total output and side force as well as the lever: the lever is a ratio and can
                    // agree by luck while both of its terms are wrong, and it is suppressed outright
                    // wherever the reading is judged meaningless, which is where the trouble is.
                    float gap = Mathf.Max(
                        RelativeGap(sampled[i].TotalOutput, sampled[j].TotalOutput),
                        RelativeGap(Mathf.Abs(sampled[i].TotalSideForce),
                                    Mathf.Abs(sampled[j].TotalSideForce)));

                    if (gap <= MirrorTolerance) continue;

                    broken++;
                    signature.Append(i).Append(',');
                    if (gap > worstGap) { worstGap = gap; worst = i; }
                }

                string answer = $"{pairs}:{signature}|{helmSignature}|{shadingOnly}";
                if (answer == _loggedAudit) return;
                _loggedAudit = answer;

                string shadingNote = shadingOnly > 0
                    ? $"; {shadingOnly} differ only in sail shading, which the game does per tack (single-sided shadow planes)"
                    : "";

                if ((broken == 0 || worst < 0) && (helmBroken == 0 || worstHelm < 0))
                {
                    Plugin.Log.LogInfo($"[Ring] mirror audit: {pairs} pairs, symmetric{shadingNote}.");
                    return;
                }

                if (shadingOnly > 0)
                    Plugin.Log.LogInfo($"[Ring] mirror audit: {shadingOnly} of {pairs} pairs differ only in sail "
                        + "shading, which the game does per tack (single-sided shadow planes).");

                if (broken > 0 && worst >= 0)
                {
                    Plugin.Log.LogWarning($"[Ring] mirror audit: {broken} of {pairs} pairs asymmetric in "
                        + $"force, worst {Bearing(worst):F0}/{Bearing(Segments - 1 - worst):F0} off by "
                        + $"{worstGap * 100f:F1} percent.");

                    DumpMirrorPair(sampled[worst], sampled[Segments - 1 - worst],
                        Bearing(worst), Bearing(Segments - 1 - worst));
                }

                if (helmBroken > 0 && worstHelm >= 0)
                {
                    Plugin.Log.LogWarning($"[Ring] mirror audit: {helmBroken} of {pairs} pairs disagree "
                        + $"about the helm by more than {helmTolerance:F2} m, worst "
                        + $"{Bearing(worstHelm):F0}/{Bearing(Segments - 1 - worstHelm):F0} off by "
                        + $"{worstHelmGap:F2} m.");

                    DumpHelmPair(sampled[worstHelm], sampled[Segments - 1 - worstHelm],
                        Bearing(worstHelm), Bearing(Segments - 1 - worstHelm));
                }

                Plugin.Log.LogInfo("[Ring] mirror audit: reported again only if a different set of "
                    + "pairs disagrees.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] mirror audit failed: " + e.Message);
            }
        }

        /// <summary>The helm lever a bearing reads, where it has one: the verdict, or the off-scale figure.</summary>
        private static bool LeverOf(RigBalance balance, out float lever)
        {
            lever = balance.HelmMeaningful ? balance.HelmMeters
                : balance.HelmOffScale ? balance.RawLever
                : 0f;
            return balance.HelmMeaningful || balance.HelmOffScale;
        }

        /// <summary>
        /// Splits one mirror pair's helm into what each sail contributes on each tack, and lists
        /// the sails whose contribution differs, biggest difference first.
        ///
        /// The lever is minus the total turning moment over the total side force, so each sail's
        /// share of it is minus its own turning moment over that same total, and the shares plus
        /// the hull's windage add up to the reading. Under reflection every share should match. A
        /// sail whose share does not is the sail making the ring lopsided, and its trims, limits
        /// and fore-and-aft position on each tack say why.
        /// </summary>
        private static void DumpHelmPair(RigBalance a, RigBalance b, float bearingA, float bearingB)
        {
            float leverA, leverB;
            LeverOf(a, out leverA);
            LeverOf(b, out leverB);

            Plugin.Log.LogWarning($"[Ring] helm {bearingA:F0} vs {bearingB:F0}: {leverA:+0.00;-0.00} vs "
                + $"{leverB:+0.00;-0.00} m (want equal; positive is weather), side force "
                + $"{a.TotalSideForce:F0} vs {b.TotalSideForce:F0} N (want negated)");

            if (a.Sails == null || b.Sails == null) return;
            if (Mathf.Abs(a.TotalSideForce) < 1f || Mathf.Abs(b.TotalSideForce) < 1f) return;

            var rows = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<float, string>>();
            float sumA = 0f, sumB = 0f;

            foreach (SailContribution ca in a.Sails)
            {
                float shareA = -(ca.YawFromSide + ca.YawFromThrust) / a.TotalSideForce;
                sumA += shareA;

                SailContribution cb = default(SailContribution);
                bool found = false;
                foreach (SailContribution candidate in b.Sails)
                {
                    if (ReferenceEquals(candidate.Sail, ca.Sail)) { cb = candidate; found = true; break; }
                }

                string label = RigModel.SailLabel(ca.Sail, ca.Name);
                if (!found)
                {
                    rows.Add(new System.Collections.Generic.KeyValuePair<float, string>(Mathf.Abs(shareA),
                        $"{label}: {shareA:+0.00;-0.00} m at {bearingA:F0}, not drawing at {bearingB:F0}"));
                    continue;
                }

                float shareB = -(cb.YawFromSide + cb.YawFromThrust) / b.TotalSideForce;
                float diff = Mathf.Abs(shareA - shareB);
                if (diff < 0.1f) continue;

                rows.Add(new System.Collections.Generic.KeyValuePair<float, string>(diff,
                    $"{label}: helm share {shareA:+0.00;-0.00} vs {shareB:+0.00;-0.00} m, "
                    + $"trim {ca.Trim:F1} vs {cb.Trim:F1} (limits {ca.MinTrim:+0;-0;0} to {ca.MaxTrim:+0;-0;0}), "
                    + $"side {ca.SideForce:F0} vs {cb.SideForce:F0} N, "
                    + $"ce.z {ca.Arm.z:F2} vs {cb.Arm.z:F2}, ce.x {ca.Arm.x:F2} vs {cb.Arm.x:F2}"));
            }

            foreach (SailContribution cb in b.Sails)
            {
                float shareB = -(cb.YawFromSide + cb.YawFromThrust) / b.TotalSideForce;
                sumB += shareB;

                bool seen = false;
                foreach (SailContribution candidate in a.Sails)
                {
                    if (ReferenceEquals(candidate.Sail, cb.Sail)) { seen = true; break; }
                }
                if (seen) continue;

                rows.Add(new System.Collections.Generic.KeyValuePair<float, string>(Mathf.Abs(shareB),
                    $"{RigModel.SailLabel(cb.Sail, cb.Name)}: not drawing at {bearingA:F0}, "
                    + $"{shareB:+0.00;-0.00} m at {bearingB:F0}"));
            }

            rows.Sort((x, y) => y.Key.CompareTo(x.Key));
            int shown = Mathf.Min(rows.Count, 8);
            for (int k = 0; k < shown; k++) Plugin.Log.LogWarning("[Ring]   " + rows[k].Value);
            if (rows.Count > shown)
                Plugin.Log.LogWarning($"[Ring]   ...and {rows.Count - shown} more sail(s) differing by less.");

            Plugin.Log.LogWarning($"[Ring]   hull windage and rounding: {leverA - sumA:+0.00;-0.00} vs "
                + $"{leverB - sumB:+0.00;-0.00} m");
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
                    + $"trim {ca.Trim:F1} vs {cb.Trim:F1}{(trimOk ? "" : $" WANT {-ca.Trim:F1}")} "
                    + $"(limits {ca.MinTrim:+0;-0;0} to {ca.MaxTrim:+0;-0;0}), "
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
                    + $"clamped {ca.DriveClamped} vs {cb.DriveClamped}, "
                    + $"shaded {ca.Shading * 100f:F0}% vs {cb.Shading * 100f:F0}%");
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
        /// The verdict color for one evaluated angle. Shared with the helm bar through
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
                    ? ShipyardGizmo.WeatherHelmFarColor
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
        /// Used by the ring, the helm bar and the reef track alike, so the three cannot drift into
        /// disagreeing about what a color means.
        /// </summary>
        internal static Color HelmColor(float helmMeters, float balancedMeters)
        {
            if (balancedMeters <= 0.0001f) return ShipyardGizmo.BalancedColor;

            float bands = Mathf.Abs(helmMeters) / balancedMeters;

            // Two linear legs through a middle stop. The middle stop is what makes the start of
            // the signal visible (green turning orange or light blue), so the old square-root ease
            // is no longer needed for that, and dropping it leaves room to see the far end deepen.
            bool weather = helmMeters > 0f;
            Color mid = weather ? ShipyardGizmo.WeatherHelmColor : ShipyardGizmo.LeeHelmMidColor;
            Color far = weather ? ShipyardGizmo.WeatherHelmFarColor : ShipyardGizmo.LeeHelmColor;

            if (bands <= MidAtBands)
                return Color.Lerp(ShipyardGizmo.BalancedColor, mid, Mathf.InverseLerp(1f, MidAtBands, bands));
            return Color.Lerp(mid, far, Mathf.InverseLerp(MidAtBands, FarAtBands, bands));
        }

        /// <summary>
        /// The same ramp over a signed position from -1 (far lee) through 0 (balanced) to +1 (far
        /// weather), for the ring button's texture, which shows the whole scale at once.
        /// </summary>
        internal static Color HelmColorAt(float signedPosition)
        {
            float bands = 1f + Mathf.Abs(signedPosition) * (FarAtBands - 1f);
            return HelmColor(Mathf.Sign(signedPosition) * bands, 1f);
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
        /// The helm at one bearing as a tick, for the HelmTicks setting: positive for weather helm,
        /// negative for lee, its size from just over zero at the edge of the balanced band to 1 at
        /// the ramp's far stop, and zero for balanced or no verdict, which get no tick. The same
        /// thresholds as the colors (VerdictFor, HelmColor), so tick and color never disagree.
        /// </summary>
        internal static float TickFor(RigBalance balance, float maxOutput, float balancedMeters)
        {
            if (IsDead(balance, maxOutput)) return 0f;
            if (balance.HelmOffScale) return balance.RawLever > 0f ? 1f : -1f;
            if (balancedMeters <= 0.0001f) return 0f;

            float bands = Mathf.Abs(balance.HelmMeters) / balancedMeters;
            if (bands <= 1f) return 0f;
            float size = Mathf.Max(Mathf.InverseLerp(1f, FarAtBands, bands), 0.001f);
            return balance.HelmMeters > 0f ? size : -size;
        }

        private static bool TicksWanted => Config.Settings.ShowHelmTicks != null
            && Config.Settings.ShowHelmTicks.Value;

        /// <summary>
        /// Rebuilds the arrow so it sits at the current wind bearing. By default it points inward at
        /// the boat, the direction the wind blows; the WindArrowStyle setting turns it round to point
        /// into the wind like a weather vane. It is drawn in the ring's own local space and rotated
        /// with the boat, so it needs no transform of its own beyond following.
        /// </summary>
        private void UpdateArrow(Transform boat)
        {
            if (_arrowGo == null || _arrowMesh == null) return;
            BuildArrowMesh();
        }

        private static bool WeatherVaneWanted => Config.Settings.WindArrowStyle != null
            && Config.Settings.WindArrowStyle.Value == Config.WindArrowStyles.WeatherVane;

        private float ArrowHeadLength => Mathf.Max(_radius * 0.16f, 0.8f);

        // The shaft grows with the wind speed, from 0.4 heads in a calm to 4.9 at 100 knots. At the
        // 10 m/s default it is about 1.2 heads, close to the fixed 1.5 it used to be, and at 30 m/s
        // about 3, the same lengths as when the range stopped at 30.
        private const float ShaftAtCalm = 0.4f;
        private const float ShaftAtStorm = 4.9f;

        private float SpeedFraction => Mathf.InverseLerp(Config.RigBalanceLimits.MinWindSpeed,
            Config.RigBalanceLimits.MaxWindSpeed, WindSpeed);

        private float ArrowShaftLength => ArrowHeadLength * Mathf.Lerp(ShaftAtCalm, ShaftAtStorm, SpeedFraction);

        private float KnobRadius => ArrowHeadLength * 0.28f;

        /// <summary>From the ring's center to the knob's center, which sits just past the arrow's outer end.</summary>
        private float KnobDistance => _radius + ArrowShaftLength + KnobRadius;

        /// <summary>The arrow's bearing in the ring's own frame, outward from the boat.</summary>
        private Vector3 ArrowDirectionLocal
        {
            get
            {
                float rad = WindAngle * Mathf.Deg2Rad;
                return new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            }
        }

        /// <summary>Inverse of KnobDistance: the wind speed that would put the knob this far out.</summary>
        private float SpeedForKnobDistance(float distance)
        {
            float shaftHeads = (distance - _radius - KnobRadius) / ArrowHeadLength;
            float t = Mathf.InverseLerp(ShaftAtCalm, ShaftAtStorm, shaftHeads);
            return Mathf.Lerp(Config.RigBalanceLimits.MinWindSpeed, Config.RigBalanceLimits.MaxWindSpeed, t);
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 span = b - a;
            float lengthSquared = span.sqrMagnitude;
            if (lengthSquared < 0.0001f) return Vector2.Distance(point, a);

            float along = Mathf.Clamp01(Vector2.Dot(point - a, span) / lengthSquared);
            return Vector2.Distance(point, a + span * along);
        }

        private const int KnobSegments = 16;

        /// <summary>The waterline's color, shared so the knob reads as the control for it.</summary>
        internal static readonly Color KnobColor = new Color(0.35f, 0.80f, 1f, 0.95f);

        /// <summary>The alpha the part not under the cursor fades to.</summary>
        private const float FadedAlpha = 0.30f;

        private void BuildArrowMesh()
        {
            if (_arrowMesh == null) return;

            float rad = WindAngle * Mathf.Deg2Rad;
            Vector3 d = new Vector3(-Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            Vector3 side = Vector3.Cross(Vector3.up, d).normalized;

            float head = ArrowHeadLength;
            float halfWidth = head * 0.55f;
            float shaft = ArrowShaftLength;
            float shaftHalf = halfWidth * 0.30f;

            // Downwind: the head's point is just inside the ring, its base on the ring, and the
            // shaft runs outward. Weather vane: the same footprint turned end for end, so the arrow
            // covers the same stretch of water and the ring point the drag grabs (HandleDrag) still
            // lies on it, now partway along the shaft.
            bool vane = WeatherVaneWanted;

            // Turning the arrow end for end alone would mirror it, which reverses the winding of
            // every triangle, and the Standard fallback material culls back faces. Flipping the
            // sideways axis as well makes it a half turn about the vertical instead, which keeps the
            // winding; the arrow is symmetric about its own axis, so its shape does not change.
            if (vane) side = -side;

            Vector3 inner = d * (_radius - head);
            Vector3 outer = d * (_radius + shaft);
            Vector3 tip = vane ? outer : inner;
            Vector3 headBase = vane ? outer - d * head : inner + d * head;
            Vector3 tail = vane ? inner : outer;

            // The knob that sets the wind speed: a disc just past the arrow's outer end, which is the
            // tail in the default style and the head with the weather vane. Wound the same way as the
            // ring's own quads, which the fallback material's back-face culling already accepts.
            Vector3 knobCenter = d * KnobDistance;
            float knobRadius = KnobRadius;

            var vertices = new Vector3[7 + 1 + KnobSegments];
            vertices[0] = tip;                                  // head
            vertices[1] = headBase + side * halfWidth;
            vertices[2] = headBase - side * halfWidth;
            vertices[3] = headBase + side * shaftHalf;          // shaft
            vertices[4] = headBase - side * shaftHalf;
            vertices[5] = tail + side * shaftHalf;
            vertices[6] = tail - side * shaftHalf;
            vertices[7] = knobCenter;
            for (int k = 0; k < KnobSegments; k++)
            {
                float a = 2f * Mathf.PI * k / KnobSegments;
                vertices[8 + k] = knobCenter + new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * knobRadius;
            }

            var triangles = new int[9 + KnobSegments * 3];
            int[] arrowTriangles = { 0, 1, 2, 3, 5, 6, 3, 6, 4 };
            Array.Copy(arrowTriangles, triangles, arrowTriangles.Length);
            for (int k = 0; k < KnobSegments; k++)
            {
                int t = 9 + k * 3;
                triangles[t + 0] = 7;
                triangles[t + 1] = 8 + k;
                triangles[t + 2] = 8 + (k + 1) % KnobSegments;
            }

            // The knob in the waterline's blue, so it reads as the control for the heel it shows.
            // The part under the cursor or in hand goes white and the other fades back.
            Color color = new Color(0.98f, 0.98f, 0.92f, 0.90f);
            Color knobColor = KnobColor;
            if (_focus == ArrowPart.Arrow)
            {
                color = Color.white;
                knobColor.a = FadedAlpha;
            }
            else if (_focus == ArrowPart.Knob)
            {
                color.a = FadedAlpha;
                knobColor = Color.white;
            }

            var colors = new Color[vertices.Length];
            for (int i = 0; i < 7; i++) colors[i] = color;
            for (int i = 7; i < colors.Length; i++) colors[i] = knobColor;

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
        }

        private void BuildMesh(float radius, float band, Color[] colors, float[] ticks)
        {
            FillRing(_mesh, radius, band, colors, TicksWanted ? ticks : null);
        }

        /// <summary>A tick's width, and its length past the band at its smallest and largest, in bands.</summary>
        private const float TickWidth = 0.5f, TickShortest = 0.6f, TickLongest = 3f;

        private static void FillRing(Mesh mesh, float radius, float band, Color[] colors, float[] ticks)
        {
            float inner = radius - band * 0.5f;
            float outer = radius + band * 0.5f;

            int tickCount = 0;
            if (ticks != null)
                foreach (float tick in ticks)
                    if (tick != 0f) tickCount++;

            int quads = MeshSegments + tickCount;
            var vertices = new Vector3[quads * 4];
            var vertexColors = new Color[quads * 4];
            var triangles = new int[quads * 6];

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

            // Ticks, one per evaluated bearing at the middle of its arc: out of the ring for weather
            // helm, into it for lee, longer as the helm grows. A reading that does not depend on
            // telling the colors apart. Each starts on the band's center line, so it grows out of the
            // band with no seam, in the band's own color at that bearing, and is wound like the
            // band's quads, which the fallback material's back-face culling already accepts.
            if (tickCount > 0)
            {
                int quad = MeshSegments;
                for (int i = 0; i < ticks.Length; i++)
                {
                    float tick = ticks[i];
                    if (tick == 0f) continue;

                    float degrees = 360f * (i + 0.5f) / ticks.Length;
                    float a = degrees * Mathf.Deg2Rad;
                    Vector3 d = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                    Vector3 along = new Vector3(-Mathf.Cos(a), 0f, -Mathf.Sin(a)) * (band * TickWidth * 0.5f);

                    float length = band * (0.5f + Mathf.Lerp(TickShortest, TickLongest, Mathf.Abs(tick)));
                    float near = tick > 0f ? radius : radius - length;
                    float far = tick > 0f ? radius + length : radius;

                    int v = quad * 4;
                    vertices[v + 0] = d * near - along;
                    vertices[v + 1] = d * far - along;
                    vertices[v + 2] = d * far + along;
                    vertices[v + 3] = d * near + along;

                    Color c = colors[i];
                    c.a = 1f;
                    for (int k = 0; k < 4; k++) vertexColors[v + k] = c;

                    int t = quad * 6;
                    triangles[t + 0] = v + 0;
                    triangles[t + 1] = v + 1;
                    triangles[t + 2] = v + 2;
                    triangles[t + 3] = v + 0;
                    triangles[t + 4] = v + 2;
                    triangles[t + 5] = v + 3;
                    quad++;
                }
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
