using System;
using BepInEx.Configuration;
using SailwindRigBalance.Core;
using SailwindRigBalance.Shipyard;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// The shipyard gizmo: three world-space markers, read the way KSP's CoM/CoL/CoT are read.
    ///
    ///   magenta  centre of effort      where the sail plan's turning force acts
    ///   blue     centre of resistance  where the hull's lateral drag pushes back
    ///   yellow   centre of mass        the boat's own balance point
    ///
    /// Yellow is read straight off the rigidbody, so it is whatever the game itself is using. Its
    /// base value is BoatKeel.centerOfMass, a per-hull authored point (BoatKeel.cs:7), and
    /// BoatMass.cs:116 maintains it as that plus an offset fed by loose cargo and by the player.
    /// It is often below the keel, which is a deliberate self-righting trick, not an error.
    ///
    /// It matters for two things the other two markers cannot show. Unity takes torque about the
    /// centre of mass, and the sail force is applied at the windcenter (Sail.cs:468), so the height
    /// of magenta above yellow is the heeling lever. And it moves when the hull's own loadout does,
    /// which is a real shipyard choice wherever a part carries a BoatCustomKeel (BoatCustomKeel.cs:11).
    ///
    /// The balance answer is the fore-and-aft offset between magenta and blue, and nothing else. Magenta
    /// aft of blue is weather helm, magenta forward is lee helm. A coloured link between the two
    /// carries that verdict so it reads from any camera angle without a number on screen.
    ///
    /// The marker and the link colour are the same statement, which took work to make true. Magenta
    /// is not simply where the sails average out to. It is drawn on the centreline at the lever arm
    /// the rig's yaw moment about the hull origin works out to, so the fore-and-aft run of the elbow
    /// IS the balance figure rather than merely correlating with it
    /// (RigModel.CombineCentreOfEffort). Its height is still the sails' own centroid, because that
    /// is the heeling lever against yellow and a separate reading.
    ///
    /// Without that the two halves came from different quantities and could point opposite ways on
    /// a square rigger, where the courses carry the drive and the headsails the side force from
    /// opposite ends of the boat: magenta plainly forward of blue while the link between them was
    /// painted for weather helm.
    ///
    /// Four decisions carry the whole file:
    ///
    ///  - Marker positions are held in the BOAT'S local space and converted to world every frame.
    ///    The shipyard lifts the boat clear of the water after admitting it, so a position sampled
    ///    once at evaluation time is left behind in mid-air within a second of entering.
    ///
    ///  - Markers are camera-facing quads carrying a pre-shaded ball image, not mesh spheres. An
    ///    unlit mesh sphere is a flat disc by construction: with no lighting term there is nothing
    ///    to vary the colour across it. Baking the shading into the image keeps the unlit
    ///    requirement and still reads as a ball.
    ///
    ///  - The shading is baked, so the markers hold their colour and their form at midnight exactly
    ///    as at noon. Scene lighting never touches them.
    ///
    ///  - Markers are drawn by a second camera that clears depth only, so they show THROUGH the
    ///    hull. Two of the three sit inside the boat; without this the user simply sees nothing.
    ///
    /// The three marker objects are created once per shipyard session and only ever moved.
    /// Rebuilding them was what produced a screenful of spheres: applying a customization destroys
    /// and re-instantiates every sail, Unity defers Object.Destroy to end of frame, and the
    /// collision sweep calls UpdateOrder repeatedly. A fixed pool of three cannot express that bug.
    ///
    /// Nothing is parented to a Sail or to the boat: Sail.UpdateInstallPosition toggles the sail
    /// GameObject off and on again on every add and every move.
    /// </summary>
    public class ShipyardGizmo : MonoBehaviour
    {
        /// <summary>
        /// Close hauled by default. Balance is judged where it actually bites; a run has no side
        /// force to balance and would report every rig as neutral. Configurable because 45 degrees
        /// is a judgement about where balance matters, not a fact.
        /// </summary>
        /// <summary>
        /// The angle everything is evaluated at. Starts from config and is then owned by the wind
        /// arrow: dragging it round the ring is the same act as choosing a point of sail, so there
        /// is no second source of truth to keep in step.
        /// </summary>
        private float WindAngleDegrees
        {
            get
            {
                if (_ring.Exists) return _ring.WindAngle;
                return Config.Settings.WindAngle != null ? Config.Settings.WindAngle.Value : 45f;
            }
        }

        /// <summary>
        /// Seconds to wait after the last change event before recomputing. Not a performance tweak:
        /// it lets the destroyed sails of a customization actually go away before Mast.sails is
        /// enumerated, and collapses the shipyard's repeated UpdateOrder calls into one pass.
        /// </summary>
        private const float RebuildDelay = 0.25f;

        /// <summary>
        /// Offset below which a rig reads as balanced rather than one-handed, scaled to the hull.
        /// Held as a field because it depends on the boat, and set on every rebuild.
        /// </summary>
        private float _balancedMetres = 0.3f;

        private const int Effort = 0, Resistance = 1, Mass = 2, MarkerCount = 3;

        private static readonly Color EffortColour = new Color(1.00f, 0.25f, 0.90f);
        private static readonly Color ResistanceColour = new Color(0.25f, 0.60f, 1.00f);
        private static readonly Color MassColour = new Color(1.00f, 0.92f, 0.20f);
        private static readonly Color UnreliableColour = new Color(1.00f, 0.15f, 0.15f);

        // Colour follows danger, not direction.
        //
        // Lee helm is the state to avoid: let go of the wheel and she bears away, the sails fill
        // harder and she accelerates downwind. Weather helm is the safe residual: let go and she
        // rounds up into the wind and stops. So lee helm is red, and weather helm is amber, meaning
        // safe but costing you, because holding any standing helm bleeds speed continuously
        // (Rudder.cs:57-62 applies 2% of the rudder torque as a backwards force).
        //
        // The first version had weather helm red, which signalled the safe direction as the
        // dangerous one.
        internal static readonly Color WeatherHelmColour = new Color(1.00f, 0.68f, 0.15f);
        internal static readonly Color LeeHelmColour = new Color(1.00f, 0.20f, 0.15f);
        internal static readonly Color BalancedColour = new Color(0.35f, 1.00f, 0.45f);

        private readonly GameObject[] _markers = new GameObject[MarkerCount];
        private readonly Material[] _materials = new Material[MarkerCount];
        private readonly Texture2D[] _textures = new Texture2D[MarkerCount];
        private readonly Color[] _painted = new Color[MarkerCount];
        private readonly Vector3[] _local = new Vector3[MarkerCount];
        /// <summary>
        /// Mirrors the three visibility config entries so the array indexing stays simple. Written
        /// back whenever a button is clicked, so a hidden marker stays hidden next session.
        /// </summary>
        private readonly bool[] _visible = { true, true, true };

        private static ConfigEntry<bool> VisibilityEntry(int index)
        {
            if (index == Effort) return Config.Settings.ShowEffort;
            if (index == Resistance) return Config.Settings.ShowResistance;
            return Config.Settings.ShowMass;
        }

        private GameObject _root;
        private LineRenderer _link;
        private Material _linkMaterial;
        private Texture2D _linkTexture;

        /// <summary>
        /// The ring's own material, which is never tinted.
        ///
        /// The ring used to be handed _linkMaterial. That material's _Color is overwritten with the
        /// current helm verdict on every recompute, and Sprites/Default multiplies _Color into the
        /// vertex colour, so all 24 of the ring's independently computed verdicts came out
        /// multiplied by the verdict at whatever single angle the wind arrow happened to point at.
        /// A lee-helm reading turned the whole ring red, a balanced one turned it green, and the
        /// grey no-verdict guard dimmed everything to 55 percent. The ring was reporting the arrow.
        /// </summary>
        private Material _ringMaterial;

        // The reef track: where the combined centre of effort travels as the selected sail goes
        // between fully set and fully furled. Two endpoints describe every setting in between.
        private LineRenderer _track;
        private Material _trackMaterial;
        private Vector3 _trackFrom, _trackTo;   // boat-local
        private bool _trackValid;
        private Sail _trackSail;        // owned by Recompute
        private Sail _polledSelection;  // owned by the Update poll, never by Recompute

        // The shipyard lerps a newly admitted boat into its cradle over about two seconds
        // (Shipyard.cs:435), and AdmitShip's postfix fires at the start of that, so the first
        // reading was taken with the boat an eighth of the way there and nothing re-read it when it
        // arrived. Boat pose matters: the wind-height multiplier is taken per sail off world Y, and
        // the hull length is measured in the boat's own axes. Watching the transform go still covers
        // the discharge move and any other mod that repositions the boat as well.
        private Vector3 _lastBoatPosition;
        private Quaternion _lastBoatRotation;
        private bool _settleArmed;
        private float _stillSince;
        private const float SettleSeconds = 0.15f;

        private readonly BalanceRing _ring = new BalanceRing();

        private Transform _boat;
        private Camera _overlay, _source;
        private int _layer = -1;
        private Shader _unlit;
        private bool _shaderResolved;

        private bool _valid;
        private bool _unreliable;
        /// <summary>
        /// True when the model has no verdict to give at this angle, either because the sails are
        /// barely drawing or because there is no side force to balance. Decided by the same test the
        /// ring uses (BalanceRing.IsDead), so the two cannot contradict each other.
        /// </summary>
        private bool _helmDead;
        private bool _ringNeedsRebuild = true;
        private float _helmMetres;      // positive = weather helm, in metres of CE offset
        private string _lastSummary;    // so an unchanged verdict is not logged every rebuild

        private float _rebuildAt = -1f;

        private GUIStyle _circleStyle, _tipStyle, _panelStyle, _panelInnerStyle;
        private Texture2D _circleTex;
        private Texture2D _panelTex;
        private Texture2D _paperTex;
        private Color _paperTint = Color.white;
        private Rect _paperUv = new Rect(0f, 0f, 1f, 1f);
        private bool _styleAdopted;


        // Sized to the buttons and nothing more: 8 padding, then 24 wide buttons separated by 3 of
        // margin. Any slack here shows up as dead background beside the last button.
        private const float PanelWidth = 8f + 3f + 24f + 3f + 24f + 3f + 24f + 3f + 8f;
        private const float PanelHeight = 8f + 3f + 24f + 3f + 8f;
        private const float PanelMargin = 20f;

        /// <summary>How long a button must be hovered before its name appears.</summary>
        private const float HoverDelay = 0.66f;

        private Rect _panel;
        private string _hoverTip;      // what is hovered right now, this Repaint
        private string _hoverTracked;  // what the delay is being timed against
        private float _hoverSince;

        // ------------------------------------------------------------------ lifecycle

        private void Update()
        {
            if (!ShipyardHooks.InShipyard)
            {
                if (_root != null) Teardown();
                _rebuildAt = -1f;
                return;
            }

            WatchForSettle();
            Shipyard.UnrollWatch.Tick();

            if (ShipyardHooks.ConsumeDirty())
            {
                _rebuildAt = Time.unscaledTime + RebuildDelay;
                _ringNeedsRebuild = true;
            }

            // Selecting a different sail changes which reef track is meaningful, and the shipyard
            // gives no event for it, so it is polled. Cheap: one accessor, no allocation.
            //
            // This is also the frame's single reading of the selection: OnGUI reads the latched
            // field rather than calling the accessor again. IMGUI needs the same sequence of
            // layout-emitting calls in its Layout and Repaint passes, and the shipyard's own raycast
            // picking can null or set the selection between the two, which made the panel emit three
            // controls in one pass and four in the other.
            //
            // The comparison is against a field owned by THIS poll, not against _trackSail, which
            // Recompute owns. Comparing against _trackSail deadlocked the whole gizmo: the poll
            // pushed the rebuild deadline forward every frame, so Recompute never ran, so
            // _trackSail never caught up, so the poll fired again. Selecting a sail froze every
            // marker until it was deselected, which included every furl made while it was selected.
            Sail selection = SelectedSail();
            if (selection != _polledSelection)
            {
                _polledSelection = selection;
                if (_valid) _rebuildAt = Time.unscaledTime + RebuildDelay;
            }

            // Tested against the same sentinel LateUpdate tests, >= 0 here and < 0 there. With a
            // strict > 0 here, a deadline of exactly zero would have satisfied neither: Update would
            // never have fired it and LateUpdate would never have been allowed to replace it, so the
            // gizmo would have latched dead for the session with nothing in the log.
            if (_rebuildAt >= 0f && Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = -1f;
                Recompute();
            }
        }

        /// <summary>
        /// Marks the rig dirty once the boat has stopped moving.
        ///
        /// Shipyard.AdmitShip starts a coroutine that lerps the hull into its cradle over roughly two
        /// seconds (Shipyard.cs:274, :435) and returns immediately, so the postfix that opens a
        /// session fires at the very start of that move and the 0.25 s rebuild delay lands about an
        /// eighth of the way in. MoveShip's tail calls only ShipyardUI.UpdateMastButtons
        /// (Shipyard.cs:445-448), never UpdateOrder or LoadData, so nothing this mod patches fires
        /// when the boat arrives.
        ///
        /// That mattered because two inputs move with the hull. Every sail's force is scaled by
        /// GetWindHeightMult of its own windcenter's world Y (Sail.cs:411), and the curve saturates,
        /// so a rig hanging lower is not merely scaled down, it is reweighted low sails against high
        /// ones. And the hull length is measured along the boat's own axis at whatever yaw it has
        /// reached. A player who walked in, read the gizmo and walked out got an answer computed for
        /// a boat in mid-air, and not the same answer twice, because it depended on the frame rate
        /// during that quarter second.
        /// </summary>
        private void WatchForSettle()
        {
            GameObject boat = ShipyardHooks.CurrentBoat;
            if (boat == null)
            {
                _settleArmed = false;
                return;
            }

            Transform t = boat.transform;
            bool moved = (t.position - _lastBoatPosition).sqrMagnitude > 1e-6f
                || Quaternion.Angle(t.rotation, _lastBoatRotation) > 0.01f;
            _lastBoatPosition = t.position;
            _lastBoatRotation = t.rotation;

            if (moved)
            {
                _settleArmed = true;
                _stillSince = Time.unscaledTime;
                return;
            }

            // Armed only by a move, so a boat that has been sitting still cannot keep re-triggering.
            if (!_settleArmed || Time.unscaledTime - _stillSince < SettleSeconds) return;

            _settleArmed = false;
            ShipyardHooks.MarkDirty();
        }

        /// <summary>
        /// Runs after everything else has moved for the frame, so the markers never trail the boat
        /// or the orbit camera by a frame. Three jobs: follow the boat, face the camera, hold a
        /// constant on-screen size.
        /// </summary>
        private void LateUpdate()
        {
            if (_root == null || !_valid) return;

            if (_boat == null)
            {
                // The boat went away without a discharge hook firing. Stop drawing rather than
                // leave three markers hanging in the air.
                SetValid(false);
                return;
            }

            Camera source = ResolveSourceCamera();
            if (source == null)
            {
                if (_overlay != null) _overlay.enabled = false;
                return;
            }

            if (_overlay != null)
            {
                Transform t = source.transform;
                _overlay.transform.SetPositionAndRotation(t.position, t.rotation);
                _overlay.fieldOfView = source.fieldOfView;
                _overlay.nearClipPlane = source.nearClipPlane;
                _overlay.farClipPlane = source.farClipPlane;
                _overlay.orthographic = source.orthographic;
                _overlay.orthographicSize = source.orthographicSize;
                _overlay.rect = source.rect;
                _overlay.depth = source.depth + 1f;
                _overlay.enabled = true;
            }

            Vector3 eye = source.transform.position;
            Quaternion facing = source.transform.rotation;

            for (int i = 0; i < _markers.Length; i++)
            {
                if (_markers[i] == null || !_markers[i].activeSelf) continue;

                Vector3 world = _boat.TransformPoint(_local[i]);
                Transform t = _markers[i].transform;
                t.position = world;
                t.rotation = facing;                        // billboard
                t.localScale = Vector3.one * MarkerSize(eye, world);
            }

            if (_link != null && _link.enabled)
            {
                // An elbow, not a straight line. A sail plan's centre of effort sits many metres
                // above the hull, so a direct line is almost vertical and the fore-and-aft offset,
                // which is the entire answer, shows up as an imperceptible lean. Dropping straight
                // down to the hull's height first turns that offset into the horizontal run of the
                // elbow, at true scale, readable from any angle.
                Vector3 effort = _local[Effort];
                Vector3 resistance = _local[Resistance];
                Vector3 bend = new Vector3(effort.x, resistance.y, effort.z);

                Vector3 a = _boat.TransformPoint(effort);
                Vector3 b = _boat.TransformPoint(bend);
                Vector3 c = _boat.TransformPoint(resistance);
                _link.SetPosition(0, a);
                _link.SetPosition(1, b);
                _link.SetPosition(2, c);

                float width = MarkerSize(eye, b) * 0.14f;
                _link.startWidth = width;
                _link.endWidth = width;
            }

            _ring.Follow(_boat);
            _ring.HandleDrag(source, _boat);

            // Dragging the arrow recomputes without the usual settle delay: the reason to drag is
            // to watch the markers move, and a quarter second of lag would destroy that.
            //
            // Only when nothing else is already waiting, though. Writing the deadline
            // unconditionally overwrote a pending rig-change settle with "now", so a customization
            // applied while the arrow was being held was evaluated with zero delay, against sails
            // Unity had not finished destroying yet. That is the whole reason RebuildDelay exists.
            if (_ring.ConsumeAngleChanged() && _rebuildAt < 0f) _rebuildAt = Time.unscaledTime;

            if (_track != null && _track.enabled)
            {
                Vector3 from = _boat.TransformPoint(_trackFrom);
                Vector3 to = _boat.TransformPoint(_trackTo);
                _track.SetPosition(0, from);
                _track.SetPosition(1, to);

                // Thinner than the helm link: this is a what-if, not the current state.
                float width = MarkerSize(eye, (from + to) * 0.5f) * 0.07f;
                _track.startWidth = width;
                _track.endWidth = width;
            }
        }

        /// <summary>
        /// Size proportional to distance, which holds a roughly constant size on screen. A fixed
        /// world size is a pinhead on a brig when zoomed out and swallows a dinghy when zoomed in.
        /// </summary>
        private static float MarkerSize(Vector3 eye, Vector3 point)
        {
            float scale = Config.Settings.MarkerScale != null ? Config.Settings.MarkerScale.Value : 1f;
            return Mathf.Clamp(Vector3.Distance(eye, point) * 0.030f, 0.30f, 3.0f) * scale;
        }

        /// <summary>
        /// Reads the stored visibility once per shipyard session. Done here rather than in Awake so
        /// that editing the config file between visits takes effect without a restart.
        /// </summary>
        private void LoadVisibility()
        {
            for (int i = 0; i < _visible.Length; i++)
            {
                ConfigEntry<bool> entry = VisibilityEntry(i);
                if (entry != null) _visible[i] = entry.Value;
            }
        }

        private void OnDestroy()
        {
            Teardown();
            if (_circleTex != null) Destroy(_circleTex);
            if (_panelTex != null) Destroy(_panelTex);
        }

        // ------------------------------------------------------------------ evaluation

        private void Recompute()
        {
            GameObject boat = ShipyardHooks.CurrentBoat;
            if (boat == null || !RigModel.Available)
            {
                if (!RigModel.Available)
                    Plugin.Log.LogWarning("[Gizmo] the game's sail force fields did not resolve; markers disabled.");
                SetValid(false);
                return;
            }

            RigBalance balance;
            if (!RigModel.TryEvaluate(boat, WindAngleDegrees, out balance))
            {
                SetValid(false);
                return;
            }

            Vector3 centreOfMass = ReadCentreOfMass(boat, balance.HullOrigin);

            // The offset that IS the answer, in metres, along the boat's own fore-aft axis. Computed
            // once, by the model, and read from there by the marker colour, the ring and the reef
            // track alike; it used to be derived separately in all three, which is three chances for
            // the sign convention to drift apart.
            _helmMetres = balance.HelmMetres;
            _unreliable = balance.AnySailHasFrozenCe;

            // Scaled to the boat. A flat threshold in metres is a tight tolerance on a brig and a
            // meaningless one on a dinghy, so it necessarily misreports one of them.
            float fraction = Config.Settings.BalancedThreshold != null
                ? Config.Settings.BalancedThreshold.Value
                : 0.02f;
            _balancedMetres = Mathf.Clamp(balance.HullLength * fraction, 0.05f, 3f);

            EnsureVisuals();
            EnsureFurlButton();

            // Held in the boat's frame, because the shipyard lifts the boat after admitting it and
            // keeps moving it while an order is being fitted.
            _boat = boat.transform;
            _local[Effort] = _boat.InverseTransformPoint(balance.CombinedCentreOfEffort);
            _local[Resistance] = _boat.InverseTransformPoint(balance.HullOrigin);
            _local[Mass] = _boat.InverseTransformPoint(centreOfMass);

            // The ring is a sweep of every wind angle, so its colours do not depend on where the
            // arrow points. Rebuilding it while the arrow is being dragged was doing thirteen full
            // rig evaluations per frame on top of the two the markers need, which is what dropped
            // the frame rate by half. It is rebuilt when the RIG changes, not when the wind moves.
            //
            // It runs BEFORE the marker colours, because the sweep is what establishes how much
            // output this rig is capable of, and that is the yardstick the "no verdict" test uses.
            bool wantRing = Config.Settings.ShowRing == null || Config.Settings.ShowRing.Value;
            if (wantRing && (_ringNeedsRebuild || !_ring.Exists))
            {
                // Seed the arrow from config the first time, then leave it alone: after that the
                // arrow is where the angle lives.
                if (!_ring.Exists && Config.Settings.WindAngle != null)
                    _ring.SetWindAngle(Config.Settings.WindAngle.Value);

                _ring.Rebuild(boat, _boat, balance, _balancedMetres,
                    _ringMaterial, _layer, _root == null ? null : _root.transform);
                _ringNeedsRebuild = false;
            }

            _helmDead = BalanceRing.IsDead(balance, _ring.MaxOutput);

            Repaint(Effort, _unreliable ? UnreliableColour : EffortColour);
            UpdateLinkColour();
            UpdateTrack(boat, balance);

            SetValid(true);

            LogBalance(balance);
        }

        /// <summary>
        /// One line when the answer changes, and the per-sail detail only for whoever asked for it.
        ///
        /// This used to be unconditional Info logging, which was written for a rare debounced event
        /// and then inherited by the wind arrow: dragging it recomputes every frame, so a seven-sail
        /// brig at 60 fps wrote about 480 formatted lines a second straight to disk through BepInEx's
        /// listener, for as long as the arrow was held.
        ///
        /// Nothing at Info level is written WHILE the arrow is held. Deduplicating on the formatted
        /// line is not enough on its own, because the line carries the wind angle and the offset and
        /// both of those move on essentially every frame of a real drag: that turns a thousand lines
        /// a second into sixty, not into none. The state when the player lets go is the one worth
        /// recording, and it still is.
        /// </summary>
        private void LogBalance(RigBalance balance)
        {
            string verdict = _helmDead ? "no verdict here, the sails are not working"
                : (Mathf.Abs(_helmMetres) < _balancedMetres ? "balanced"
                : (_helmMetres > 0f ? "weather helm" : "lee helm"));

            string summary = $"[Gizmo] {balance.Sails.Count} sail(s); wind {balance.WindAngle:F0} deg off the bow; "
                + $"CE offset {_helmMetres:F2} m ({verdict}, band {_balancedMetres:F2} m, hull {balance.HullLength:F1} m)"
                + $"{(_unreliable ? "; a sail's CE does not track its size" : "")}";

            if (summary != _lastSummary && !_ring.Dragging)
            {
                _lastSummary = summary;
                Plugin.Log.LogInfo(summary);
            }

            // The raw quantities behind that one line, for a pasted support log: when a verdict
            // looks wrong, whether the yaw or the side force moved is the first thing to know.
            //
            // Suppressed during a drag like the summary above, and for a structural reason rather
            // than a tidiness one. Debug is off in BepInEx's disk listener by DEFAULT, but that is a
            // user-editable setting, so anyone who enabled it for another mod would get the original
            // per-frame flood straight back. The guard also skips building the strings, which
            // LogDebug's object parameter would otherwise format on every frame of a drag whether
            // anything consumed them or not.
            if (_ring.Dragging) return;

            Plugin.Log.LogDebug($"[Gizmo]   yaw={balance.YawMoment:F0} Nm "
                + $"(side {balance.YawFromSide:F0} / thrust {balance.YawFromThrust:F0}) "
                + $"fwd={balance.TotalForwardForce:F0} N side={balance.TotalSideForce:F0} N "
                + $"output={balance.TotalOutput:F0} N (rig best {_ring.MaxOutput:F0} N)");

            // Per sail, so a furl that does not move the balance can be told apart from a furl that
            // never reached the model. Note Sail.cs:415 floors the multiplier at
            // Debugger.debugYardMult (0.015), so a furled sail keeps 1.5% of its force as bare-yard
            // windage rather than dropping to nothing.
            foreach (SailContribution c in balance.Sails)
            {
                Plugin.Log.LogDebug($"[Gizmo]   {c.Name}: cat={c.Category} upwindEff={c.UpwindEfficiency:F3} "
                    + $"unroll={c.Unroll:F2} fwd={c.ForwardForce:F0} side={c.SideForce:F0} "
                    + $"lever={c.LeverFromOrigin:F2} m height={c.HeightAboveWaterline:F1} m "
                    + $"yaw={c.YawMoment:F0}");
            }
        }

        /// <summary>
        /// Says why the reef track is not being drawn. Without this an absent track is
        /// indistinguishable from the feature being switched off or from no sail being selected,
        /// and there is no other feedback at all. Suppressed during an arrow drag for the same
        /// reason the rest of the logging is.
        /// </summary>
        private void LogTrackRefusal(string reason)
        {
            if (_ring.Dragging) return;
            Plugin.Log.LogDebug("[Gizmo] no reef track: " + reason + ".");
        }

        /// <summary>
        /// The centre of mass, taken from BoatKeel rather than from the rigidbody.
        ///
        /// This distinction is the whole reason the marker works. A keel option moves the centre of
        /// mass by writing BoatKeel.centerOfMass in OnEnable, never the rigidbody directly: vanilla
        /// does it in BoatCustomKeel.cs:11, Shipyard Expansion does it in its own SE_Keel.cs:23, and
        /// Drydock in KeelWeight.cs:24. The only thing that copies that field onto the rigidbody is
        /// BoatMass.UpdateMass (BoatMass.cs:116), and its caller is gated at BoatMass.cs:25 on
        /// GameState.playing and GameState.lastBoat == this transform. GameState.lastBoat is set on
        /// embark (PlayerEmbarkTriggerNew.cs:77) and never cleared, so that gate is open only for
        /// the boat you sailed in on. Judge a boat you have not bought yet, which is what this mod
        /// is for, and the rigidbody value is frozen at whatever BoatKeel.Start left there
        /// (BoatKeel.cs:16), no matter which keel you pick.
        ///
        /// Reading the source field instead is live in every case. It also drops the cargo and
        /// crew-position offset BoatMass folds in, which is right here: this is a marker for
        /// judging a hull's design, not its current loading.
        /// </summary>
        private static Vector3 ReadCentreOfMass(GameObject boat, Vector3 fallback)
        {
            var body = boat.GetComponent<Rigidbody>();
            Vector3 fromBody = body != null ? body.worldCenterOfMass : fallback;

            BoatKeel keel;
            try
            {
                keel = boat.GetComponent<BoatKeel>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] BoatKeel is not the shape this build expects: " + e.Message);
                return fromBody;
            }

            if (keel == null || body == null) return fromBody;

            // Same space the rigidbody uses, so this matches Unity's own worldCenterOfMass maths.
            Vector3 fromKeel = body.position + body.rotation * keel.centerOfMass;

            float drift = Vector3.Distance(fromKeel, fromBody);
            if (drift > 0.05f)
            {
                Plugin.Log.LogInfo($"[Gizmo] centre of mass from BoatKeel {keel.centerOfMass}, "
                    + $"rigidbody differs by {drift:F2} m (BoatMass is not maintaining this boat, "
                    + "or cargo is aboard). Using BoatKeel.");
            }

            return fromKeel;
        }

        private void SetValid(bool valid)
        {
            _valid = valid;
            for (int i = 0; i < _markers.Length; i++)
            {
                if (_markers[i] != null) _markers[i].SetActive(valid && _visible[i]);
            }
            if (_link != null) _link.enabled = valid && _visible[Effort] && _visible[Resistance];
            if (_track != null) _track.enabled = valid && _trackValid && _visible[Effort];
            _ring.SetVisible(valid && (Config.Settings.ShowRing == null || Config.Settings.ShowRing.Value));
            if (_overlay != null && !valid) _overlay.enabled = false;
        }

        private void UpdateLinkColour()
        {
            Color colour = _helmDead ? BalanceRing.NoVerdictColour
                : (Mathf.Abs(_helmMetres) < _balancedMetres ? BalancedColour
                : (_helmMetres > 0f ? WeatherHelmColour : LeeHelmColour));
            if (_link == null) return;

            // LineRenderer's own colours carry the verdict. The material's _Color is left at the
            // white MakeMaterial set it to, because the shader multiplies the two: tinting both
            // squared the colour, so the drawn line never matched the swatch on the panel button.
            _link.startColor = colour;
            _link.endColor = colour;
            TintFallbackOnly(_linkMaterial, colour);
        }

        // ------------------------------------------------------------------ visuals

        /// <summary>
        /// Attempts to clone the shipyard's furl-all button, a handful of times and then never
        /// again. Retried because the shipyard's own UI is built as the boat is admitted and the
        /// button may not exist on the first rebuild; capped because without Shipyard Expansion
        /// there is nothing to find, and a scene-wide search on every rebuild forever would be a
        /// pointless cost for the many players who do not run it.
        /// </summary>
        private void EnsureFurlButton()
        {
            if (Shipyard.FurlSailButton.Exists || _furlAttempts >= MaxFurlAttempts) return;

            _furlAttempts++;
            if (Shipyard.FurlSailButton.TryCreate()) return;

            if (_furlAttempts == MaxFurlAttempts)
            {
                Plugin.Log.LogInfo("[Furl] no furl-all button found to sit beneath after "
                    + $"{MaxFurlAttempts} tries; using the panel button instead.");
            }
        }

        private int _furlAttempts;
        private const int MaxFurlAttempts = 5;

        private void EnsureVisuals()
        {
            if (_root != null) return;

            LoadVisibility();
            _root = new GameObject("RigBalanceGizmo");
            _layer = ResolveMarkerLayer();

            MakeMarker(Effort, "CentreOfEffort", EffortColour);
            MakeMarker(Resistance, "CentreOfLateralResistance", ResistanceColour);
            MakeMarker(Mass, "CentreOfMass", MassColour);

            MakeLink();
            MakeTrack();
            MakeRingMaterial();
            MakeOverlayCamera();
        }

        private void MakeMarker(int index, string name, Color colour)
        {
            try
            {
                // A quad, not a sphere. See the class comment: an unlit sphere has no shading term
                // and renders as a flat disc, so the ball is drawn into the image instead.
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = name;

                var collider = go.GetComponent<Collider>();
                if (collider != null) Destroy(collider);

                go.layer = _layer;
                go.transform.SetParent(_root.transform, false);
                go.SetActive(false);

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    _textures[index] = MakeBallTexture(colour);
                    _painted[index] = colour;
                    _materials[index] = MakeMaterial(_textures[index]);
                    renderer.material = _materials[index];
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                }

                _markers[index] = go;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Gizmo] marker '{name}' failed: {e.Message}");
                _markers[index] = null;
            }
        }

        /// <summary>
        /// Recolours a marker in place. The colour is baked into the image rather than applied as a
        /// tint, so this regenerates it, and only when the colour actually changed.
        /// </summary>
        private void Repaint(int index, Color colour)
        {
            if (_markers[index] == null) return;
            if (_painted[index] == colour) return;

            Texture2D replacement = MakeBallTexture(colour);
            if (_materials[index] != null) _materials[index].mainTexture = replacement;
            if (_materials[index] != null && _materials[index].HasProperty("_EmissionMap"))
                _materials[index].SetTexture("_EmissionMap", replacement);

            if (_textures[index] != null) Destroy(_textures[index]);
            _textures[index] = replacement;
            _painted[index] = colour;
        }

        private void MakeLink()
        {
            try
            {
                var go = new GameObject("HelmLink");
                go.layer = _layer;
                go.transform.SetParent(_root.transform, false);

                _link = go.AddComponent<LineRenderer>();
                _link.useWorldSpace = true;
                _link.positionCount = 3;     // centre of effort, the bend below it, then the hull
                _link.numCapVertices = 4;
                _link.numCornerVertices = 4;
                _link.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _link.receiveShadows = false;
                _link.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                _link.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                _linkTexture = MakeSolidTexture();
                _linkMaterial = MakeMaterial(_linkTexture);
                _link.material = _linkMaterial;
                _link.enabled = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] helm link failed: " + e.Message);
                _link = null;
            }
        }

        private void MakeTrack()
        {
            try
            {
                var go = new GameObject("ReefTrack");
                go.layer = _layer;
                go.transform.SetParent(_root.transform, false);

                _track = go.AddComponent<LineRenderer>();
                _track.useWorldSpace = true;
                _track.positionCount = 2;
                _track.numCapVertices = 4;
                _track.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _track.receiveShadows = false;
                _track.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                _track.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                _trackMaterial = MakeMaterial(_linkTexture);
                _track.material = _trackMaterial;
                _track.enabled = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] reef track failed: " + e.Message);
                _track = null;
            }
        }

        /// <summary>
        /// The ring's material. Separate from the track's so that a failure in either cannot leave
        /// the other without one, and never passed to TintFallbackOnly: see _ringMaterial.
        /// </summary>
        private void MakeRingMaterial()
        {
            try
            {
                _ringMaterial = MakeMaterial(_linkTexture);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] ring material failed: " + e.Message);
                _ringMaterial = null;
            }
        }

        /// <summary>
        /// Works out where the combined centre of effort would end up if the selected sail were
        /// taken to the opposite extreme of its furl state, and colours the track by the verdict at
        /// that destination: amber if furling this sail would leave weather helm, red for lee helm,
        /// green for balanced.
        ///
        /// Two evaluations, and the line between them is an approximation, not the path. The centre
        /// is now drawn at the lever arm -YawMoment/TotalSideForce, and sweeping one sail's unroll
        /// moves numerator and denominator independently, so the true path is a ratio of two affine
        /// functions rather than a weighted average of fixed points. Over a normal sweep that curve
        /// is gentle and a straight segment between exact endpoints reads correctly.
        ///
        /// It is refused outright in the two cases where it would not. If either end has no helm
        /// reading the endpoints are not even the same KIND of point, one being a lever arm and the
        /// other the sails' own centroid, so a line joining them means nothing. And if the net side
        /// force changes sign across the sweep the real path runs off through infinity somewhere in
        /// the middle, which a straight line would quietly draw straight through: that is a genuine
        /// case on a square rigger with backed sails, the same rig the HelmMetres note calls out.
        /// Both refusals write a debug line, because a track that is simply absent looks identical
        /// to the feature being turned off.
        /// </summary>
        private void UpdateTrack(GameObject boat, RigBalance current)
        {
            _trackValid = false;
            _trackSail = _polledSelection;

            bool wanted = Config.Settings.ShowReefTrack == null || Config.Settings.ShowReefTrack.Value;
            if (!wanted || _trackSail == null || _boat == null) return;

            float destinationUnroll = _trackSail.currentUnroll > 0.5f ? 0f : 1f;

            RigBalance other;
            if (!RigModel.TryEvaluate(boat, WindAngleDegrees, out other, _trackSail, destinationUnroll,
                    current.HullLength))
                return;

            // Both ends must be the same KIND of point before a line between them says anything.
            // Without a meaningful helm reading the centre of effort is drawn as the sails' own
            // centroid, up in the rig and off the centreline, rather than as a lever arm on it, so
            // joining one of each draws a confident segment across two incomparable quantities.
            //
            // Note this is HelmMeaningful and not the wider IsDead. IsDead also covers "the sails
            // are barely drawing at this angle", and both ends of that are still lever arms and
            // still perfectly comparable: the honest answer there is to draw the track in the
            // no-verdict grey, which VerdictFor does below, not to make it disappear. Gating the
            // geometry on IsDead made the track vanish whenever furling the selected sail dropped
            // the rig under a tenth of its best output, with nothing on screen to distinguish that
            // from the feature being switched off.
            if (!current.HelmMeaningful || !other.HelmMeaningful)
            {
                LogTrackRefusal("one end has no helm reading, so the two ends are not the same "
                    + "kind of point");
                return;
            }

            // A sign change in the net side force puts a pole in the middle of the swept path, so
            // the two endpoints say nothing about what happens between them. Safe against
            // Mathf.Sign's +1 at exactly zero only because the gate above already guarantees both
            // magnitudes exceed 0.01 N: do not reorder these two.
            if (Mathf.Sign(current.TotalSideForce) != Mathf.Sign(other.TotalSideForce))
            {
                LogTrackRefusal("the net side force changes sign across the sweep");
                return;
            }

            _trackFrom = _boat.InverseTransformPoint(current.CombinedCentreOfEffort);
            _trackTo = _boat.InverseTransformPoint(other.CombinedCentreOfEffort);

            // Grey when the destination carries no verdict. The track used to be the only one of
            // the three readouts that turned no-verdict into a positive one, painting green because
            // a forced zero lever sits inside the balanced band.
            Color colour = BalanceRing.VerdictFor(other, _ring.MaxOutput, _balancedMetres);

            if (_track != null)
            {
                _track.startColor = colour;
                _track.endColor = colour;
                TintFallbackOnly(_trackMaterial, colour);
            }

            _trackValid = true;
        }

        /// <summary>
        /// A camera that clears depth and draws only the marker layer, so the markers sit on top of
        /// the hull instead of inside it. It copies the live camera every frame rather than being
        /// parented to it, because the shipyard swaps cameras and a parented object would be
        /// orphaned or dragged along with the wrong one.
        /// </summary>
        private void MakeOverlayCamera()
        {
            if (Config.Settings.DrawThroughHull != null && !Config.Settings.DrawThroughHull.Value)
            {
                // Everything goes back to the default layer so the shipyard camera still renders it;
                // the two markers inside the hull will simply be occluded.
                //
                // The whole hierarchy, not a hand-written list. Naming the markers and the link
                // individually left the reef track behind on the private layer, and Camera.main's
                // culling mask has every bit above 23 clear, so with no overlay camera created on
                // this branch nothing rendered it at all: the feature vanished silently in the exact
                // configuration the mod advertises as the safe fallback. The ring escaped only by
                // accident, being built later from the already-rewritten field.
                _layer = 0;
                foreach (Transform t in _root.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.layer = 0;
                }
                Plugin.Log.LogInfo("[Gizmo] DrawThroughHull is off; markers inside the hull will be hidden by it.");
                return;
            }

            if (_layer < 0)
            {
                Plugin.Log.LogWarning("[Gizmo] every layer on this build is named, so no private "
                    + "layer was free. Markers will draw on the default layer and the hull will "
                    + "hide the two inside it.");
                return;
            }

            try
            {
                var go = new GameObject("RigBalanceOverlayCamera");
                go.transform.SetParent(_root.transform, false);

                _overlay = go.AddComponent<Camera>();
                _overlay.clearFlags = CameraClearFlags.Depth;
                _overlay.cullingMask = 1 << _layer;
                _overlay.useOcclusionCulling = false;
                _overlay.allowHDR = false;
                _overlay.allowMSAA = false;
                _overlay.enabled = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] overlay camera failed: " + e.Message);
                _overlay = null;
            }
        }

        /// <summary>
        /// An unnamed layer is one the project never assigned, so claiming it cannot collide with
        /// the game's own culling masks, physics matrix, or any other mod's layer use.
        /// </summary>
        private static int ResolveMarkerLayer()
        {
            for (int i = 31; i >= 8; i--)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            }
            return -1;
        }

        private Camera ResolveSourceCamera()
        {
            if (_source != null && _source.enabled && _source.gameObject.activeInHierarchy) return _source;

            _source = Camera.main;
            if (_source == null)
            {
                Camera best = null;
                foreach (Camera candidate in Camera.allCameras)
                {
                    if (candidate == _overlay) continue;
                    if (best == null || candidate.depth > best.depth) best = candidate;
                }
                _source = best;
            }
            return _source;
        }

        private Material MakeMaterial(Texture2D texture)
        {
            if (!_shaderResolved)
            {
                _shaderResolved = true;
                _unlit = FindUnlitShader();
                if (_unlit != null)
                {
                    Plugin.Log.LogInfo("[Gizmo] marker shader: " + _unlit.name);
                }
                else
                {
                    Plugin.Log.LogWarning("[Gizmo] no unlit shader in this build; falling back to "
                        + "emissive Standard. Standard ignores vertex colour, so the balance ring "
                        + "will draw plain instead of per-angle. Everything else is unaffected.");
                }
            }

            Material material;
            if (_unlit != null)
            {
                material = new Material(_unlit);

                // The colour is already in the texture and in the vertex colours, so the material's
                // own tint must be neutral or it multiplies twice.
                //
                // _Color is forced to white because white IS neutral for it on every candidate.
                // _TintColor is deliberately LEFT ALONE, which is the opposite of what this used to
                // do. White is not neutral there: Legacy Shaders/Particles/Alpha Blended computes
                // o.color = 2.0 * v.color * _TintColor, so its neutral value is the 0.5 grey it
                // ships with, and overwriting that with white doubled every colour and clipped it.
                // Amber and red both washed toward yellow-white, which is the one distinction the
                // whole readout exists to make, and the grey no-verdict colour lost the alpha that
                // makes it read as dimmed. A shader's own default is by definition its no-tint
                // value, so the right move is not to touch it.
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            }
            else
            {
                // No unlit shader is present in this build. Standard in cutout mode with a black
                // albedo and the image in emission self-lights to the same result: it reads
                // identically at midnight and at noon, and the cutout keeps the round silhouette.
                material = new Material(Shader.Find("Standard"));
                material.SetFloat("_Mode", 1f);
                material.SetFloat("_Cutoff", 0.4f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.renderQueue = 2450;
                material.SetColor("_Color", Color.black);
                material.SetColor("_EmissionColor", Color.white);
                material.SetTexture("_EmissionMap", texture);
            }

            material.mainTexture = texture;
            return material;
        }

        /// <summary>
        /// Colours a line on the emissive-Standard fallback path ONLY, and does nothing on the
        /// normal one.
        ///
        /// Every candidate in FindUnlitShader multiplies vertex colour, so where one of them
        /// resolved the LineRenderer's own start and end colours already carry the verdict and
        /// writing the material as well multiplies the colour by itself. Standard ignores vertex
        /// colour entirely, so on that path the material is the only place a colour can live.
        ///
        /// This is per renderer, which is why it is safe for the link and the track and NOT for the
        /// ring: the ring is one mesh carrying two dozen different verdicts, and a material can only
        /// hold one. On the fallback path the ring is drawn plain, which is stated in the log.
        /// </summary>
        private void TintFallbackOnly(Material material, Color colour)
        {
            if (material == null || _unlit != null) return;
            material.SetColor("_EmissionColor", colour);
        }

        /// <summary>
        /// Searched by name and in order, because a Unity build only contains the shaders something
        /// in it actually references. Every candidate is unlit, textured and alpha blended, which
        /// is what a round marker on a square quad needs, AND every one of them multiplies vertex
        /// colour, which the link, the track and the ring all depend on. Which one exists is a
        /// property of this build, not something to assume, so the resolved name is logged.
        /// </summary>
        private static Shader FindUnlitShader()
        {
            string[] candidates =
            {
                "Sprites/Default",
                "UI/Default",
                "Particles/Standard Unlit",
                "Legacy Shaders/Particles/Alpha Blended",
                "Mobile/Particles/Alpha Blended",
            };

            // Unlit/Transparent is deliberately NOT a candidate, though it fits every other
            // requirement. It has no vertex colour input at all, so the helm link, the reef track
            // and the balance ring would all draw plain white through it while the code believed it
            // had a working shader and stayed silent. Falling through to the emissive Standard path
            // below is worse looking but honest, and it says so in the log.

            foreach (string name in candidates)
            {
                Shader shader = Shader.Find(name);
                if (shader != null) return shader;
            }
            return null;
        }

        private void Teardown()
        {
            Shipyard.FurlSailButton.Destroy();
            Shipyard.UnrollWatch.Clear();

            // The ring first, because it holds _ringMaterial as its sharedMaterial.
            _ring.Destroy();

            _furlAttempts = 0;
            _valid = false;
            _source = null;
            _overlay = null;
            _link = null;
            _track = null;
            _trackValid = false;
            _trackSail = null;
            _polledSelection = null;
            _lastSummary = null;
            _settleArmed = false;
            _boat = null;

            // Materials are destroyed, not merely dropped. Unity only cleans up a material it
            // instantiated itself through the renderer.material getter; these were constructed here
            // and assigned in, so they are this object's to destroy, and destroying _root does not
            // touch them. Teardown runs on every shipyard exit and EnsureVisuals builds a fresh set
            // on the next visit, so leaking them meant five orphaned native objects per visit,
            // reclaimed by nothing short of a scene unload.
            DestroyMaterial(ref _linkMaterial);
            DestroyMaterial(ref _trackMaterial);
            DestroyMaterial(ref _ringMaterial);

            for (int i = 0; i < _markers.Length; i++)
            {
                _markers[i] = null;
                if (_materials[i] != null) Destroy(_materials[i]);
                _materials[i] = null;
                if (_textures[i] != null) Destroy(_textures[i]);
                _textures[i] = null;
                _painted[i] = default(Color);
            }

            if (_linkTexture != null)
            {
                Destroy(_linkTexture);
                _linkTexture = null;
            }

            if (_root != null)
            {
                Destroy(_root);
                _root = null;
            }
        }

        private void DestroyMaterial(ref Material material)
        {
            if (material != null) Destroy(material);
            material = null;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>
        /// Paints a lit sphere into a square image: a Lambert term from a fixed key light up and to
        /// the left, an ambient floor so the dark side stays legible, a small specular highlight,
        /// and a rim lift so the silhouette holds against a bright sky. The alpha channel is the
        /// circle, feathered by one pixel.
        ///
        /// This is what makes an unlit marker look like a ball. The shading cannot come from the
        /// scene, because a marker that dims at dusk or takes a tint from a lantern is a
        /// colour-coded readout that lies.
        /// </summary>
        private static Texture2D MakeBallTexture(Color tint)
        {
            const int Size = 128;
            const float Radius = Size * 0.5f;

            Vector3 key = new Vector3(-0.45f, 0.55f, 0.70f).normalized;
            Vector3 eye = new Vector3(0f, 0f, 1f);

            var tex = new Texture2D(Size, Size, TextureFormat.ARGB32, false);
            var pixels = new Color32[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x + 0.5f - Radius) / Radius;
                    float dy = (y + 0.5f - Radius) / Radius;
                    float d2 = dx * dx + dy * dy;

                    if (d2 >= 1f)
                    {
                        pixels[y * Size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    // The visible hemisphere's surface normal at this pixel.
                    Vector3 normal = new Vector3(dx, dy, Mathf.Sqrt(1f - d2));

                    float lambert = Mathf.Max(0f, Vector3.Dot(normal, key));
                    float shade = 0.34f + 0.66f * lambert;

                    // Blinn-Phong highlight, tight enough to read as a wet ball rather than a blob.
                    Vector3 half = (key + eye).normalized;
                    float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(normal, half)), 28f) * 0.55f;

                    // A lift at the edge keeps the outline visible against a bright sky.
                    float rim = Mathf.Pow(1f - normal.z, 3f) * 0.30f;

                    float r = Mathf.Clamp01(tint.r * (shade + rim) + spec);
                    float g = Mathf.Clamp01(tint.g * (shade + rim) + spec);
                    float b = Mathf.Clamp01(tint.b * (shade + rim) + spec);

                    // One pixel of feathering so the silhouette is not jagged.
                    float alpha = Mathf.Clamp01((1f - Mathf.Sqrt(d2)) * Radius);

                    pixels[y * Size + x] = new Color(r, g, b, alpha);
                }
            }

            tex.SetPixels32(pixels);
            tex.wrapMode = TextureWrapMode.Clamp;   // clamp, or the feathered edge wraps and bleeds
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        /// <summary>Opaque white, for the link line, which takes its colour from vertex colours.</summary>
        private static Texture2D MakeSolidTexture()
        {
            var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            tex.SetPixels32(new[]
            {
                new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
                new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255),
            });
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        // ------------------------------------------------------------------ UI

        /// <summary>
        /// Three round buttons and nothing else. The buttons carry the marker colours, so the strip
        /// doubles as the legend; hovering one names it. No readout, by design: the gizmo is the
        /// output, and a number on screen just invites reading the number instead of the rig.
        ///
        /// It is fixed in the top right corner and cannot be dragged. Nothing else in the shipyard
        /// moves, so a panel that did read as a stray piece of another program. Its background and
        /// font are taken from the shipyard's own buttons at runtime rather than imitated, for the
        /// same reason the furl button is a clone: matching the game's paper look by copying it is
        /// exact, and matching it by eye never is.
        /// </summary>
        private void OnGUI()
        {
            if (!ShipyardHooks.InShipyard || !_valid) return;
            if (Config.Settings.ShowPanel != null && !Config.Settings.ShowPanel.Value) return;
            EnsureStyles();
            AdoptGameStyle();

            // The furl button only exists while a sail is selected, so the panel has to grow and
            // shrink with it or the fourth button spills past the background.
            //
            // From the field Update latched, never a fresh accessor call. IMGUI requires the same
            // sequence of layout-emitting calls in a frame's Layout and Repaint passes, and the
            // shipyard's own picking runs in Update and can change the selection between the two.
            // When it did, DrawPanel emitted three controls in one pass and four in the other, and
            // Unity threw "Getting control N's position in a group with only M controls" every frame
            // until it settled.
            float width = PanelWidth
                + (!Shipyard.FurlSailButton.Exists && _polledSelection != null ? 27f : 0f);
            _panel = new Rect(Screen.width - width - PanelMargin, PanelMargin,
                width, PanelHeightFor(width));

            _hoverTip = null;
            DrawPaper(_panel);
            GUILayout.BeginArea(_panel, _panelInnerStyle);
            DrawPanel(0);
            GUILayout.EndArea();

            // Hover tracking is only updated on Repaint, because GUI.tooltip is populated while the
            // hovered control is drawn and reads empty during Layout. Updating on every event would
            // reset the timer several times a frame and the delay would never elapse.
            if (Event.current.type == EventType.Repaint)
            {
                if (_hoverTip != _hoverTracked)
                {
                    _hoverTracked = _hoverTip;
                    _hoverSince = Time.unscaledTime;
                }
            }

            bool hoverReady = !string.IsNullOrEmpty(_hoverTracked)
                && Time.unscaledTime - _hoverSince >= HoverDelay;

            // The one case where silence would be dishonest: a sail whose centre of effort does not
            // follow its own size makes the magenta marker wrong, not merely imprecise. That one is
            // not a hover hint, so it shows immediately.
            string note = _unreliable
                ? "A sail's centre of effort does not follow its size. The magenta marker is unreliable."
                : (hoverReady ? _hoverTracked : null);
            if (string.IsNullOrEmpty(note)) return;

            const float NoteWidth = 230f;
            var content = new GUIContent(note);
            float noteHeight = _tipStyle.CalcHeight(content, NoteWidth);
            // Right edge aligned with the panel's, so the two read as one control wherever it sits.
            var rect = new Rect(_panel.xMax - NoteWidth, _panel.yMax + 6f, NoteWidth, noteHeight);
            DrawPaper(rect);
            GUI.Label(rect, content, _tipStyle);
        }

        private void DrawPanel(int id)
        {
            // Centred with flexible space on all four sides rather than by trusting PanelWidth to
            // be exactly right. The width constant is derived from an assumed IMGUI box model, and
            // any error in it showed up as the buttons hugging the left edge with dead space on the
            // right. Flexible space splits whatever slack actually exists evenly, so the row is
            // centred whether the constant is perfect or a few pixels out.
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Toggle(Effort, _unreliable ? UnreliableColour : EffortColour, "Centre of effort");
            Toggle(Resistance, ResistanceColour, "Centre of lateral resistance");
            Toggle(Mass, MassColour, "Centre of mass");
            // Only a fallback. The real furl control is a clone of the shipyard's own furl-all
            // button, sitting directly beneath it; this appears only when there was no such button
            // to clone, which is the normal case without Shipyard Expansion installed.
            if (!Shipyard.FurlSailButton.Exists) FurlToggle();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();

            // Captured here rather than read afterwards, because GUI.tooltip is only populated
            // while the hovered control is being drawn.
            if (!string.IsNullOrEmpty(GUI.tooltip)) _hoverTip = GUI.tooltip;
        }

        /// <summary>
        /// Borrows the shipyard's own parchment texture and lettering, once, so the panel is made
        /// of the same material as everything around it instead of being a grey box from a
        /// different decade of UI design. Falls back silently to the default IMGUI skin, which is
        /// legible if plain, when nothing suitable is on screen.
        /// </summary>
        private void AdoptGameStyle()
        {
            if (_styleAdopted) return;
            _styleAdopted = true;

            try
            {
                foreach (GoPointerButton button in FindObjectsOfType<GoPointerButton>())
                {
                    if (button == null) continue;

                    var renderer = button.GetComponent<Renderer>();
                    var text = button.GetComponentInChildren<TextMesh>();
                    if (renderer == null || renderer.sharedMaterial == null || text == null) continue;

                    var paper = renderer.sharedMaterial.mainTexture as Texture2D;
                    if (paper == null) continue;

                    _paperTex = paper;

                    // Which part of the sheet this button actually uses. parchment_alpha is an
                    // atlas: every shipyard button maps a different sub-rectangle of it, so drawing
                    // the whole texture shows several scrolls at once and drawing an arbitrary
                    // patch shows blank paper. The mesh's own UV bounds are the exact region the
                    // game draws for a button, so copying those gives a button-shaped scroll.
                    _paperUv = UvBoundsOf(button.gameObject);
                    // The material's own tint. Without this the panel came out grey-tan while every
                    // button around it was warm peach, because the colour lives on the material and
                    // not in the texture.
                    _paperTint = renderer.sharedMaterial.HasProperty("_Color")
                        ? renderer.sharedMaterial.GetColor("_Color")
                        : Color.white;

                    // The texture itself is deliberately NOT used as a background. It is a long
                    // scroll, and stretching it into a short wide strip reads as a rug. The panel
                    // draws its own rounded parchment in the same palette instead. What is worth
                    // borrowing is the lettering and the ink, which cannot distort.
                    if (text.font != null)
                    {
                        _tipStyle.font = text.font;
                        _tipStyle.fontSize = 13;
                    }
                    // The shipyard's ink, so hover text reads as a label on the same paper. The
                    // background stays the generated one; swapping in the scroll texture here would
                    // reintroduce the stretching on an even smaller box.
                    _tipStyle.normal.textColor = new Color(0.20f, 0.13f, 0.08f);

                    _tipStyle.normal.background = null;   // the label draws its own paper too

                    Plugin.Log.LogInfo($"[Gizmo] adopted the shipyard's paper from '{button.name}' "
                        + $"(texture '{paper.name}' {paper.width}x{paper.height}, tint {_paperTint}, "
                        + $"font '{(text.font == null ? "none" : text.font.name)}').");
                    return;
                }

                Plugin.Log.LogInfo("[Gizmo] no shipyard button found to borrow a style from; "
                    + "using the default skin.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] could not adopt the shipyard style: " + e.Message);
            }
        }

        private void Toggle(int index, Color colour, string tooltip)
        {
            Color previous = GUI.color;
            GUI.color = _visible[index] ? colour : new Color(0.32f, 0.32f, 0.32f, 0.85f);

            bool clicked = GUILayout.Button(new GUIContent(string.Empty, tooltip), _circleStyle,
                GUILayout.Width(24f), GUILayout.Height(24f));

            GUI.color = previous;

            if (!clicked) return;

            _visible[index] = !_visible[index];
            if (_markers[index] != null) _markers[index].SetActive(_visible[index]);
            if (_link != null) _link.enabled = _visible[Effort] && _visible[Resistance];

            ConfigEntry<bool> entry = VisibilityEntry(index);
            if (entry != null) entry.Value = _visible[index];
        }

        /// <summary>
        /// Furls or sets the sail the player currently has selected in the shipyard, and nothing
        /// else. Appears only while a sail is selected.
        ///
        /// It holds NO state of its own. Whether the button reads as set or furled is decided by
        /// reading Sail.currentUnroll each time it is drawn, because that field is the state. Any
        /// remembered copy would go stale the moment Shipyard Expansion's furl-all button ran, or
        /// the moment a customization was applied, since that destroys every sail and recreates it
        /// at the currentUnroll = 1 default (Sail.cs:80). Reading live means this button and SE's
        /// stay in agreement without either one knowing the other exists.
        /// </summary>
        private void FurlToggle()
        {
            // The frame's latched selection, so this button's existence cannot change between the
            // Layout and Repaint passes of one OnGUI frame.
            Sail selected = _polledSelection;
            if (selected == null) return;

            bool set = selected.currentUnroll > 0.5f;

            Color previous = GUI.color;
            GUI.color = set ? new Color(0.95f, 0.95f, 0.90f) : new Color(0.32f, 0.32f, 0.32f, 0.85f);

            string name = string.IsNullOrEmpty(selected.sailName) ? "this sail" : selected.sailName;
            bool clicked = GUILayout.Button(
                new GUIContent(string.Empty, (set ? "Furl " : "Set ") + name),
                _circleStyle, GUILayout.Width(24f), GUILayout.Height(24f));

            GUI.color = previous;
            if (!clicked) return;

            // The reef track already showed where this would land; clicking commits to it.
            Shipyard.FurlSailButton.SetUnroll(ShipyardHooks.CurrentBoat, selected, set ? 0f : 1f);
        }

        /// <summary>
        /// The sail the player has selected, via the game's own accessor. This is how vanilla's own
        /// ObstructionOutline decides which sail it belongs to (ObstructionOutline.cs:13), so it
        /// needs no patch and no guessing.
        /// </summary>
        private static Sail SelectedSail()
        {
            try
            {
                if (GameState.currentShipyard == null) return null;
                var installer = GameState.currentShipyard.sailInstaller;
                return installer == null ? null : installer.GetCurrentSail();
            }
            catch
            {
                return null;
            }
        }

        private void EnsureStyles()
        {
            if (_circleStyle != null) return;

            _circleTex = MakeCircleTexture(28);
            // Margins fixed rather than inherited: PanelWidth is computed from these, and the
            // skin's default button margin would leave background showing past the last button.
            _circleStyle = new GUIStyle(GUI.skin.button)
            {
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(3, 3, 3, 3),
            };
            _circleStyle.normal.background = _circleTex;
            _circleStyle.hover.background = _circleTex;
            _circleStyle.active.background = _circleTex;

            // Boxed rather than a bare label so the text stays legible over the shipyard's pale
            // paper panels as well as over the night sky.
            _tipStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 12,
                wordWrap = true,
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(10, 10, 7, 7),
                border = new RectOffset(12, 12, 12, 12),
            };
            // Same paper as the panel, and AdoptGameStyle replaces the lettering and ink with the
            // shipyard's own so the label reads as part of the game rather than beside it.
            _tipStyle.normal.background = _panelTex;
            _tipStyle.normal.textColor = new Color(0.20f, 0.13f, 0.08f);

            // A plain box, drawn separately from the layout area, so the panel is fixed in place.
            // It used to be a GUI.Window purely to inherit IMGUI's dragging, which made it the one
            // thing in the shipyard that could be moved and read as foreign because of it.
            // Corner radius 10 in a 32 px tile, nine-sliced on a 12 px border so the corners keep
            // their shape at any panel size and only the flat middle stretches.
            _panelTex = MakePanelTexture(32, 10);
            _panelStyle = new GUIStyle(GUIStyle.none)
            {
                border = new RectOffset(12, 12, 12, 12),
            };
            _panelStyle.normal.background = _panelTex;
            _panelInnerStyle = new GUIStyle(GUIStyle.none) { padding = new RectOffset(8, 8, 8, 8) };
        }

        /// <summary>
        /// Draws the panel background using the shipyard's ACTUAL paper, tinted with the shipyard's
        /// actual material colour.
        ///
        /// The first attempt stretched the whole sheet across the panel and looked like a rug,
        /// because parchment_alpha is a long scroll with ragged ends and visible fibre. The fix is
        /// not to draw it differently sized but to draw a small patch from the MIDDLE of it: a few
        /// percent of a sheet of paper is nearly uniform, so scaling that patch cannot distort
        /// anything recognisable, and it is still literally the game's own texture and colour
        /// rather than an imitation of them.
        /// </summary>
        private void DrawPaper(Rect rect)
        {
            if (_paperTex == null)
            {
                GUI.Box(rect, GUIContent.none, _panelStyle);
                return;
            }

            // The colour is a constant, and deliberately so after three attempts at deriving it.
            //
            // Drawing the game's texture painted black, because parchment_alpha does not carry the
            // paper in its RGB. Generating the shape and tinting it with the material's _Color
            // painted black too, because that property is black as well. Both attempts multiplied
            // by a value that turns out to be zero, which is why "fixing" it twice changed nothing.
            // Wherever the shipyard's peach actually comes from, it is not in either place I looked,
            // and a fourth guess is worth less than a constant matched to the buttons on screen.
            //
            // The material colour is still read, but only to log it.
            Color previous = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(rect, _panelTex, ScaleMode.StretchToFill, true);
            GUI.color = previous;
        }

        /// <summary>Matched by eye to the shipyard's own buttons.</summary>
        private static readonly Color PaperColour = new Color(0.96f, 0.81f, 0.67f, 0.97f);
        private static readonly Color PaperEdgeColour = new Color(0.45f, 0.30f, 0.20f, 1.00f);

        /// <summary>
        /// The sub-rectangle of the shared sheet that a given shipyard element draws, taken from
        /// its mesh UVs. This is the whole scroll graphic for that element, so drawing it just
        /// means the same scroll at a different size.
        /// </summary>
        private static Rect UvBoundsOf(GameObject go)
        {
            var filter = go.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            Vector2[] uv = mesh != null ? mesh.uv : null;
            if (uv == null || uv.Length == 0) return new Rect(0f, 0f, 1f, 1f);

            float minU = float.MaxValue, minV = float.MaxValue;
            float maxU = float.MinValue, maxV = float.MinValue;
            foreach (Vector2 p in uv)
            {
                minU = Mathf.Min(minU, p.x);
                minV = Mathf.Min(minV, p.y);
                maxU = Mathf.Max(maxU, p.x);
                maxV = Mathf.Max(maxV, p.y);
            }

            float w = maxU - minU, h = maxV - minV;
            if (w <= 0.0001f || h <= 0.0001f) return new Rect(0f, 0f, 1f, 1f);
            return new Rect(minU, minV, w, h);
        }

        /// <summary>
        /// Panel height that keeps the borrowed scroll at its own proportions. Stretching it to an
        /// arbitrary box is what made it look like a rug; letting the artwork set the shape and
        /// centring the buttons inside it does not.
        /// </summary>
        private float PanelHeightFor(float width) => PanelHeight;

        /// <summary>
        /// A rounded parchment panel, generated. Only used when nothing could be borrowed.
        ///
        /// Borrowing the shipyard's own texture was the obvious move and it looked like a rug: that
        /// art is a long scroll, and stretching it into a short wide strip distorts every fibre in
        /// it. Nine-slicing someone else's texture without knowing its border widths is guesswork.
        /// Generating a plain rounded rectangle in the same palette cannot distort at any size, and
        /// the shipyard's lettering and ink colour are still borrowed for the hover label, so the
        /// two sit together.
        ///
        /// The circles need this behind them for a plainer reason: they are saturated colours meant
        /// to match the markers, and against an open sky they have nothing to contrast with.
        /// </summary>
        private static Texture2D MakePanelTexture(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var pixels = new Color32[size * size];

            Color paper = PaperColour;
            Color edge = PaperEdgeColour;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance outside the rounded rectangle's inner box, which is 0 anywhere in
                    // the straight-edged middle and grows only into the corners.
                    float dx = Mathf.Max(radius - x, x - (size - 1 - radius), 0f);
                    float dy = Mathf.Max(radius - y, y - (size - 1 - radius), 0f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    float alpha = Mathf.Clamp01(radius - d + 0.5f);
                    // A thin darker rim, so the panel has an edge against pale sky as well as dark.
                    float rim = Mathf.Clamp01(1.5f - Mathf.Abs(radius - d));

                    Color c = Color.Lerp(paper, edge, rim * 0.85f);
                    c.a = paper.a * alpha;
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        /// <summary>A filled white disc, tinted per button through GUI.color.</summary>
        private static Texture2D MakeCircleTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    // One pixel of feathering so the edge is not jagged.
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
