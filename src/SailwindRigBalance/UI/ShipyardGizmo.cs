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
    ///   magenta  center of effort      where the sails pull, weighted by each sail's force
    ///   blue     center of resistance  where the hull's lateral drag pushes back
    ///   yellow   center of mass        the boat's own balance point
    ///
    /// Yellow is read straight off the rigidbody, so it is whatever the game itself is using. Its
    /// base value is BoatKeel.centerOfMass, a per-hull authored point (BoatKeel.cs:7), and
    /// BoatMass.cs:116 maintains it as that plus an offset fed by loose cargo and by the player.
    /// It is often below the keel, which is a deliberate self-righting trick, not an error.
    ///
    /// It matters for two things the other two markers cannot show. Unity takes torque about the
    /// center of mass, and the sail force is applied at the windcenter (Sail.cs:468), so the height
    /// of magenta above yellow is the heeling lever. And it moves when the hull's own loadout does,
    /// which is a real shipyard choice wherever a part carries a BoatCustomKeel (BoatCustomKeel.cs:11).
    ///
    /// The balance answer is the helm bar: a colored post up from blue to the top of the hull, then a
    /// bar along the centerline whose length is the lever the rig's yaw moment about the hull origin
    /// works out to. Aft is weather helm and forward is lee helm, without a number on screen.
    ///
    /// Magenta is a separate statement, where the sails pull, and the two can point opposite ways.
    /// Yaw weights each sail's station by its side force and its offset from the centerline by its
    /// drive, and it includes the hull's windage, so a one-sail junk can show magenta forward of blue
    /// and a bar running aft. See RigBalance.HelmMeters and FINDINGS section 19.
    ///
    /// Four decisions carry the whole file:
    ///
    ///  - Marker positions are held in the BOAT'S local space and converted to world every frame.
    ///    The shipyard lifts the boat clear of the water after admitting it, so a position sampled
    ///    once at evaluation time is left behind in mid-air within a second of entering.
    ///
    ///  - Markers are camera-facing quads carrying a pre-shaded ball image, not mesh spheres. An
    ///    unlit mesh sphere is a flat disc by construction: with no lighting term there is nothing
    ///    to vary the color across it. Baking the shading into the image keeps the unlit
    ///    requirement and still reads as a ball.
    ///
    ///  - The shading is baked, so the markers hold their color and their form at midnight exactly
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
        /// The angle everything is evaluated at. Starts from the wind in port (or config, see
        /// SeedFromWorldWind) and is then owned by the wind
        /// arrow: dragging it round the ring is the same act as choosing a point of sail, so there
        /// is no second source of truth to keep in step.
        /// </summary>
        private float WindAngleDegrees
        {
            get
            {
                if (_ring.Exists) return _ring.WindAngle;
                if (_carryWind) return _carryAngle;
                return Config.Settings.WindAngle != null ? Config.Settings.WindAngle.Value : 45f;
            }
        }

        // ------------------------------------------------------------------ what the gizmo is on
        //
        // A shipyard boat, or the boat the hull showcase is looking at from the main menu. The two
        // never overlap: the showcase only runs while no game is being played.

        private static bool TargetActive => ShipyardHooks.InShipyard || HullShowcase.Active;

        private static GameObject TargetBoat => HullShowcase.Active ? HullShowcase.CurrentBoat : ShipyardHooks.CurrentBoat;

        /// <summary>The boat the visuals were built for, so a showcase step rebuilds them for the next.</summary>
        private GameObject _sessionBoat;

        /// <summary>
        /// The wind the arrow was set to on the previous showcase boat, carried to the next so a
        /// chosen wind compares hull against hull, or, in the shipyard, the wind blowing in port when
        /// the visit started. Cleared when the shipyard or the showcase closes.
        /// </summary>
        private bool _carryWind;
        private float _carryAngle, _carrySpeed;

        /// <summary>Set when a shipyard visit starts, so the first evaluation starts the arrow at the
        /// wind blowing in port. Only the start: after that the arrow is the player's.</summary>
        private bool _seedFromWorld;

        /// <summary>
        /// Starts the arrow at the wind blowing where the boat lies: the bearing it comes from against
        /// her heading, and its speed. Wind.currentWind is the way the air moves (HeelCheck reads the
        /// apparent wind as it minus the boat's velocity, BoatWind.cs:25-33), so it comes from minus that.
        /// In a dead calm there is no bearing to take, and the config values stand.
        /// </summary>
        private void SeedFromWorldWind(GameObject boat)
        {
            if (Config.Settings.StartFromLocalWind != null && !Config.Settings.StartFromLocalWind.Value) return;
            try
            {
                Vector3 wind = Wind.currentWind;
                Vector3 from = boat.transform.InverseTransformDirection(-wind);
                from.y = 0f;
                if (wind.magnitude < 0.1f || from.sqrMagnitude < 0.0001f) return;

                // The same bearing convention as the arrow's drag (BalanceRing: (-sin, 0, cos)), and the
                // same half meter per second steps its knob sets.
                float angle = Mathf.Repeat(Mathf.Atan2(-from.x, from.z) * Mathf.Rad2Deg, 360f);
                float speed = Mathf.Clamp(Mathf.Round(wind.magnitude * 2f) * 0.5f,
                    Config.RigBalanceLimits.MinWindSpeed, Config.RigBalanceLimits.MaxWindSpeed);

                _carryWind = true;
                _carryAngle = angle;
                _carrySpeed = speed;
                Plugin.Log.LogInfo($"[Gizmo] wind arrow starts at the wind in port: {angle:F0} deg, {speed:F1} m/s.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("[Gizmo] the wind in port could not be read: " + e.Message);
            }
        }

        /// <summary>
        /// The wind speed she is heeled in, m/s. Owned by the knob on the wind arrow once the ring
        /// exists, from the config before then and whenever the ring is switched off.
        /// </summary>
        private float WindSpeed
        {
            get
            {
                if (_ring.Exists) return _ring.WindSpeed;
                if (_carryWind) return _carrySpeed;
                return Config.Settings.WindSpeed != null ? Config.Settings.WindSpeed.Value : RigModel.SyntheticWindSpeed;
            }
        }

        private readonly Waterline _waterline = new Waterline();
        private Material _waterlineMaterial;
        private bool _waterlineMeasured;

        /// <summary>The attitude of the last reading, for the heel label and the log.</summary>
        private SeaAttitude _attitude;

        private static bool HeelModeled => Config.Settings.ModelHeel == null || Config.Settings.ModelHeel.Value;

        // The ring's button hides the waterline with it (user's call, 2026-11-14): one button for
        // everything drawn around the hull, rather than a sixth in the strip.
        private static bool WaterlineWanted => HeelModeled
            && (Config.Settings.ShowWaterline == null || Config.Settings.ShowWaterline.Value)
            && (Config.Settings.ShowRing == null || Config.Settings.ShowRing.Value);

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
        private float _balancedMeters = 0.3f;

        private const int Effort = 0, Resistance = 1, Mass = 2, MarkerCount = 3;

        private static readonly Color EffortColor = new Color(1.00f, 0.25f, 0.90f);
        private static readonly Color ResistanceColor = new Color(0.25f, 0.60f, 1.00f);
        private static readonly Color MassColor = new Color(1.00f, 0.92f, 0.20f);
        private static readonly Color UnreliableColor = new Color(1.00f, 0.15f, 0.15f);

        // A DIVERGING scale, cool against warm, not a severity ramp.
        //
        // Lee helm is the state to avoid: let go of the wheel and she bears away, the sails fill
        // harder and she accelerates downwind. Weather helm is the safe residual: let go and she
        // rounds up into the wind and stops. It still costs you, because holding any standing helm
        // bleeds speed continuously (Rudder.cs:57-62 applies 2% of the rudder torque as a backwards
        // force), but it is a different thing from lee helm, not a milder amount of it.
        //
        // Weather helm was amber for a while and that was wrong for a reason worth recording. Green
        // to amber to red is the traffic light, and a traffic light is a scale of ONE quantity
        // getting worse, so amber inevitably read as partway to lee helm rather than as the opposite
        // direction. Helm is signed, and zero is the good value: the correct picture is two arms
        // leaving a neutral center, which needs two hues that differ in KIND. Blue against red does
        // that and carries the safe-versus-dangerous reading for free. (The version before amber had
        // weather helm red, which signalled the safe direction as the dangerous one.)
        //
        // Lighter and more cyan than the center-of-resistance marker's blue, so the helm bar
        // arriving at that marker still reads as a separate thing from it.
        private static readonly Color StandardWeatherMid = new Color(0.25f, 0.78f, 1.00f);
        private static readonly Color StandardLeeFar = new Color(1.00f, 0.20f, 0.15f);

        // The middle stops of the ramp (BalanceRing.HelmColor). With only the two ends, a rig a
        // little outside the band was already most of the way to full blue or red, so players saw
        // three colors and no gradient. Orange sits between green and red on the lee side; on the
        // weather side the light blue above becomes the middle stop and a dark blue marks the far
        // end. The dark blue stays deeper than ResistanceColor so the bar still reads apart from it.
        private static readonly Color StandardLeeMid = new Color(1.00f, 0.60f, 0.10f);
        private static readonly Color StandardWeatherFar = new Color(0.12f, 0.22f, 0.85f);
        private static readonly Color StandardBalanced = new Color(0.35f, 1.00f, 0.45f);

        // The ColorblindPalette set (a player asked, Discord 2026-11-15). Green against orange and
        // red is the pair red-green color blindness merges: simulated (Machado 2009, full
        // severity) balanced green and the orange middle stop come out about 26 apart in CIELAB for
        // protanopia and deuteranopia, and the two lee stops 18. This keeps the blue weather arm,
        // makes balanced a near white and the lee arm orange to dark orange, which tells apart by
        // lightness as well as hue: every pair, gray included, stays at least 27 apart under
        // protanopia, deuteranopia and tritanopia alike.
        private static readonly Color ColorblindWeatherFar = new Color(0.10f, 0.25f, 0.80f);
        private static readonly Color ColorblindWeatherMid = new Color(0.40f, 0.72f, 1.00f);
        private static readonly Color ColorblindBalanced = new Color(0.96f, 0.96f, 0.92f);
        private static readonly Color ColorblindLeeMid = new Color(1.00f, 0.72f, 0.30f);
        private static readonly Color ColorblindLeeFar = new Color(0.70f, 0.28f, 0.00f);

        private static bool ColorblindWanted => Config.Settings.ColorblindPalette != null
            && Config.Settings.ColorblindPalette.Value;

        internal static Color WeatherHelmColor => ColorblindWanted ? ColorblindWeatherMid : StandardWeatherMid;
        internal static Color LeeHelmColor => ColorblindWanted ? ColorblindLeeFar : StandardLeeFar;
        internal static Color LeeHelmMidColor => ColorblindWanted ? ColorblindLeeMid : StandardLeeMid;
        internal static Color WeatherHelmFarColor => ColorblindWanted ? ColorblindWeatherFar : StandardWeatherFar;
        internal static Color BalancedColor => ColorblindWanted ? ColorblindBalanced : StandardBalanced;

        /// <summary>
        /// Bumped when the palette setting changes, so the textures painted from it (the legend's
        /// strip and the ring button) are painted again.
        /// </summary>
        internal static int PaletteVersion;

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
        private LineRenderer _barHead;
        private Material _linkMaterial;
        private Texture2D _linkTexture;

        /// <summary>
        /// The ring's own material, which is never tinted.
        ///
        /// The ring used to be handed _linkMaterial. That material's _Color is overwritten with the
        /// current helm verdict on every recompute, and Sprites/Default multiplies _Color into the
        /// vertex color, so all 24 of the ring's independently computed verdicts came out
        /// multiplied by the verdict at whatever single angle the wind arrow happened to point at.
        /// A lee-helm reading turned the whole ring red, a balanced one turned it green, and the
        /// gray no-verdict guard dimmed everything to 55 percent. The ring was reporting the arrow.
        /// </summary>
        private Material _ringMaterial;

        // The reef track: where the combined center of effort travels as the selected sail goes
        // between fully set and fully furled. Two endpoints describe every setting in between.
        private LineRenderer _track;
        private Material _trackMaterial;
        private Vector3 _trackFrom, _trackTo;   // boat-local
        private bool _trackValid;
        private Sail _trackSail;        // owned by Recompute
        private Sail _polledSelection;  // owned by the Update poll, never by Recompute

        /// <summary>
        /// The keel drift last reported, so an unchanged boat is not described every frame. Static
        /// because the method that reads it is. Cleared on teardown, so a second shipyard visit
        /// reports its own boat rather than staying quiet because the previous one happened to match.
        /// </summary>
        private static float _loggedComDrift;

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

        // Held back at the start of a visit, so it appears once, finished, instead of three times.
        // Read 2026-11-14: the first reading came 0.25 s in with the boat still sliding into the
        // cradle (lever 0.64 m), the ring's sweep finishing re-read her mid-slide (4.18 m), and the
        // settle gave the real one (1.04 m), then a second sweep re-colored it again. Now nothing is
        // computed while she moves, and nothing is drawn on the boat until the first sweep after she
        // stops has finished and the markers have been re-colored against it. The strip and legend
        // stay up meanwhile with a Calculating note (user's call, 2026-11-14: a vanishing interface
        // read as a mod that had stopped). HoldSeconds is the backstop: past
        // it everything shows whatever state it is in, so a boat that never stops twitching (another
        // mod, a future game change) cannot keep the gizmo hidden.
        private bool _holding;
        private bool _revealAfterRecompute;
        private float _holdUntil, _holdStart;
        private const float HoldSeconds = 6f;

        private void Start()
        {
            StartCoroutine(SamplePaperLoop());
        }

        private void StartHold()
        {
            _holding = true;
            _revealAfterRecompute = false;
            _holdStart = Time.unscaledTime;
            _holdUntil = _holdStart + HoldSeconds;
            ApplyHold();
        }

        private void Reveal()
        {
            _holding = false;
            _revealAfterRecompute = false;
            ApplyHold();
        }

        private void ApplyHold()
        {
            if (_root != null && _root.activeSelf == _holding) _root.SetActive(!_holding);
        }

        private readonly BalanceRing _ring = new BalanceRing();

        // ------------------------------------------------------------------ sail force arrows
        //
        // One arrow per drawing sail, at its center of effort, showing the horizontal force it makes
        // at the arrow's angle. Held in the boat's frame like the markers and placed each frame.
        private readonly System.Collections.Generic.List<LineRenderer> _forceArrows
            = new System.Collections.Generic.List<LineRenderer>();
        private readonly System.Collections.Generic.List<Vector3> _forceFrom
            = new System.Collections.Generic.List<Vector3>();
        private readonly System.Collections.Generic.List<Vector3> _forceTo
            = new System.Collections.Generic.List<Vector3>();

        /// <summary>Each drawn arrow's share of the whole rig's force, in percent.</summary>
        private readonly System.Collections.Generic.List<float> _forceShare
            = new System.Collections.Generic.List<float>();

        /// <summary>Each drawn arrow's force, in newtons at the model's reference wind.</summary>
        private readonly System.Collections.Generic.List<float> _forceNewtons
            = new System.Collections.Generic.List<float>();

        /// <summary>
        /// Below this share an arrow keeps its arrow but loses its number. A fourteen-sail junk
        /// would otherwise carry fourteen labels, and the question the numbers answer is which
        /// sails are doing the work, which the small ones are not.
        /// </summary>
        private const float ForceLabelMinPercent = 3f;

        private GUIStyle _forceLabelStyle;
        private readonly GUIContent _forceLabel = new GUIContent();
        private Material _forceMaterial;
        private static readonly Color ForceColor = new Color(1.00f, 0.62f, 0.95f, 0.95f);

        /// <summary>
        /// Shaft then head, sampled at the four points each arrow is drawn through. See PlaceForces.
        /// </summary>
        private static readonly AnimationCurve ArrowWidth = new AnimationCurve(
            new Keyframe(0f, 0.30f, 0f, 0f),
            new Keyframe(0.72f, 0.30f, 0f, 0f),
            new Keyframe(0.7201f, 1f, 0f, -3.573f),
            new Keyframe(1f, 0f, -3.573f, 0f));


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
        /// <summary>
        /// True when the sails are drawing hard but the lever is past the quarter-hull bound: a
        /// verdict of "maximally lopsided", drawn at the saturated end of the ramp rather than as
        /// the gray that means "nothing here". Sign of the direction in _rawLever.
        /// </summary>
        private bool _helmOffScale;
        private float _rawLever;        // the unbounded lever, only trusted while _helmOffScale
        private float _barLever;        // the helm bar in meters, positive aft; zero means no lean to draw
        private float _helmBarHeight;   // the bar's height above the boat root, the top of the hull
        private float _unrollSeen = float.NaN;  // what PollFurlState last read; NaN means nothing yet
        private bool _ringNeedsRebuild = true;
        private float _helmMeters;      // positive = weather helm, in meters of helm lever
        private string _lastSummary;    // so an unchanged verdict is not logged every rebuild

        private float _rebuildAt = -1f;

        private GUIStyle _circleStyle, _ringStyle, _forceStyle, _tipStyle, _panelStyle, _panelInnerStyle;
        private Texture2D _circleTex;
        private Texture2D _ringTex;
        private int _ringTexPalette;
        private Texture2D _forceTex;
        private Texture2D _panelTex;
        private Texture2D _paperTex;
        private Color _paperTint = Color.white;
        private Rect _paperUv = new Rect(0f, 0f, 1f, 1f);
        private bool _styleAdopted;


        // Sized to the buttons and nothing more: 8 padding, then 24 wide buttons separated by 3 of
        // margin. Any slack here shows up as dead background beside the last button. Five buttons:
        // the three markers, the balance ring and the sail forces.
        private const float PanelWidth = 8f + 3f + 24f + 3f + 24f + 3f + 24f + 3f + 24f + 3f + 24f + 3f + 8f;
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
            // Eases toward the last sample, so a change of light shifts the panels instead of
            // snapping them.
            _paper = Color.Lerp(_paper, _paperTarget, 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime));

            if (!TargetActive)
            {
                if (_root != null) Teardown();
                _rebuildAt = -1f;
                _sessionBoat = null;
                _carryWind = false;
                _seedFromWorld = false;
                return;
            }

            // A showcase step puts the gizmo on another boat. Everything is rebuilt for it, as for a
            // fresh shipyard visit, with the wind the arrow was set to carried across.
            GameObject target = TargetBoat;
            if (target != _sessionBoat)
            {
                if (HullShowcase.Active && _root != null)
                {
                    if (_ring.Exists)
                    {
                        _carryWind = true;
                        _carryAngle = _ring.WindAngle;
                        _carrySpeed = _ring.WindSpeed;
                    }
                    Teardown();
                }
                _sessionBoat = target;
                if (target != null) StartHold();
                if (!HullShowcase.Active && target != null) _seedFromWorld = true;
                if (HullShowcase.Active && target != null)
                {
                    _rebuildAt = Time.unscaledTime + RebuildDelay;
                    _ringNeedsRebuild = true;
                    ShipyardHooks.NoteReason("showcase hull");
                }
            }

            WatchForSettle();
            Shipyard.UnrollWatch.Tick();
            if (!HullShowcase.Active) EnsureFurlButton();

            if (ShipyardHooks.ConsumeDirty())
            {
                _rebuildAt = Time.unscaledTime + RebuildDelay;
                _ringNeedsRebuild = true;
            }

            // In the showcase every sail is judged set whatever its furl state, so there is nothing to
            // poll for.
            if (!HullShowcase.Active) PollFurlState();

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
                if (_valid)
                {
                    _rebuildAt = Time.unscaledTime + RebuildDelay;
                    ShipyardHooks.NoteReason("selection changed");
                }
            }

            PollFurlKey(selection);

            // Tested against the same sentinel LateUpdate tests, >= 0 here and < 0 there. With a
            // strict > 0 here, a deadline of exactly zero would have satisfied neither: Update would
            // never have fired it and LateUpdate would never have been allowed to replace it, so the
            // gizmo would have latched dead for the session with nothing in the log.
            // While held, a boat still moving is not read: the settle marks the rig dirty when she
            // stops, which schedules the reading that counts. Not pushed back, only not fired, and
            // only until the backstop.
            bool backstop = _holding && Time.unscaledTime >= _holdUntil;
            bool stillMoving = _holding && _settleArmed && !backstop;
            if (_rebuildAt >= 0f && Time.unscaledTime >= _rebuildAt && !stillMoving)
            {
                _rebuildAt = -1f;

                // What asked for this, named. Taken here rather than where the dirty flag is consumed
                // because the rebuild is debounced: one rebuild absorbs a whole drag's worth of flags,
                // and reporting them at the flag would be a line per frame.
                LogRebuild(ShipyardHooks.TakeReasons());

                Recompute();
            }

            if (backstop)
            {
                Plugin.Log.LogInfo($"[Gizmo] shown after {HoldSeconds:F0} s without the boat settling and "
                    + "the ring finishing; the reading may still change.");
                Reveal();
            }
        }

        /// <summary>
        /// The furl key: the same toggle as the strip's circle and the cloned "Furl this sail"
        /// button, for the sail selected right now. Players balancing a big rig asked for it,
        /// because every sail they want to try in or out is otherwise a click on the sail and a
        /// trip to the corner of the screen and back.
        ///
        /// Skipped while an IMGUI text field has the keyboard, so typing a G into another mod's
        /// box does not furl a sail, and with ShowPanel off, which promises no interface at all. The
        /// game cannot open its pause menu in a shipyard (StartMenu.cs:129), so that needs no check.
        ///
        /// A closed window can leave the keyboard focus behind with nothing on screen to show it, so
        /// a press that is ignored for that reason says so in the log.
        ///
        /// Not KeyboardShortcut.IsDown, which refuses to fire while any other key or button is held,
        /// so a press would do nothing while the player happened to be holding something else.
        /// </summary>
        private static void PollFurlKey(Sail selection)
        {
            if (selection == null || Config.Settings.FurlSelectedSailKey == null) return;
            if (Config.Settings.ShowPanel != null && !Config.Settings.ShowPanel.Value) return;

            try
            {
                KeyboardShortcut shortcut = Config.Settings.FurlSelectedSailKey.Value;
                if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey)) return;

                foreach (KeyCode modifier in shortcut.Modifiers)
                    if (!Input.GetKey(modifier)) return;
            }
            catch (Exception e)
            {
                if (!_furlKeyFailureLogged)
                {
                    _furlKeyFailureLogged = true;
                    Plugin.Log.LogWarning("[Gizmo] the furl key could not be read: " + e.Message);
                }
                return;
            }

            if (GUIUtility.keyboardControl != 0)
            {
                Plugin.Log.LogInfo("[Gizmo] furl key ignored: another window has the keyboard (control "
                    + GUIUtility.keyboardControl + ").");
                return;
            }

            Shipyard.FurlSailButton.Toggle(selection);
        }

        private static bool _furlKeyFailureLogged;

        /// <summary>
        /// Rebuilds when any sail's furl state changes, whoever changed it.
        ///
        /// Sail.currentUnroll is a public field with no event. Shipyard Expansion's Furl Sails button
        /// writes it from a coroutine (ShipyardUnfurlButton.UnrollSails) without passing through
        /// anything this mod hooks, so the ring kept painting the rig as it was before the click, and
        /// the markers only caught up on the next unrelated rebuild. Reading the field is the one
        /// approach that does not depend on knowing every mod that writes it.
        ///
        /// Never pushes back a rebuild already pending. A field that changed every frame would then
        /// hold the deadline off forever, which froze the whole gizmo once before: see the note on
        /// the selection poll in Update. Here it only means a rebuild every RebuildDelay.
        /// </summary>
        private void PollFurlState()
        {
            float now = UnrollFingerprint(TargetBoat);
            bool changed = !float.IsNaN(_unrollSeen) && Mathf.Abs(now - _unrollSeen) > 0.005f;
            _unrollSeen = now;
            if (!changed) return;

            _ringNeedsRebuild = true;
            ShipyardHooks.NoteReason("sail furled or set");
            if (_rebuildAt < 0f) _rebuildAt = Time.unscaledTime + RebuildDelay;
        }

        /// <summary>
        /// One number that moves when any sail's unroll does. Weighted by position, so swapping one
        /// sail's furl for another's still registers.
        /// </summary>
        private static float UnrollFingerprint(GameObject boat)
        {
            var refs = boat == null ? null : boat.GetComponent<BoatRefs>();
            if (refs == null || refs.masts == null) return 0f;

            float sum = 0f;
            int index = 0;
            foreach (Mast mast in refs.masts)
            {
                if (mast == null || mast.sails == null) continue;

                foreach (GameObject sailObject in mast.sails)
                {
                    if (sailObject == null) continue;
                    var sail = sailObject.GetComponent<Sail>();
                    if (sail == null) continue;
                    sum += sail.currentUnroll * (1f + 0.37f * index++);
                }
            }

            return sum;
        }

        private string _lastRebuildReason;
        private int _repeatedRebuilds;

        /// <summary>
        /// Reports what asked for a rebuild, collapsing an unbroken run of the same answer.
        ///
        /// Some rebuilds are legitimately per frame. Dragging the wind arrow deliberately skips the
        /// settle delay, because the whole reason to drag it is to watch the markers move, so it
        /// recomputes on every frame the cursor moves and is right to. Logged plainly that produced
        /// 938 lines from one shipyard visit, of which 876 were consecutive and identical: about
        /// fifteen seconds of dragging at sixty frames a second. It read like a runaway loop and was
        /// nothing of the sort, which cost a round of diagnosis on its own.
        ///
        /// The first of a run is logged immediately, so nothing is ever hidden behind a later event,
        /// and the repeats are counted and flushed when the answer changes or the session ends.
        /// Deliberately general rather than a test for dragging: any future path that recomputes every
        /// frame gets the same treatment without anyone having to notice it first.
        /// </summary>
        private void LogRebuild(string reason)
        {
            if (reason == _lastRebuildReason)
            {
                _repeatedRebuilds++;
                return;
            }

            FlushRepeatedRebuilds();
            Plugin.Log.LogInfo("[Gizmo] rebuild: " + reason);
            _lastRebuildReason = reason;
        }

        private void FlushRepeatedRebuilds()
        {
            if (_repeatedRebuilds > 0)
            {
                Plugin.Log.LogInfo($"[Gizmo]   ...and {_repeatedRebuilds} more of the same.");
                _repeatedRebuilds = 0;
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
            GameObject boat = TargetBoat;
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
            ShipyardHooks.MarkDirty("boat settled");
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

                EnsureUiCameraAboveOverlay();
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
                // The helm bar: a post straight up from the blue marker to the top of the hull, then a
                // bar along the centerline, aft for weather helm and forward for lee helm, with an
                // arrowhead on the end. The bar's length is the lever, at true length at any zoom.
                //
                // Up at the hull's top it cannot hide behind the blue ball however short it gets, and
                // it still shows with DrawThroughHull off. The post carries the color from every
                // camera angle, including bow-on, where a fore-and-aft bar foreshortens to a point.
                // With no lean to show, balanced or no verdict, the post stands alone.
                Vector3 resistance = _local[Resistance];
                var deck = new Vector3(resistance.x, Mathf.Max(_helmBarHeight, resistance.y + 1f), resistance.z);
                float size = MarkerSize(eye, _boat.TransformPoint(deck));

                _link.positionCount = _barLever == 0f ? 2 : 3;
                _link.SetPosition(0, _boat.TransformPoint(resistance));
                _link.SetPosition(1, _boat.TransformPoint(deck));

                if (_barLever != 0f)
                {
                    // Never more than half the bar, so a short lever still shows some shaft.
                    float length = Mathf.Abs(_barLever);
                    float headLength = Mathf.Min(size * 0.7f, length * 0.5f);
                    float aft = -Mathf.Sign(_barLever);

                    Vector3 headBase = _boat.TransformPoint(deck + new Vector3(0f, 0f, aft * (length - headLength)));
                    Vector3 tip = _boat.TransformPoint(deck + new Vector3(0f, 0f, aft * length));
                    _link.SetPosition(2, headBase);

                    if (_barHead != null)
                    {
                        _barHead.SetPosition(0, headBase);
                        _barHead.SetPosition(1, tip);
                        _barHead.startWidth = size * 0.55f;
                        _barHead.endWidth = 0f;
                    }
                }

                float width = size * 0.2f;
                _link.startWidth = width;
                _link.endWidth = width;
            }

            _ring.Follow(_boat);

            // Advance an in-progress sweep by one frame's budget. The ring evaluates every bearing
            // around the boat, which measured at 189 ms in one go on a full square rigger, so it is
            // spread across frames instead of freezing the shipyard for a fifth of a second every
            // time a sail is touched. The old ring stays on screen until the new one is complete.
            _ring.Step(_boat);

            _ring.HandleDrag(source, _boat);

            // Dragging the arrow recomputes without the usual settle delay: the reason to drag is
            // to watch the markers move, and a quarter second of lag would destroy that.
            //
            // Only when nothing else is already waiting, though. Writing the deadline
            // unconditionally overwrote a pending rig-change settle with "now", so a customization
            // applied while the arrow was being held was evaluated with zero delay, against sails
            // Unity had not finished destroying yet. That is the whole reason RebuildDelay exists.
            if (_ring.ConsumeAngleChanged() && _rebuildAt < 0f)
            {
                _rebuildAt = Time.unscaledTime;
                ShipyardHooks.NoteReason("wind arrow");
            }

            // A new wind speed heels her differently at every bearing, so the ring needs a fresh
            // sweep, once, when the knob is let go. During the drag only the arrow's own bearing is
            // re-read, through the angle-changed path above, which keeps the drag smooth.
            if (_ring.ConsumeSpeedSettled())
            {
                _ringNeedsRebuild = true;
                if (_rebuildAt < 0f) _rebuildAt = Time.unscaledTime;
                ShipyardHooks.NoteReason("wind speed");
            }

            _waterline.Tick(_boat, MarkerSize(eye, _boat.position) * 0.12f, Time.unscaledDeltaTime);

            // A finished sweep establishes how much output this rig is capable of, which is what the
            // markers measure "no verdict" against. Re-color them against it. This does not restart
            // the sweep: the ring is only rebuilt when the RIG changed, and this is not that.
            if (_ring.ConsumeSweepCompleted())
            {
                // Whichever reading comes next is colored against this sweep, so it is the one to show.
                if (_holding) _revealAfterRecompute = true;
                if (_rebuildAt < 0f)
                {
                    _rebuildAt = Time.unscaledTime;
                    ShipyardHooks.NoteReason("sweep finished");
                }
            }

            PlaceForces(eye);

            if (_track != null && _track.enabled)
            {
                Vector3 from = _boat.TransformPoint(_trackFrom);
                Vector3 to = _boat.TransformPoint(_trackTo);
                _track.SetPosition(0, from);
                _track.SetPosition(1, to);

                // Thinner than the helm bar: this is a what-if, not the current state.
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
            if (_ringTex != null) Destroy(_ringTex);
            if (_forceTex != null) Destroy(_forceTex);
            if (_panelTex != null) Destroy(_panelTex);
            if (_paperRead != null) Destroy(_paperRead);
            if (_legendTex != null) Destroy(_legendTex);
        }

        // ------------------------------------------------------------------ evaluation

        private void Recompute()
        {
            GameObject boat = TargetBoat;
            if (boat == null || !RigModel.Available)
            {
                if (!RigModel.Available)
                    Plugin.Log.LogWarning("[Gizmo] the game's sail force fields did not resolve; markers disabled.");
                SetValid(false);
                return;
            }

            if (_seedFromWorld)
            {
                _seedFromWorld = false;
                if (!_ring.Exists) SeedFromWorldWind(boat);
            }

            RigBalance balance;
            if (!RigModel.TryEvaluate(boat, WindAngleDegrees, out balance, null, 1f, 0f, null, WindSpeed))
            {
                SetValid(false);
                return;
            }
            _attitude = balance.Attitude;

            Vector3 centerOfMass = ReadCenterOfMass(boat, balance.HullOrigin);

            // The lever that IS the answer, in meters. Computed once, by the model, and read from
            // there by the helm bar, the ring and the reef track alike; it used to be derived
            // separately in all three, which is three chances for the sign convention to drift apart.
            _helmMeters = balance.HelmMeters;
            _unreliable = balance.AnySailHasFrozenCe;

            // Scaled to the boat. A flat threshold in meters is a tight tolerance on a brig and a
            // meaningless one on a dinghy, so it necessarily misreports one of them.
            float fraction = Config.Settings.BalancedThreshold != null
                ? Config.Settings.BalancedThreshold.Value
                : 0.02f;
            _balancedMeters = Mathf.Clamp(balance.HullLength * fraction, 0.05f, 3f);

            EnsureVisuals();

            // Held in the boat's frame, because the shipyard lifts the boat after admitting it and
            // keeps moving it while an order is being fitted.
            _boat = boat.transform;
            _local[Effort] = _boat.InverseTransformPoint(balance.CombinedCenterOfEffort);
            _local[Resistance] = _boat.InverseTransformPoint(balance.HullOrigin);
            _local[Mass] = _boat.InverseTransformPoint(centerOfMass);

            // The ring is a sweep of every wind angle, so its colors do not depend on where the
            // arrow points. Rebuilding it while the arrow is being dragged was doing thirteen full
            // rig evaluations per frame on top of the two the markers need, which is what dropped
            // the frame rate by half. It is rebuilt when the RIG changes, not when the wind moves.
            //
            // It runs BEFORE the marker colors, because the sweep is what establishes how much
            // output this rig is capable of, and that is the yardstick the "no verdict" test uses.
            // The hull's shape for the waterline is read when the rig or the boat changes, the same
            // events that rebuild the ring, and never on an arrow drag.
            if (_ringNeedsRebuild || !_waterlineMeasured)
            {
                _waterline.Measure(boat);
                _waterlineMeasured = true;
            }
            _waterline.SetTarget(balance.Attitude, WaterlineWanted);

            bool wantRing = Config.Settings.ShowRing == null || Config.Settings.ShowRing.Value;
            if (wantRing && (_ringNeedsRebuild || !_ring.Exists))
            {
                // Seed the arrow from config the first time, then leave it alone: after that the
                // arrow is where the angle lives.
                if (!_ring.Exists && _carryWind)
                {
                    _ring.SetWindAngle(_carryAngle);
                    _ring.SetWindSpeed(_carrySpeed);
                }
                else
                {
                    if (!_ring.Exists && Config.Settings.WindAngle != null)
                        _ring.SetWindAngle(Config.Settings.WindAngle.Value);
                    if (!_ring.Exists && Config.Settings.WindSpeed != null)
                        _ring.SetWindSpeed(Config.Settings.WindSpeed.Value);
                }

                _ring.Rebuild(boat, _boat, balance, _balancedMeters,
                    _ringMaterial, _layer, _root == null ? null : _root.transform);
                _ringNeedsRebuild = false;

                // A sweep that finished before this one started was of the boat somewhere else.
                _revealAfterRecompute = false;

            }

            _helmDead = BalanceRing.IsDead(balance, _ring.MaxOutput);
            _helmOffScale = !_helmDead && balance.HelmOffScale;
            _rawLever = balance.RawLever;
            _barLever = _helmDead ? 0f
                : _helmOffScale ? Mathf.Sign(_rawLever) * balance.OffScaleLever
                : Mathf.Abs(_helmMeters) < _balancedMeters ? 0f
                : _helmMeters;
            _helmBarHeight = balance.HullTopHeight;

            Repaint(Effort, _unreliable ? UnreliableColor : EffortColor);
            UpdateLinkColor();
            UpdateTrack(boat, balance);
            UpdateForces(balance);

            SetValid(true);

            // Shown once the boat has stopped and this reading is colored against a finished sweep,
            // or straight away when there is no ring to wait for.
            if (_holding && !_settleArmed && (_revealAfterRecompute || !_ring.Exists)) Reveal();
            else ApplyHold();

            LogRestAttitudes(balance);
            LogUiSurvey();
            LogCradleAttitude();
            LogHullOffset(balance);
            LogBodyOffset(balance);
            LogBalance(balance);
        }

        /// <summary>True once a boat's rest attitudes have been reported. Cleared on teardown.</summary>
        private bool _restAttitudesLogged;

        /// <summary>The hull-versus-root offset last reported, so a stable boat logs it once.</summary>
        private float _loggedHullOffset;

        /// <summary>The rigidbody offset last reported, so a steady boat logs nothing.</summary>
        private float _loggedBodyOffset;

        /// <summary>
        /// Reports when the boat's rigidbody and its transform disagree about where the boat is.
        ///
        /// Nothing is measured from the rigidbody any more, so this changes no reading. It is here
        /// because a gap of about 1.1 m between the two, once, made a whole ring lopsided while the
        /// model still took its origin from the rigidbody, and nothing in the log said so: the only
        /// trace was every sail in the mirror audit sitting the same distance to port on both
        /// tacks. If it happens again, this names the size and the moment.
        ///
        /// A quarter meter before it speaks, with the same again of hysteresis. The shipyard's own
        /// two-second lerp into the cradle can put physics a frame behind the transform, which is
        /// a few centimeters and not news; a gap worth reporting is a lot larger than that.
        /// </summary>
        private void LogBodyOffset(RigBalance balance)
        {
            float offset = balance.BodyOffsetLocal.magnitude;
            if (Mathf.Abs(offset - _loggedBodyOffset) <= 0.25f) return;

            bool wasReported = _loggedBodyOffset > 0.25f;
            _loggedBodyOffset = offset;

            if (offset <= 0.25f)
            {
                if (wasReported)
                    Plugin.Log.LogInfo("[Gizmo] the boat's rigidbody and transform agree again.");
                return;
            }

            Plugin.Log.LogWarning($"[Gizmo] the boat's rigidbody sits ({balance.BodyOffsetLocal.x:F2}, "
                + $"{balance.BodyOffsetLocal.y:F2}, {balance.BodyOffsetLocal.z:F2}) m from its transform "
                + "(x starboard, y up, z forward). Readings are measured from the transform, where the "
                + "sail positions come from, so they are unaffected; something moved the boat outside "
                + "physics.");
        }

        /// <summary>The cradle attitude last reported, in degrees, so a settled boat logs it once.</summary>
        private float _loggedPitch, _loggedHeel;

        /// <summary>True once a cradle attitude has been reported at all. Cleared on teardown.</summary>
        private bool _cradleLogged;

        /// <summary>
        /// Reports when the visible hull and the boat root are not in the same place, and by how
        /// much, so the walk the first field report described is measurable from a log instead of
        /// from a screen recording. The reporter's own diagnosis was better than the mod's: they
        /// noticed it grew a little per oversized sail resize, and at about 400 edits in one session
        /// the accumulated offset put the ring visibly off the ship. Logged on change, a quarter
        /// meter of hysteresis, so a walking hull leaves a breadcrumb trail and a stable one costs
        /// one line.
        /// </summary>
        private void LogHullOffset(RigBalance balance)
        {
            float offset = new Vector2(balance.HullCenterLocal.x, balance.HullCenterLocal.z).magnitude;

            if (Mathf.Abs(offset - _loggedHullOffset) <= 0.25f) return;
            _loggedHullOffset = offset;

            if (offset < 0.5f)
            {
                Plugin.Log.LogInfo("[Gizmo] hull and boat root agree again.");
                return;
            }

            Plugin.Log.LogInfo($"[Gizmo] the hull's colliders sit ({balance.HullCenterLocal.x:F1}, "
                + $"{balance.HullCenterLocal.y:F1}, {balance.HullCenterLocal.z:F1}) m from the boat "
                + "root (x starboard, y up, z forward). The ring centers on the hull horizontally "
                + "and takes its height from the root; the shipyard camera follows the root and is "
                + "the game's own.");
        }

        /// <summary>
        /// Reports the attitude the cradle holds the boat at, because that is what turns a
        /// horizontal error into a vertical one and it is invisible on screen: a boat two degrees
        /// down by the head looks level in a shipyard that is itself built on a slope.
        /// Shipyard.MoveShip lerps the boat to editedShipPosition.rotation (Shipyard.cs:437-446),
        /// which is hand-placed per yard, so the same boat sits differently at every shipyard and
        /// the second field report's two players saw the ring drift in opposite directions.
        ///
        /// Logged on change, half a degree of hysteresis, so the two-second lerp on admitting a ship
        /// leaves at most a couple of lines and the last one is the attitude she settled at.
        /// </summary>
        private void LogCradleAttitude()
        {
            if (_boat == null) return;

            float pitch = Mathf.Asin(Mathf.Clamp(_boat.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float heel = Mathf.Asin(Mathf.Clamp(_boat.right.y, -1f, 1f)) * Mathf.Rad2Deg;

            if (_cradleLogged
                && Mathf.Abs(pitch - _loggedPitch) <= 0.5f
                && Mathf.Abs(heel - _loggedHeel) <= 0.5f) return;

            _cradleLogged = true;
            _loggedPitch = pitch;
            _loggedHeel = heel;

            Plugin.Log.LogInfo($"[Gizmo] the cradle holds her at {pitch:F1} deg by the head and "
                + $"{heel:F1} deg of heel (positive is bow up, starboard down).");
        }

        /// <summary>Once per game run: the survey describes scene structure, which does not change.</summary>
        private static bool _uiSurveyLogged;

        /// <summary>
        /// Measures the rendering surface the gizmo has to coexist with, because it has now been
        /// guessed at twice and one of the guesses shipped wrong. FINDINGS section 6 measured the
        /// MAST BUTTONS on layer 23 WorldUI, and the fix for the gizmo drawing over the shipyard
        /// menu generalised that to the whole menu: the overlay camera redraws WorldUI, the layer
        /// name resolves, and the parchment panels still sit under the gizmo, so the panels are not
        /// on that layer and nothing read so far says where they are.
        ///
        /// Two sweeps. Every camera in the scene, because the panels may be drawn by one this mod
        /// has never considered, and the fix may be camera order rather than layers. Then every
        /// renderer under ShipyardUI and under the main camera, the latter because a panel that
        /// holds a fixed place on screen is probably parented to the camera. Grouped by layer,
        /// shader and queue rather than listed, so the answer is a handful of lines whatever the
        /// hierarchy holds.
        /// </summary>
        private static void LogUiSurvey()
        {
            if (_uiSurveyLogged) return;
            _uiSurveyLogged = true;

            try
            {
                foreach (Camera cam in FindObjectsOfType<Camera>())
                {
                    if (cam == null || cam.name == "RigBalanceOverlayCamera") continue;
                    Plugin.Log.LogInfo($"[Survey] camera '{cam.name}' depth={cam.depth} "
                        + $"enabled={cam.enabled} clear={cam.clearFlags} mask=0x{cam.cullingMask:X8} "
                        + $"parent='{(cam.transform.parent == null ? "<root>" : cam.transform.parent.name)}'");
                }

                var groups = new System.Collections.Generic.Dictionary<string, int>();
                var examples = new System.Collections.Generic.Dictionary<string, string>();

                SurveyRenderers(ShipyardUI.instance == null ? null : ShipyardUI.instance.transform,
                    "ShipyardUI", groups, examples);
                SurveyRenderers(Camera.main == null ? null : Camera.main.transform,
                    "main camera", groups, examples);

                if (groups.Count == 0)
                {
                    Plugin.Log.LogInfo("[Survey] no renderers found under ShipyardUI or the main camera.");
                    return;
                }

                foreach (var kv in groups)
                {
                    Plugin.Log.LogInfo($"[Survey] {kv.Value} renderer(s): {kv.Key}, e.g. '{examples[kv.Key]}'");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Survey] failed: " + e.Message);
            }
        }

        private static void SurveyRenderers(Transform root, string where,
            System.Collections.Generic.Dictionary<string, int> groups,
            System.Collections.Generic.Dictionary<string, string> examples)
        {
            if (root == null) return;

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.sharedMaterial == null) continue;

                Material mat = r.sharedMaterial;
                string shader = mat.shader == null ? "<none>" : mat.shader.name;
                string key = $"under {where}, layer {r.gameObject.layer} "
                    + $"'{LayerMask.LayerToName(r.gameObject.layer)}', shader '{shader}', "
                    + $"queue {mat.renderQueue}";

                int n;
                groups.TryGetValue(key, out n);
                groups[key] = n + 1;
                if (!examples.ContainsKey(key)) examples[key] = r.gameObject.name;
            }
        }

        /// <summary>
        /// Every sail's rest attitude, once per boat, so the reach of the remaining mirror defect can
        /// be settled by measurement rather than argued about.
        ///
        /// The model poses each sail as Euler(rest.x, rest.y, trim). Unity composes Ry * Rx * Rz, and
        /// reflecting through the centerline maps Euler(x, y, z) to Euler(x, -y, -z), so a nonzero y
        /// means the mirror of a pose the search tries is a pose it never tries. That is true at every
        /// bearing, not only at the two where the audit currently notices, which is why the size of
        /// the symptom does not bound the size of the cause.
        ///
        /// A boat that reports y = 0 everywhere confines the defect to where it was seen and the ring
        /// can be released as it stands. A boat that does not means every reading carries a small
        /// error and the pose family has to be rebuilt on the hinge axis first. One line per sail,
        /// once, is the whole cost of telling those apart.
        /// </summary>
        private void LogRestAttitudes(RigBalance balance)
        {
            if (_restAttitudesLogged || balance.Sails == null || balance.Sails.Count == 0) return;
            _restAttitudesLogged = true;

            int offAxis = 0;
            foreach (SailContribution c in balance.Sails)
            {
                // Euler angles come back in [0, 360), so a rest attitude of "none" reads as 0 or as
                // 360 minus a hair. Folded to a signed offset from zero before it is judged.
                float x = Mathf.DeltaAngle(0f, c.RestEuler.x);
                float y = Mathf.DeltaAngle(0f, c.RestEuler.y);
                bool breaksMirror = Mathf.Abs(y) > 0.5f;
                if (breaksMirror) offAxis++;

                Plugin.Log.LogInfo($"[Pose] '{c.Name}': rest x={x:F2} y={y:F2} deg"
                    + (breaksMirror ? "  <- y is not zero, this sail's poses cannot mirror" : ""));
            }

            if (offAxis == 0)
            {
                Plugin.Log.LogInfo($"[Pose] {balance.Sails.Count} sail(s), every rest yaw zero: the "
                    + "pose family is closed under reflection and the remaining mirror defect is "
                    + "confined to where it was measured.");
            }
            else
            {
                Plugin.Log.LogWarning($"[Pose] {offAxis} of {balance.Sails.Count} sail(s) carry a rest "
                    + "yaw, so their poses cannot mirror at ANY bearing. The trim search needs to be "
                    + "built on the hinge axis rather than by substituting into Euler angles.");
            }
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
                : _helmOffScale ? ("off the scale, about " + _rawLever.ToString("F0") + " m of "
                    + (_rawLever > 0f ? "weather" : "lee") + " helm")
                : (_barLever == 0f ? "balanced"
                : (_barLever > 0f ? "weather helm" : "lee helm"));

            string summary = $"[Gizmo] {balance.Sails.Count} sail(s); wind {balance.WindAngle:F0} deg off the bow; "
                + $"helm lever {_helmMeters:F2} m ({verdict}, band {_balancedMeters:F2} m, hull {balance.HullLength:F1} m)"
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

            if (balance.Attitude.Valid)
            {
                Plugin.Log.LogDebug($"[Gizmo]   at {balance.WindSpeed:F1} m/s: heel {balance.Attitude.Heel:F1} deg, "
                    + $"pitch {balance.Attitude.Pitch:F1} deg, origin {balance.Attitude.OriginHeight:F2} m above "
                    + $"the sea{(balance.Attitude.Overpowered ? ", OVERPOWERED (no balance found)" : "")}; "
                    + $"sail moment ({balance.SailMoment.x:F0}, {balance.SailMoment.y:F0}, {balance.SailMoment.z:F0}) Nm "
                    + "at 10 m/s");
            }

            Plugin.Log.LogDebug($"[Gizmo]   yaw={balance.YawMoment:F0} Nm "
                + $"(side {balance.YawFromSide:F0} / thrust {balance.YawFromThrust:F0} / heel {balance.YawFromHeel:F0}) "
                + $"fwd={balance.TotalForwardForce:F0} N side={balance.TotalSideForce:F0} N "
                + $"output={balance.TotalOutput:F0} N (rig best {_ring.MaxOutput:F0} N)");

            // Per sail, so a furl that does not move the balance can be told apart from a furl that
            // never reached the model. Note Sail.cs:415 floors the multiplier at
            // Debugger.debugYardMult (0.015), so a furled sail keeps 1.5% of its force as bare-yard
            // windage rather than dropping to nothing.
            foreach (SailContribution c in balance.Sails)
            {
                Plugin.Log.LogDebug($"[Gizmo]   {c.Name}: cat={c.Category} upwindEff={c.UpwindEfficiency:F3} "
                    + $"unroll={c.Unroll:F2} shaded={c.Shading * 100f:F0}% fwd={c.ForwardForce:F0} side={c.SideForce:F0} "
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
        /// The center of mass, taken from BoatKeel rather than from the rigidbody.
        ///
        /// This distinction is the whole reason the marker works. A keel option moves the center of
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
        private static Vector3 ReadCenterOfMass(GameObject boat, Vector3 fallback)
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

            // Rigidbody.centerOfMass is local to the rigidbody's own pose, and on the boat root that
            // is the transform's pose, so this is Unity's worldCenterOfMass maths. It is taken off
            // the transform rather than the rigidbody so the yellow marker sits in the same frame as
            // the other two whenever physics and the transform are briefly out of step; see the
            // pivot note on RigModel.
            Transform frame = boat.transform;
            Vector3 fromKeel = frame.position + frame.rotation * keel.centerOfMass;

            // Reported when it CHANGES, not on every rebuild. This is a statement about the boat, and
            // the boat does not change between two rebuilds a frame apart, so logging it per rebuild
            // says nothing new 938 times: that is the count from one shipyard visit, and it buried
            // every line worth reading. A tenth of a meter is far below anything the drift would
            // move by for a real reason and far above float noise in a distance.
            float drift = Vector3.Distance(fromKeel, fromBody);
            if (drift > 0.05f)
            {
                if (Mathf.Abs(drift - _loggedComDrift) > 0.1f)
                {
                    _loggedComDrift = drift;
                    Plugin.Log.LogInfo($"[Gizmo] center of mass from BoatKeel {keel.centerOfMass}, "
                        + $"rigidbody differs by {drift:F2} m (cargo aboard, a keel BoatMass has not "
                        + "applied, or the game counting the player aboard at their map position while "
                        + "she is in the yard). The marker and the heel model use BoatKeel.");
                }
            }
            else
            {
                _loggedComDrift = 0f;
            }

            return fromKeel;
        }

        /// <summary>The bar belongs to the blue button and the reef track to the magenta one.</summary>
        private void UpdateBarAndTrackEnabled()
        {
            bool bar = _valid && _visible[Resistance];
            if (_link != null) _link.enabled = bar;
            if (_barHead != null) _barHead.enabled = bar && _barLever != 0f;
            if (_track != null) _track.enabled = _valid && _trackValid && _visible[Effort];
        }

        private void SetValid(bool valid)
        {
            _valid = valid;
            for (int i = 0; i < _markers.Length; i++)
            {
                if (_markers[i] != null) _markers[i].SetActive(valid && _visible[i]);
            }
            UpdateBarAndTrackEnabled();
            if (!valid)
                foreach (LineRenderer arrow in _forceArrows)
                    if (arrow != null) arrow.enabled = false;
            _ring.SetVisible(valid && (Config.Settings.ShowRing == null || Config.Settings.ShowRing.Value));
            if (!valid) _waterline.Hide();
            if (_overlay != null && !valid) _overlay.enabled = false;
        }

        /// <summary>
        /// Recomputes the sail force arrows from the current reading. Scaled so the sail pulling
        /// hardest gets an arrow a fifth of the hull long and every other arrow is in proportion,
        /// because what the arrow lengths are for is comparing sails with each other: which one is
        /// pulling hardest, and which way. The absolute figure is written beside each head instead;
        /// see DrawForceLabels.
        ///
        /// Horizontal only, side force along the boat's starboard axis and drive along its bow,
        /// because that is the plane the helm lives in. A sail too weak to see at this scale, under
        /// two percent of the strongest, is left out rather than drawn as a dot.
        /// </summary>
        private void UpdateForces(RigBalance balance)
        {
            _forceFrom.Clear();
            _forceTo.Clear();
            _forceShare.Clear();
            _forceNewtons.Clear();

            bool wanted = Config.Settings.ShowSailForces != null && Config.Settings.ShowSailForces.Value;
            if (!wanted || balance.Sails == null || balance.Sails.Count == 0) return;

            // Shares are of the whole rig, the sails too small to draw included, so the numbers
            // on a rig add up to a hundred whether or not every sail got an arrow.
            float strongest = 0f, total = 0f;
            foreach (SailContribution c in balance.Sails)
            {
                float magnitude = c.ForceMagnitude;
                strongest = Mathf.Max(strongest, magnitude);
                total += magnitude;
            }
            if (strongest <= 0f || total <= 0f) return;

            float scale = balance.HullLength * 0.2f / strongest;

            foreach (SailContribution c in balance.Sails)
            {
                float magnitude = c.ForceMagnitude;
                if (magnitude < strongest * 0.02f) continue;

                _forceFrom.Add(c.Arm);
                _forceTo.Add(c.Arm + new Vector3(c.SideForce, 0f, c.ForwardForce) * scale);
                _forceShare.Add(magnitude / total * 100f);
                _forceNewtons.Add(magnitude);
            }
        }

        /// <summary>
        /// Writes each arrow's share of the rig's force next to its head, as flat text on the
        /// screen.
        ///
        /// A player asked for these because the arrows are drawn in the world, so perspective
        /// distorts their lengths: one pointing at the camera looks short, one near the camera
        /// looks long, and comparing sails by eye means correcting for both. Text drawn in screen
        /// space is the same size wherever the sail is and whichever way it pulls.
        ///
        /// Written either as a force or as a share, chosen by the button's cycle. The force is in
        /// real-world kilonewtons at the model's own apparent wind, RigModel.SyntheticWindSpeed,
        /// 10 m/s (see RealNewtonsPerGameNewton), and every sail's ratio to every other is the same
        /// at any wind.
        /// The share is of all the sails' force added together, direction ignored, so a rig's
        /// numbers add up to 100. Either way the unit is printed on the label, so the mode the
        /// button was left in is never something the player has to remember.
        ///
        /// Placed a little past the head along the arrow, so the number never covers the point it
        /// labels, and skipped for any head behind the camera.
        /// </summary>
        private void DrawForceLabels()
        {
            if (_forceShare.Count == 0 || _boat == null) return;
            if (Event.current.type != EventType.Repaint) return;

            Camera camera = ResolveSourceCamera();
            if (camera == null) return;

            EnsureLabelStyle();

            bool percent = Config.Settings.SailForceUnit != null
                && Config.Settings.SailForceUnit.Value == Config.SailForceUnits.Percent;

            int count = Mathf.Min(Mathf.Min(_forceShare.Count, _forceNewtons.Count),
                Mathf.Min(_forceFrom.Count, _forceTo.Count));
            for (int i = 0; i < count; i++)
            {
                float share = _forceShare[i];
                if (share < ForceLabelMinPercent) continue;

                Vector3 local = _forceTo[i] + (_forceTo[i] - _forceFrom[i]) * 0.14f;
                Vector3 screen = camera.WorldToScreenPoint(_boat.TransformPoint(local));
                if (screen.z <= 0f) continue;

                _forceLabel.text = percent
                    ? Mathf.RoundToInt(share) + "%"
                    : Kilonewtons(_forceNewtons[i], WindSpeed);
                DrawLabelAt(screen, _forceLabel, Vector2.zero);
            }
        }

        private void EnsureLabelStyle()
        {
            if (_forceLabelStyle != null) return;

            // After AdoptGameStyle, so it inherits the shipyard's lettering and ink.
            _forceLabelStyle = new GUIStyle(_tipStyle)
            {
                fontSize = 12,
                wordWrap = false,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(5, 5, 1, 1),
            };
            _forceLabelStyle.normal.background = null;
        }

        /// <summary>A small paper label centered on a screen point, shifted by <paramref name="nudge"/> pixels.</summary>
        private void DrawLabelAt(Vector3 screen, GUIContent content, Vector2 nudge)
        {
            Vector2 size = _forceLabelStyle.CalcSize(content);
            size.x += 2f;

            var rect = new Rect(screen.x - size.x * 0.5f + nudge.x, Screen.height - screen.y - size.y * 0.5f + nudge.y,
                size.x, size.y);
            DrawPaper(rect);
            GUI.Label(rect, content, _forceLabelStyle);
        }

        private readonly GUIContent _windLabel = new GUIContent();
        private const float KnotsPerMeterPerSecond = 1.943844f;

        /// <summary>
        /// The wind speed beside the knob that sets it, and the heel beside the waterline it draws,
        /// in the same paper labels as the sail forces. Knots for the wind because that is how a
        /// sailor says it, and "kt" rather than "kn" so it cannot be misread next to "kN".
        /// </summary>
        private void DrawWindLabels()
        {
            if (_boat == null || Event.current.type != EventType.Repaint) return;

            Camera camera = ResolveSourceCamera();
            if (camera == null) return;

            EnsureLabelStyle();

            if (_ring.Visible)
            {
                Vector3 knob = camera.WorldToScreenPoint(_ring.KnobWorld);
                if (knob.z > 0f)
                {
                    _windLabel.text = Mathf.RoundToInt(WindSpeed * KnotsPerMeterPerSecond) + " kt";
                    DrawLabelAt(knob, _windLabel, new Vector2(0f, -22f));
                }
            }

            if (_waterline.Visible)
            {
                SeaAttitude shown = _waterline.Shown;
                Vector3 anchor = _boat.position + _boat.TransformDirection(_waterline.LabelAnchorLocal);
                Vector3 screen = camera.WorldToScreenPoint(anchor);
                if (screen.z > 0f)
                {
                    // A trailing plus where no balance was found: she goes over further than this.
                    _windLabel.text = Mathf.RoundToInt(Mathf.Abs(shown.Heel)) + "°"
                        + (shown.Overpowered ? "+" : "");
                    DrawLabelAt(screen, _windLabel, new Vector2(0f, 18f));
                }
            }
        }

        /// <summary>
        /// Places the force arrows. Each is a camera-facing line drawn through four points, so the
        /// width curve can step from shaft to head between the second and third: a two-point line
        /// only samples its width at its two ends and could only ever taper.
        /// </summary>
        private void PlaceForces(Vector3 eye)
        {
            for (int i = 0; i < _forceFrom.Count; i++)
            {
                LineRenderer arrow = ForceArrow(i);
                if (arrow == null) return;

                Vector3 from = _boat.TransformPoint(_forceFrom[i]);
                Vector3 to = _boat.TransformPoint(_forceTo[i]);
                Vector3 span = to - from;

                arrow.SetPosition(0, from);
                arrow.SetPosition(1, from + span * 0.72f);
                arrow.SetPosition(2, from + span * 0.7201f);
                arrow.SetPosition(3, to);
                arrow.widthMultiplier = MarkerSize(eye, (from + to) * 0.5f) * 0.35f;
                arrow.enabled = true;
            }

            for (int i = _forceFrom.Count; i < _forceArrows.Count; i++)
                if (_forceArrows[i] != null) _forceArrows[i].enabled = false;
        }

        private LineRenderer ForceArrow(int index)
        {
            while (_forceArrows.Count <= index)
            {
                if (_root == null) return null;

                try
                {
                    if (_forceMaterial == null) _forceMaterial = MakeMaterial(_linkTexture);

                    var go = new GameObject("SailForce");
                    go.layer = _layer;
                    go.transform.SetParent(_root.transform, false);

                    var line = go.AddComponent<LineRenderer>();
                    line.useWorldSpace = true;
                    line.positionCount = 4;
                    line.numCapVertices = 0;
                    line.widthCurve = ArrowWidth;
                    line.material = _forceMaterial;
                    line.startColor = ForceColor;
                    line.endColor = ForceColor;
                    line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    line.receiveShadows = false;
                    line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                    line.enabled = false;
                    _forceArrows.Add(line);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[Gizmo] sail force arrow failed: " + e.Message);
                    return null;
                }
            }

            return _forceArrows[index];
        }

        /// <summary>
        /// Cycles off, arrows with kilonewtons, arrows with percentages, off. Two settings carry
        /// it rather than one three-way setting, because ShowSailForces shipped as a plain on/off
        /// in v0.16.0 and a player who turned the arrows on should find them still on. Switching
        /// off resets the unit to kilonewtons, so the cycle always starts in the same place.
        /// </summary>
        private void ForceToggle()
        {
            ConfigEntry<bool> entry = Config.Settings.ShowSailForces;
            ConfigEntry<Config.SailForceUnits> unit = Config.Settings.SailForceUnit;
            bool on = entry != null && entry.Value;
            bool percent = unit != null && unit.Value == Config.SailForceUnits.Percent;

            Color previous = GUI.color;
            GUI.color = on ? Color.white : new Color(0.32f, 0.32f, 0.32f, 0.85f);

            string tip = !on ? "Sail forces"
                : percent ? "Sail forces, share of the rig (click to hide)"
                : "Sail forces in kN at the wind arrow's speed (click for percent)";

            bool clicked = GUILayout.Button(new GUIContent(string.Empty, tip), _forceStyle,
                GUILayout.Width(24f), GUILayout.Height(24f));

            GUI.color = previous;

            if (!clicked || entry == null) return;

            if (on && !percent && unit != null)
            {
                // kN to percent: the arrows stay, only the labels change, and OnGUI reads the
                // setting every frame, so there is nothing to rebuild.
                unit.Value = Config.SailForceUnits.Percent;
                return;
            }

            entry.Value = !on;
            if (unit != null) unit.Value = Config.SailForceUnits.Kilonewtons;

            // The arrows come from the reading, so the next one draws them. Hidden now, not then.
            if (!entry.Value)
            {
                _forceFrom.Clear();
                _forceTo.Clear();
                _forceShare.Clear();
                _forceNewtons.Clear();
                foreach (LineRenderer arrow in _forceArrows)
                    if (arrow != null) arrow.enabled = false;
            }
            else if (_rebuildAt < 0f)
            {
                // Only when nothing is already waiting: see the wind arrow's identical guard in
                // LateUpdate, which exists because overwriting a pending settle broke it once.
                _rebuildAt = Time.unscaledTime;
                ShipyardHooks.NoteReason("sail forces shown");
            }
        }

        /// <summary>
        /// A game force as real-world kilonewtons with two significant figures: "12 kN", "2.3 kN",
        /// "0.45 kN". Small boats make forces well under a kilonewton per sail, where one decimal
        /// place would write most of them as 0.1 or 0.2 and lose the comparison the number is there
        /// for.
        /// </summary>
        private static string Kilonewtons(float gameNewtons, float windSpeed)
        {
            // gameNewtons is the force at SyntheticWindSpeed. A real sail's force goes with the square
            // of the wind, so at the arrow's speed it is scaled by the square of the ratio.
            float ratio = windSpeed / RigModel.SyntheticWindSpeed;
            float kn = gameNewtons * RealNewtonsPerGameNewton * ratio * ratio / 1000f;
            if (kn >= 10f) return kn.ToString("F0") + " kN";
            if (kn >= 1f) return kn.ToString("F1") + " kN";
            return kn.ToString("F2") + " kN";
        }

        /// <summary>
        /// Converts the game's sail force into what a real sail of the same area would pull in the
        /// same wind. The game's force is wind speed x sailArea x C (Sail.cs:407-469, with
        /// debugSailAreaMult 0.02 times the hardcoded 50 coming to 1), where C is the game's own
        /// dimensionless factor for angle, sail type and height, between 0 and about 2. Real sail
        /// force is half the air density x wind speed squared x area x a force coefficient, and
        /// real coefficients sit in the same 1 to 1.5 range as C at full draw, so C stands in for
        /// it. What differs is the law: linear in the game, squared in air. At the model's one wind
        /// speed that is a single constant, half of 1.225 kg/m3 x 10 m/s, about 6.1. sailArea reads
        /// as square meters because the hulls are built near real scale. A 100 m2 course running
        /// comes out near 7 kN, which is about what a real one pulls at 19 knots.
        /// </summary>
        private const float AirDensity = 1.225f;
        private const float RealNewtonsPerGameNewton = 0.5f * AirDensity * RigModel.SyntheticWindSpeed;

        /// <summary>An arrow, pointing up and to the right, in the force arrows' own color.</summary>
        private static Texture2D MakeForceTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);

            var tail = new Vector2(size * 0.22f, size * 0.22f);
            var tip = new Vector2(size * 0.80f, size * 0.80f);
            Vector2 axis = (tip - tail).normalized;
            var normal = new Vector2(-axis.y, axis.x);
            Vector2 headBase = tip - axis * (size * 0.30f);
            float shaftHalf = size * 0.065f;
            float headHalf = size * 0.20f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float along = Vector2.Dot(p - tail, axis);
                    float off = Mathf.Abs(Vector2.Dot(p - tail, normal));
                    float headAlong = Vector2.Dot(p - headBase, axis);
                    float headLength = size * 0.30f;

                    // Distance inside the shape, in pixels, for one pixel of feathering.
                    float inside = -1f;
                    if (along >= 0f && headAlong <= 0f) inside = shaftHalf - off;
                    if (headAlong >= 0f && headAlong <= headLength)
                        inside = Mathf.Max(inside, headHalf * (1f - headAlong / headLength) - off);

                    Color color = ForceColor;
                    color.a = Mathf.Clamp01(inside + 0.5f);
                    tex.SetPixel(x, y, color);
                }
            }

            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private void UpdateLinkColor()
        {
            // The same continuous ramp the ring is painted with, so the helm bar and the arc behind
            // it cannot mean different things by the same color.
            Color color = _helmDead ? BalanceRing.NoVerdictColor
                : _helmOffScale ? (_rawLever > 0f ? WeatherHelmFarColor : LeeHelmColor)
                : BalanceRing.HelmColor(_helmMeters, _balancedMeters);
            if (_link == null) return;

            // LineRenderer's own colors carry the verdict. The material's _Color is left at the
            // white MakeMaterial set it to, because the shader multiplies the two: tinting both
            // squared the color, so the drawn line never matched the swatch on the panel button.
            _link.startColor = color;
            _link.endColor = color;
            if (_barHead != null)
            {
                _barHead.startColor = color;
                _barHead.endColor = color;
            }
            TintFallbackOnly(_linkMaterial, color);
        }

        // ------------------------------------------------------------------ visuals

        /// <summary>
        /// Attempts to clone the shipyard's furl-all button, a handful of times and then never
        /// again. Capped because without a furl-all button there is nothing to find, and a search
        /// on every attempt forever would be a pointless cost for players who do not run one.
        ///
        /// It runs from Update on its own clock, never inside the first moments after a boat is
        /// admitted, because of a field crash report: Unity died with a native access violation
        /// INSIDE the child walk of the old scene-wide button scan, on the admit-frame rebuild,
        /// in an install where three other mods build shipyard UI at that same instant. An object
        /// one of them had not finished constructing passed the managed null check with nothing
        /// behind it. A native access violation cannot be caught from managed code, so the only
        /// defense is not to go looking until the construction dust has settled, and to touch as
        /// few objects as possible when we do (see FindFurlAllButton).
        /// </summary>
        private void EnsureFurlButton()
        {
            if (Shipyard.FurlSailButton.Exists || _furlAttempts >= MaxFurlAttempts) return;
            if (Time.unscaledTime - ShipyardHooks.EnteredAt < FurlSearchDelaySeconds) return;
            if (Time.unscaledTime < _nextFurlAttemptAt) return;
            _nextFurlAttemptAt = Time.unscaledTime + FurlRetrySeconds;

            _furlAttempts++;
            if (Shipyard.FurlSailButton.TryCreate()) return;

            if (_furlAttempts == MaxFurlAttempts)
            {
                Plugin.Log.LogInfo("[Furl] no furl-all button found to sit beneath after "
                    + $"{MaxFurlAttempts} tries; using the panel button instead.");
            }
        }

        private int _furlAttempts;
        private float _nextFurlAttemptAt;
        private const int MaxFurlAttempts = 5;

        /// <summary>How long after AdmitShip before the first furl-button search may run.</summary>
        private const float FurlSearchDelaySeconds = 1.5f;

        /// <summary>Spacing between searches, so five failures span the UI still being built.</summary>
        private const float FurlRetrySeconds = 1f;

        private void EnsureVisuals()
        {
            if (_root != null) return;

            LoadVisibility();
            _root = new GameObject("RigBalanceGizmo");
            _layer = ResolveMarkerLayer();

            MakeMarker(Effort, "CenterOfEffort", EffortColor);
            MakeMarker(Resistance, "CenterOfLateralResistance", ResistanceColor);
            MakeMarker(Mass, "CenterOfMass", MassColor);

            MakeLink();
            MakeTrack();
            MakeRingMaterial();
            MakeWaterlineMaterials();
            MakeOverlayCamera();
        }

        private void MakeWaterlineMaterials()
        {
            try
            {
                _waterlineMaterial = MakeMaterial(_linkTexture);
                TintFallbackOnly(_waterlineMaterial, BalanceRing.KnobColor);
                _waterline.Attach(_root.transform, _layer, _waterlineMaterial);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] waterline failed: " + e.Message);
            }
        }

        private void MakeMarker(int index, string name, Color color)
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
                    _textures[index] = MakeBallTexture(color);
                    _painted[index] = color;
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
        /// Recolors a marker in place. The color is baked into the image rather than applied as a
        /// tint, so this regenerates it, and only when the color actually changed.
        /// </summary>
        private void Repaint(int index, Color color)
        {
            if (_markers[index] == null) return;
            if (_painted[index] == color) return;

            Texture2D replacement = MakeBallTexture(color);
            if (_materials[index] != null) _materials[index].mainTexture = replacement;
            if (_materials[index] != null && _materials[index].HasProperty("_EmissionMap"))
                _materials[index].SetTexture("_EmissionMap", replacement);

            if (_textures[index] != null) Destroy(_textures[index]);
            _textures[index] = replacement;
            _painted[index] = color;
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
                _link.positionCount = 2;     // the post, and the bar when there is a lean to show
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

                var headGo = new GameObject("HelmBarHead");
                headGo.layer = _layer;
                headGo.transform.SetParent(_root.transform, false);

                _barHead = headGo.AddComponent<LineRenderer>();
                _barHead.useWorldSpace = true;
                _barHead.positionCount = 2;
                _barHead.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _barHead.receiveShadows = false;
                _barHead.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                _barHead.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                _barHead.material = _linkMaterial;
                _barHead.enabled = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] helm bar failed: " + e.Message);
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
        /// Works out where the combined center of effort would end up if the selected sail were
        /// taken to the opposite extreme of its furl state, and colors the track by the verdict at
        /// that destination: blue if furling this sail would leave weather helm, red for lee helm,
        /// green for balanced.
        ///
        /// Two evaluations, and the line between them is the path: the center of effort is a weighted
        /// average of fixed points, and sweeping one sail's unroll changes one weight. Its color is
        /// the helm reading at the destination, which is a separate question from where the line
        /// goes, so it can read gray at the destination while the line itself stays valid.
        ///
        /// Refused only when the destination has no sails drawing at all, which leaves no center of
        /// effort to go to. That writes a debug line, because a track that is simply absent looks
        /// identical to the feature being turned off.
        /// </summary>
        private void UpdateTrack(GameObject boat, RigBalance current)
        {
            _trackValid = false;
            _trackSail = _polledSelection;

            bool wanted = Config.Settings.ShowReefTrack == null || Config.Settings.ShowReefTrack.Value;
            if (!wanted || _trackSail == null || _boat == null) return;

            float destinationUnroll = Shipyard.FurlSailButton.ToggledUnroll(_trackSail);

            RigBalance other;
            if (!RigModel.TryEvaluate(boat, WindAngleDegrees, out other, _trackSail, destinationUnroll,
                    current.HullLength, null, WindSpeed))
                return;

            if (current.Sails.Count == 0 || other.Sails.Count == 0)
            {
                LogTrackRefusal("one end has no sails drawing, so it has no center of effort");
                return;
            }

            _trackFrom = _boat.InverseTransformPoint(current.CombinedCenterOfEffort);
            _trackTo = _boat.InverseTransformPoint(other.CombinedCenterOfEffort);

            // Gray when the destination carries no verdict. The track used to be the only one of
            // the three readouts that turned no-verdict into a positive one, painting green because
            // a forced zero lever sits inside the balanced band.
            Color color = BalanceRing.VerdictFor(other, _ring.MaxOutput, _balancedMeters);

            if (_track != null)
            {
                _track.startColor = color;
                _track.endColor = color;
                TintFallbackOnly(_trackMaterial, color);
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
                // The whole hierarchy, not a hand-written list. Naming the markers and the bar
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

                // The shipyard menu has to win over the gizmo, and this camera is why it did not:
                // it clears depth and renders after the main camera, so everything only the main
                // camera drew sits underneath whatever this one draws. The entire shipyard menu is
                // world-space quads and TextMesh on the WorldUI layer, main camera only (FINDINGS
                // section 6, measured: mast buttons all on layer 23, menu layered by renderQueue
                // 3001-3002, text on ZTest-Always materials). So the ring, the arrow and the markers
                // were stamped over the parchment.
                //
                // Redrawing that layer here puts the menu back on top: within this camera's pass the
                // menu's quads outrank the gizmo by renderQueue, its text ignores depth outright,
                // and both sit nearer the camera than a marker on the boat, while on pixels the menu
                // already covered the redraw lands on the same image. A gizmo genuinely in front of a
                // panel still draws in front, which is where it genuinely is.
                int worldUi = LayerMask.NameToLayer("WorldUI");
                if (worldUi >= 0) _overlay.cullingMask |= 1 << worldUi;
                else Plugin.Log.LogWarning("[Gizmo] no WorldUI layer on this build; the markers "
                    + "will draw over the shipyard menu.");
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

        private Camera _gameUiCamera;
        private float _gameUiCameraDepth;
        private bool _gameUiCameraBumped;
        private bool _gameUiCameraSearched;

        /// <summary>
        /// Keeps the game's own interface above the gizmo, by camera order, which is the only
        /// ordering that exists between them.
        ///
        /// The whole shipyard menu, and every panel the player ever reads, is world-space geometry
        /// on layer 5, drawn by a dedicated camera literally named 'UI camera', a child of the main
        /// camera with culling mask 0x20 and clear flags Depth, at depth 0. Measured in the shipyard
        /// on 2026-08-04 by the renderer survey: 338 layer-5 renderers under ShipyardUI and the main
        /// camera against 256 on WorldUI, and the WorldUI set is only the mast buttons. The previous
        /// fix redrew WorldUI in the overlay pass and changed nothing the player could see, because
        /// the panels were never on it.
        ///
        /// The overlay camera has to render after the main camera to draw through the hull, and it
        /// sits at main + 1 while the UI camera sits level with main at 0, so the gizmo landed on
        /// top of every panel. Vanilla's own layering between those two equal-depth cameras is
        /// undefined in the asset data (FINDINGS section 6) and holds by engine accident. Nudging
        /// the UI camera one step above the overlay makes main, then gizmo, then interface explicit,
        /// which is the order the game itself intends: the UI camera clears its own depth, so it
        /// draws over whatever came before it regardless of where that came from.
        ///
        /// Restored on teardown, so outside a shipyard the scene is exactly as shipped.
        /// </summary>
        private void EnsureUiCameraAboveOverlay()
        {
            if (_gameUiCamera == null)
            {
                if (_gameUiCameraSearched) return;
                _gameUiCameraSearched = true;

                foreach (Camera cam in FindObjectsOfType<Camera>())
                {
                    if (cam != null && cam != _overlay && cam.name == "UI camera")
                    {
                        _gameUiCamera = cam;
                        break;
                    }
                }

                if (_gameUiCamera == null)
                {
                    Plugin.Log.LogWarning("[Gizmo] no 'UI camera' found; the markers will draw over "
                        + "the shipyard menu.");
                    return;
                }
            }

            if (_gameUiCamera.depth <= _overlay.depth)
            {
                if (!_gameUiCameraBumped)
                {
                    _gameUiCameraDepth = _gameUiCamera.depth;
                    _gameUiCameraBumped = true;
                }

                _gameUiCamera.depth = _overlay.depth + 1f;
            }
        }

        private Camera ResolveSourceCamera()
        {
            // The showcase draws through its own camera; Camera.main stays the menu's, drawing nothing.
            if (HullShowcase.Active && HullShowcase.ViewCamera != null) return HullShowcase.ViewCamera;

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
                        + "emissive Standard. Standard ignores vertex color, so the balance ring "
                        + "will draw plain instead of per-angle. Everything else is unaffected.");
                }
            }

            Material material;
            if (_unlit != null)
            {
                material = new Material(_unlit);

                // The color is already in the texture and in the vertex colors, so the material's
                // own tint must be neutral or it multiplies twice.
                //
                // _Color is forced to white because white IS neutral for it on every candidate.
                // _TintColor is deliberately LEFT ALONE, which is the opposite of what this used to
                // do. White is not neutral there: Legacy Shaders/Particles/Alpha Blended computes
                // o.color = 2.0 * v.color * _TintColor, so its neutral value is the 0.5 gray it
                // ships with, and overwriting that with white doubled every color and clipped it.
                // Both arms washed toward yellow-white, which is the one distinction the
                // whole readout exists to make, and the gray no-verdict color lost the alpha that
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
        /// Colors a line on the emissive-Standard fallback path ONLY, and does nothing on the
        /// normal one.
        ///
        /// Every candidate in FindUnlitShader multiplies vertex color, so where one of them
        /// resolved the LineRenderer's own start and end colors already carry the verdict and
        /// writing the material as well multiplies the color by itself. Standard ignores vertex
        /// color entirely, so on that path the material is the only place a color can live.
        ///
        /// This is per renderer, which is why it is safe for the bar and the track and NOT for the
        /// ring: the ring is one mesh carrying two dozen different verdicts, and a material can only
        /// hold one. On the fallback path the ring is drawn plain, which is stated in the log.
        /// </summary>
        private void TintFallbackOnly(Material material, Color color)
        {
            if (material == null || _unlit != null) return;
            material.SetColor("_EmissionColor", color);
        }

        /// <summary>
        /// Searched by name and in order, because a Unity build only contains the shaders something
        /// in it actually references. Every candidate is unlit, textured and alpha blended, which
        /// is what a round marker on a square quad needs, AND every one of them multiplies vertex
        /// color, which the bar, the track and the ring all depend on. Which one exists is a
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
            // requirement. It has no vertex color input at all, so the helm bar, the reef track
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
            // So the next boat describes itself rather than being silently compared to the last one.
            _loggedComDrift = 0f;

            // Whatever was still repeating when the session ended is reported rather than dropped,
            // and the next session starts against no previous answer.
            FlushRepeatedRebuilds();
            _lastRebuildReason = null;
            _unrollSeen = float.NaN;

            // The next boat reports its own rest attitudes rather than inheriting this one's.
            _restAttitudesLogged = false;
            _loggedHullOffset = 0f;
            _loggedBodyOffset = 0f;
            _cradleLogged = false;
            RigModel.ResetHullLog();
            RigModel.ResetLimitsLog();

            // The interface camera goes back to the depth it shipped with, and the next session
            // re-finds it rather than trusting a reference into a scene that may have been reloaded.
            if (_gameUiCameraBumped && _gameUiCamera != null) _gameUiCamera.depth = _gameUiCameraDepth;
            _gameUiCameraBumped = false;
            _gameUiCamera = null;
            _gameUiCameraSearched = false;

            Shipyard.FurlSailButton.Destroy();
            Shipyard.UnrollWatch.Clear();

            // The ring first, because it holds _ringMaterial as its sharedMaterial.
            _ring.Destroy();
            _waterline.Destroy();
            _waterlineMeasured = false;
            _attitude = default(SeaAttitude);
            DestroyMaterial(ref _waterlineMaterial);

            _furlAttempts = 0;
            _nextFurlAttemptAt = 0f;
            _valid = false;
            _source = null;
            _overlay = null;
            _link = null;
            _barHead = null;
            _track = null;
            _trackValid = false;
            _trackSail = null;
            _polledSelection = null;
            _lastSummary = null;
            _settleArmed = false;
            _holding = false;
            _revealAfterRecompute = false;
            _paperSampled = false;
            _paperRef = null;
            _boat = null;

            // Materials are destroyed, not merely dropped. Unity only cleans up a material it
            // instantiated itself through the renderer.material getter; these were constructed here
            // and assigned in, so they are this object's to destroy, and destroying _root does not
            // touch them. Teardown runs on every shipyard exit and EnsureVisuals builds a fresh set
            // on the next visit, so leaking them meant five orphaned native objects per visit,
            // reclaimed by nothing short of a scene unload.
            DestroyMaterial(ref _linkMaterial);
            DestroyMaterial(ref _trackMaterial);
            DestroyMaterial(ref _forceMaterial);
            _forceArrows.Clear();
            _forceFrom.Clear();
            _forceTo.Clear();
            _forceShare.Clear();
            _forceNewtons.Clear();

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
        /// color-coded readout that lies.
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

        /// <summary>Opaque white, for the helm bar, which takes its color from vertex colors.</summary>
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
        /// Three round buttons and nothing else. The buttons carry the marker colors, so the strip
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
            // While held, the strip and the legend stay up with a Calculating note, so it is plain
            // the mod is working; only what is drawn on the boat waits. Not from the very first
            // frame, though: AdoptGameStyle takes the shipyard's paper once, and it used to first run
            // a RebuildDelay into the visit, when the shipyard's own buttons are certainly up.
            bool waiting = _holding && Time.unscaledTime - _holdStart >= RebuildDelay;
            if (!TargetActive || (!_valid && !waiting)) return;
            EnsureStyles();
            AdoptGameStyle();

            // First, so the strip and its notes draw over a label rather than under one, and
            // before the panel check, because the arrows can be switched on from the config with
            // the strip hidden and their numbers belong to them, not to the strip.
            if (!_holding)
            {
                DrawForceLabels();
                DrawWindLabels();
            }

            if (Config.Settings.ShowPanel != null && !Config.Settings.ShowPanel.Value) return;

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

            // The legend hangs under the strip while the ring is up, and the hover note under that.
            float below = _panel.yMax + 6f;
            bool ringWanted = _holding
                ? Config.Settings.ShowRing == null || Config.Settings.ShowRing.Value
                : _ring.Visible;
            if (ringWanted && (Config.Settings.ShowRingLegend == null || Config.Settings.ShowRingLegend.Value))
                below = DrawRingLegend(_panel.xMax, below) + 6f;
            if (_holding) below = DrawCalculating(_panel.xMax, below) + 6f;

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

            // The one case where silence would be dishonest: a sail whose center of effort does not
            // follow its own size makes the magenta marker wrong, not merely imprecise. That one is
            // not a hover hint, so it shows immediately, and stays put under the strip.
            if (_unreliable)
            {
                Rect warning = MeasureNote("A sail's center of effort does not follow its size. "
                    + "The magenta marker is unreliable.");
                warning.x = _panel.xMax - warning.width;
                warning.y = below;
                DrawNote(warning);
            }

            // A hover hint sits by the cursor and follows it, flipped to whichever side has room, so
            // it reads as belonging to the control under the mouse (user's call, 2026-11-14).
            if (hoverReady)
            {
                Rect tip = MeasureNote(_hoverTracked);
                Vector2 mouse = Event.current.mousePosition;
                const float OffsetX = 16f, OffsetY = 20f, Edge = 4f;

                tip.x = mouse.x + OffsetX;
                if (tip.xMax > Screen.width - Edge) tip.x = mouse.x - OffsetX - tip.width;
                tip.y = mouse.y + OffsetY;
                if (tip.yMax > Screen.height - Edge) tip.y = mouse.y - OffsetY - tip.height;
                tip.x = Mathf.Clamp(tip.x, Edge, Mathf.Max(Edge, Screen.width - Edge - tip.width));
                tip.y = Mathf.Clamp(tip.y, Edge, Mathf.Max(Edge, Screen.height - Edge - tip.height));
                DrawNote(tip);
            }
        }

        private readonly GUIContent _noteContent = new GUIContent();
        private bool _noteWraps;

        /// <summary>
        /// The box a note needs, at the origin. One line, box sized to the line. Measured with
        /// wrapping OFF, because CalcSize on a word-wrapping style is not the single-line size: with
        /// the shipyard's adopted font it returned a narrower preferred width, the label wrapped to
        /// two lines inside it, and the height came up short, clipping the second line. Wrapping
        /// stays off for the draw too, so what was measured is what is drawn. Only a note longer than
        /// the cap wraps, at the height computed FOR that wrapped width.
        /// </summary>
        private Rect MeasureNote(string text)
        {
            const float MaxNoteWidth = 230f;
            _noteContent.text = text;

            _tipStyle.wordWrap = false;
            Vector2 line = _tipStyle.CalcSize(_noteContent);

            // Two pixels of slack so a rounding difference between measure and draw cannot re-wrap.
            float width = line.x + 2f;
            float height = line.y;
            _noteWraps = width > MaxNoteWidth;
            if (_noteWraps)
            {
                width = MaxNoteWidth;
                _tipStyle.wordWrap = true;
                height = _tipStyle.CalcHeight(_noteContent, width);
            }
            _tipStyle.wordWrap = true;
            return new Rect(0f, 0f, width, height);
        }

        /// <summary>Draws the note last measured, in the given box.</summary>
        private void DrawNote(Rect rect)
        {
            DrawPaper(rect);
            _tipStyle.wordWrap = _noteWraps;
            GUI.Label(rect, _noteContent, _tipStyle);
            _tipStyle.wordWrap = true;
        }

        // ------------------------------------------------------------------ paper color

        // The shipyard's paper is lit: it is world-space, on a shader that takes the scene's light,
        // so it is pinker and darker in a storm, paler at noon, dim at night, while the panels here
        // are IMGUI and drawn unlit. One constant cannot match it (user, 2026-11-14, in a storm: the
        // strip and legend read orange-cream against salmon scrolls). So the color is read off the
        // screen: about once a second, at the end of the frame, the middle of the largest shipyard
        // scroll that is clear of these panels, keeping the brighter half of the pixels so the ink
        // on it does not count, and taking their median.
        private Color _paper = PaperColor;
        private Color _paperTarget = PaperColor;
        private bool _paperSampled;
        private Renderer _paperRef;
        private Camera _paperCamera;
        private Texture2D _paperRead;
        private bool _paperLogged;
        private const float PaperSampleSeconds = 1f;
        private const int PaperReadMax = 192;

        private System.Collections.IEnumerator SamplePaperLoop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            var pause = new WaitForSecondsRealtime(PaperSampleSeconds);
            while (true)
            {
                // Quickly at the start of a visit, so the panels are the right color when they
                // first show; once a second after that.
                if (_paperSampled) yield return pause;
                else yield return null;
                if (!ShipyardHooks.InShipyard || HullShowcase.Active) continue;
                yield return endOfFrame;
                try
                {
                    SamplePaper();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug("[Gizmo] paper sample failed: " + e.Message);
                    _paperSampled = true;   // so a failure retries once a second, not every frame
                }
            }
        }

        private void SamplePaper()
        {
            if (!ValidPaperRef()) FindPaperRef();
            if (_paperRef == null) return;

            Rect inner;
            if (!ScreenRectOf(_paperRef, _paperCamera, out inner)) { _paperRef = null; return; }

            // The middle half of the scroll, away from its ragged ends and edges, capped in size.
            inner = new Rect(inner.x + inner.width * 0.25f, inner.y + inner.height * 0.25f,
                inner.width * 0.5f, inner.height * 0.5f);
            float w = Mathf.Min(inner.width, PaperReadMax), h = Mathf.Min(inner.height, PaperReadMax);
            var read = new Rect(Mathf.Floor(inner.center.x - w * 0.5f), Mathf.Floor(inner.center.y - h * 0.5f),
                Mathf.Floor(w), Mathf.Floor(h));
            read.xMin = Mathf.Max(0f, read.xMin);
            read.yMin = Mathf.Max(0f, read.yMin);
            read.xMax = Mathf.Min(Screen.width, read.xMax);
            read.yMax = Mathf.Min(Screen.height, read.yMax);
            if (read.width < 8f || read.height < 8f) return;

            if (_paperRead == null)
                _paperRead = new Texture2D(PaperReadMax, PaperReadMax, TextureFormat.RGB24, false)
                    { hideFlags = HideFlags.HideAndDontSave };
            _paperRead.ReadPixels(read, 0, 0, false);
            Color32[] pixels = _paperRead.GetPixels32();

            // Only the part this read wrote; the rest holds whatever an earlier read left.
            int rw = (int)read.width, rh = (int)read.height;
            var samples = new System.Collections.Generic.List<PaperPixel>(rw * rh / 4 + 1);
            for (int y = 0; y < rh; y += 2)
                for (int x = 0; x < rw; x += 2)
                {
                    Color32 c = pixels[y * PaperReadMax + x];
                    samples.Add(new PaperPixel(0.299f * c.r + 0.587f * c.g + 0.114f * c.b, c));
                }
            if (samples.Count < 16) return;
            samples.Sort((a, b) => a.Luma.CompareTo(b.Luma));

            // The median of the brighter half: ink and shadow fall in the darker half.
            int start = samples.Count / 2;
            var bright = samples.GetRange(start, samples.Count - start);
            var sampled = new Color(Median(bright, 0) / 255f, Median(bright, 1) / 255f, Median(bright, 2) / 255f, 1f);

            _paperTarget = sampled;
            if (!_paperSampled)
            {
                _paper = sampled;
                _paperSampled = true;
            }
            if (!_paperLogged)
            {
                _paperLogged = true;
                Plugin.Log.LogInfo($"[Gizmo] paper color sampled from '{_paperRef.name}' "
                    + $"({read.width:F0}x{read.height:F0} px): {sampled}.");
            }
        }

        private struct PaperPixel
        {
            public float Luma;
            public Color32 Color;
            public PaperPixel(float luma, Color32 color) { Luma = luma; Color = color; }
        }

        private static float Median(System.Collections.Generic.List<PaperPixel> list, int channel)
        {
            var values = new float[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                Color32 c = list[i].Color;
                values[i] = channel == 0 ? c.r : channel == 1 ? c.g : c.b;
            }
            Array.Sort(values);
            return values[values.Length / 2];
        }

        private bool ValidPaperRef()
        {
            return _paperRef != null && _paperRef.enabled && _paperRef.gameObject.activeInHierarchy
                && _paperRef.isVisible && _paperCamera != null && _paperCamera.isActiveAndEnabled;
        }

        /// <summary>
        /// The largest parchment scroll of the shipyard's interface on screen that does not sit under
        /// this mod's panels in the top right corner, and the camera that draws it.
        /// </summary>
        private void FindPaperRef()
        {
            _paperRef = null;
            _paperCamera = null;
            ShipyardUI ui = ShipyardUI.instance;
            if (ui == null) return;
            try { FindPaperRefIn(ui); }
            finally
            {
                if (_paperRef == null && !_paperMissLogged)
                {
                    _paperMissLogged = true;
                    Plugin.Log.LogInfo("[Gizmo] no shipyard scroll on screen to sample the paper color "
                        + "from; the panels keep their fixed color until one shows.");
                }
            }
        }

        private bool _paperMissLogged;

        private void FindPaperRefIn(ShipyardUI ui)
        {
            // The corner these panels take, in screen pixels with y up, kept clear of samples.
            var ours = new Rect(Screen.width - 340f, Screen.height - 360f, 340f, 360f);

            // The big scrolls, not the buttons. The first version looked for the parchment texture,
            // which the scrolls turned out not to use, and sampled 'mode button Parts Other', an
            // unselected tab, so every panel came out dark brown (2026-11-14). Anything that is a
            // button or lettering is passed over, and of what is left the largest on screen is a
            // scroll.
            float best = 0f;
            var seen = _paperCandidatesLogged ? null : new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<float, string>>();
            foreach (Renderer r in ui.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || !r.isVisible) continue;
                if (r.GetComponent<TextMesh>() != null || r.GetComponentInParent<GoPointerButton>() != null) continue;

                Camera camera = CameraDrawing(r.gameObject.layer);
                Rect rect;
                if (camera == null || !ScreenRectOf(r, camera, out rect)) continue;
                if (rect.Overlaps(ours)) continue;

                float area = rect.width * rect.height;
                if (seen != null)
                {
                    Material m = r.sharedMaterial;
                    seen.Add(new System.Collections.Generic.KeyValuePair<float, string>(area, $"'{r.name}' {rect.width:F0}x{rect.height:F0} px, "
                        + $"shader '{(m == null || m.shader == null ? "none" : m.shader.name)}', "
                        + $"texture '{(m == null || !m.HasProperty("_MainTex") || m.mainTexture == null ? "none" : m.mainTexture.name)}'"));
                }
                if (area > best)
                {
                    best = area;
                    _paperRef = r;
                    _paperCamera = camera;
                }
            }

            if (seen != null && seen.Count > 0)
            {
                _paperCandidatesLogged = true;
                seen.Sort((a, b) => b.Key.CompareTo(a.Key));
                var lines = new System.Collections.Generic.List<string>();
                for (int i = 0; i < seen.Count && i < 6; i++) lines.Add(seen[i].Value);
                Plugin.Log.LogInfo("[Gizmo] paper candidates, largest first: " + string.Join("; ", lines.ToArray()) + ".");
            }
        }

        private bool _paperCandidatesLogged;

        /// <summary>The enabled camera drawn last that renders this layer to the screen.</summary>
        private static Camera CameraDrawing(int layer)
        {
            Camera found = null;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null || !c.enabled || (c.cullingMask & (1 << layer)) == 0 || c.targetTexture != null) continue;
                if (found == null || c.depth > found.depth) found = c;
            }
            return found;
        }

        /// <summary>The renderer's box on screen, in pixels with y up, the way ReadPixels takes it.</summary>
        private static bool ScreenRectOf(Renderer r, Camera camera, out Rect rect)
        {
            rect = default(Rect);
            Bounds b = r.bounds;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                Vector3 p = camera.WorldToScreenPoint(corner);
                if (p.z <= 0f) return false;
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return rect.width > 20f && rect.height > 20f && rect.xMax > 0f && rect.yMax > 0f
                && rect.xMin < Screen.width && rect.yMin < Screen.height;
        }

        private readonly GUIContent _calculatingContent = new GUIContent();

        /// <summary>
        /// "Calculating" with one, two, then three dots, under the legend while the gizmo is held.
        /// The box is sized for three dots and the text is left-aligned in it, so neither the box nor
        /// the word moves as the dots change. Returns its bottom edge.
        /// </summary>
        private float DrawCalculating(float right, float top)
        {
            _calculatingContent.text = "Calculating...";
            TextAnchor alignment = _tipStyle.alignment;
            bool wrap = _tipStyle.wordWrap;
            _tipStyle.wordWrap = false;
            _tipStyle.alignment = TextAnchor.MiddleLeft;

            Vector2 size = _tipStyle.CalcSize(_calculatingContent);
            var rect = new Rect(right - size.x - 2f, top, size.x + 2f, size.y);
            DrawPaper(rect);

            int dots = 1 + (int)(Time.unscaledTime / 0.4f) % 3;
            _calculatingContent.text = "Calculating" + new string('.', dots);
            GUI.Label(rect, _calculatingContent, _tipStyle);

            _tipStyle.alignment = alignment;
            _tipStyle.wordWrap = wrap;
            return rect.yMax;
        }

        // ------------------------------------------------------------------ ring legend

        /// <summary>The legend's rows, top to bottom, at their position on the ramp (+1 far weather, -1 far lee).</summary>
        private static readonly string[] LegendLabels =
        {
            "Strong weather helm", "Weather helm", "Balanced", "Lee helm", "Strong lee helm",
        };

        /// <summary>The extra rows with ShowHelmTicks on, outward first.</summary>
        private static readonly string[] LegendTickLabels = { "Tick outward: weather", "Tick inward: lee" };

        /// <summary>
        /// The last row with ExtendShipyardCamera on: the lift's control. Players with the game's
        /// control hints off would otherwise never learn it, and the legend is where they look.
        /// </summary>
        private const string LegendCameraLabel = "Right-drag up/down: raise view";

        private const float LegendRow = 18f;
        private const float LegendStrip = 12f;
        private Texture2D _legendTex;
        private int _legendPalette;
        private GUIStyle _legendStyle, _legendTitleStyle;
        private readonly GUIContent _legendContent = new GUIContent();

        /// <summary>
        /// What the ring's colors mean, as a strip of the same ramp the ring, the helm bar and the
        /// reef track are painted from (BalanceRing.HelmColor), with a label at each of its stops,
        /// then the gray of no verdict. Right-aligned to <paramref name="right"/>, starting at
        /// <paramref name="top"/>; returns its bottom edge.
        /// </summary>
        private float DrawRingLegend(float right, float top)
        {
            EnsureLegend();

            float labelWidth = 0f;
            _legendContent.text = "Helm";
            float titleHeight = _legendTitleStyle.CalcSize(_legendContent).y;
            labelWidth = _legendTitleStyle.CalcSize(_legendContent).x;
            foreach (string label in LegendLabels)
            {
                _legendContent.text = label;
                labelWidth = Mathf.Max(labelWidth, _legendStyle.CalcSize(_legendContent).x);
            }
            _legendContent.text = "No verdict";
            labelWidth = Mathf.Max(labelWidth, _legendStyle.CalcSize(_legendContent).x);

            bool ticks = Config.Settings.ShowHelmTicks != null && Config.Settings.ShowHelmTicks.Value;
            if (ticks)
                foreach (string label in LegendTickLabels)
                {
                    _legendContent.text = label;
                    labelWidth = Mathf.Max(labelWidth, _legendStyle.CalcSize(_legendContent).x);
                }

            bool camera = Config.Settings.ExtendShipyardCamera != null && Config.Settings.ExtendShipyardCamera.Value
                && Shipyard.ShipyardCamera.Working;
            if (camera)
            {
                _legendContent.text = LegendCameraLabel;
                labelWidth = Mathf.Max(labelWidth, _legendStyle.CalcSize(_legendContent).x);
            }

            const float Pad = 12f, Gap = 8f, CameraGap = 6f;
            int rows = LegendLabels.Length + 1 + (ticks ? LegendTickLabels.Length : 0) + (camera ? 1 : 0);
            float width = Pad + LegendStrip + Gap + labelWidth + Pad;
            float height = Pad + titleHeight + 2f + rows * LegendRow + (camera ? CameraGap : 0f) + Pad - 4f;
            var box = new Rect(right - width, top, width, height);
            DrawPaper(box);

            float x = box.x + Pad;
            float y = box.y + Pad - 3f;
            _legendContent.text = "Helm";
            GUI.Label(new Rect(x, y, width - 2f * Pad, titleHeight), _legendContent, _legendTitleStyle);
            y += titleHeight + 2f;

            // The strip runs from the middle of the first row to the middle of the last ramp row, so
            // each label sits level with its own color.
            float stripTop = y + LegendRow * 0.5f;
            float stripBottom = y + LegendRow * (LegendLabels.Length - 0.5f);
            GUI.DrawTexture(new Rect(x, stripTop - 2f, LegendStrip, stripBottom - stripTop + 4f), _legendTex,
                ScaleMode.StretchToFill, false);

            float textX = x + LegendStrip + Gap;
            for (int i = 0; i < LegendLabels.Length; i++)
            {
                _legendContent.text = LegendLabels[i];
                GUI.Label(new Rect(textX, y + i * LegendRow, labelWidth + 2f, LegendRow), _legendContent, _legendStyle);
            }

            // Gray on its own, apart from the ramp: it is not a point on the scale but the lack of one.
            float grayY = y + LegendLabels.Length * LegendRow;
            Color previous = GUI.color;
            Color gray = BalanceRing.NoVerdictColor;
            GUI.color = new Color(gray.r, gray.g, gray.b, 1f);
            GUI.DrawTexture(new Rect(x, grayY + (LegendRow - LegendStrip) * 0.5f, LegendStrip, LegendStrip),
                Texture2D.whiteTexture);
            GUI.color = previous;
            _legendContent.text = "No verdict";
            GUI.Label(new Rect(textX, grayY, labelWidth + 2f, LegendRow), _legendContent, _legendStyle);

            // With ticks on, what their direction means, each beside a small drawing of one: a bar
            // for the ring with the tick standing above it (out) or hanging below it (in).
            if (ticks)
            {
                Color ink = _legendStyle.normal.textColor;
                for (int i = 0; i < LegendTickLabels.Length; i++)
                {
                    float rowY = grayY + (i + 1) * LegendRow;
                    float mid = rowY + LegendRow * 0.5f;
                    bool outward = i == 0;

                    GUI.color = ink;
                    GUI.DrawTexture(new Rect(x, mid - 1f, LegendStrip, 3f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(x + LegendStrip * 0.5f - 1.5f, outward ? mid - 7f : mid + 2f, 3f, 6f),
                        Texture2D.whiteTexture);
                    GUI.color = previous;

                    _legendContent.text = LegendTickLabels[i];
                    GUI.Label(new Rect(textX, rowY, labelWidth + 2f, LegendRow), _legendContent, _legendStyle);
                }
            }

            // The camera's control, set apart from the color key by a small gap, beside an up/down
            // arrow: a stem with a cap at each end.
            if (camera)
            {
                float rowY = grayY + (1 + (ticks ? LegendTickLabels.Length : 0)) * LegendRow + CameraGap;
                float mid = rowY + LegendRow * 0.5f;
                float cx = x + LegendStrip * 0.5f;

                GUI.color = _legendStyle.normal.textColor;
                GUI.DrawTexture(new Rect(cx - 1f, mid - 6f, 2f, 12f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 2f, mid - 5f, 4f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 4f, mid - 3f, 8f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 4f, mid + 1f, 8f, 2f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 2f, mid + 3f, 4f, 2f), Texture2D.whiteTexture);
                GUI.color = previous;

                _legendContent.text = LegendCameraLabel;
                GUI.Label(new Rect(textX, rowY, labelWidth + 2f, LegendRow), _legendContent, _legendStyle);
            }

            return box.yMax;
        }

        private void EnsureLegend()
        {
            if (_legendTex != null && _legendPalette != PaletteVersion)
            {
                Destroy(_legendTex);
                _legendTex = null;
            }

            if (_legendTex == null)
            {
                _legendPalette = PaletteVersion;
                // Rows sit evenly down the strip but the ramp's stops do not sit evenly along it (the
                // middle stop is at 3 bands of 8), so each quarter of the strip is mapped to the stretch
                // of ramp between two labeled stops.
                float mid = (BalanceRing.MidAtBands - 1f) / (BalanceRing.FarAtBands - 1f);
                float[] stops = { 1f, mid, 0f, -mid, -1f };

                const int Height = 128;
                _legendTex = new Texture2D(1, Height, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                for (int py = 0; py < Height; py++)
                {
                    // Texture rows count up from the bottom; the strip reads top down.
                    float s = 1f - py / (float)(Height - 1);
                    float scaled = s * (stops.Length - 1);
                    int leg = Mathf.Min((int)scaled, stops.Length - 2);
                    float p = Mathf.Lerp(stops[leg], stops[leg + 1], scaled - leg);
                    Color c = BalanceRing.HelmColorAt(p);
                    c.a = 1f;
                    _legendTex.SetPixel(0, py, c);
                }
                _legendTex.Apply();
            }

            if (_legendStyle == null)
            {
                _legendStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    wordWrap = false,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(0, 0, 0, 0),
                    margin = new RectOffset(0, 0, 0, 0),
                };
                _legendStyle.normal.textColor = new Color(0.20f, 0.13f, 0.08f);
                if (_tipStyle != null && _tipStyle.font != null) _legendStyle.font = _tipStyle.font;

                _legendTitleStyle = new GUIStyle(_legendStyle) { fontSize = 13, fontStyle = FontStyle.Bold };
            }
        }

        private void DrawPanel(int id)
        {
            // Centerd with flexible space on all four sides rather than by trusting PanelWidth to
            // be exactly right. The width constant is derived from an assumed IMGUI box model, and
            // any error in it showed up as the buttons hugging the left edge with dead space on the
            // right. Flexible space splits whatever slack actually exists evenly, so the row is
            // centerd whether the constant is perfect or a few pixels out.
            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Toggle(Effort, _unreliable ? UnreliableColor : EffortColor, "Center of effort");
            Toggle(Resistance, ResistanceColor, "Center of lateral resistance and helm bar");
            Toggle(Mass, MassColor, "Center of mass");
            RingToggle();
            ForceToggle();
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
        /// of the same material as everything around it instead of being a gray box from a
        /// different decade of UI design. Falls back silently to the default IMGUI skin, which is
        /// legible if plain, when nothing suitable is on screen.
        /// </summary>
        private void AdoptGameStyle()
        {
            if (_styleAdopted) return;
            _styleAdopted = true;

            try
            {
                // The shipyard's own buttons where its interface is up; anywhere in the scene picked
                // an almanac in the player's things before (2026-11-14 log: '701 almanac(Clone)').
                GoPointerButton[] buttons = ShipyardUI.instance != null
                    ? ShipyardUI.instance.GetComponentsInChildren<GoPointerButton>()
                    : FindObjectsOfType<GoPointerButton>();
                foreach (GoPointerButton button in buttons)
                {
                    // Not this mod's own furl button, a clone that carries none of the game's styling
                    // it is meant to borrow (log 2026-11-14: adopted from 'RigBalanceFurlSelectedSail').
                    if (button == null || button.name.StartsWith("RigBalance", StringComparison.Ordinal)) continue;

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
                    // The material's own tint. Without this the panel came out gray-tan while every
                    // button around it was warm peach, because the color lives on the material and
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

        private void Toggle(int index, Color color, string tooltip)
        {
            Color previous = GUI.color;
            GUI.color = _visible[index] ? color : new Color(0.32f, 0.32f, 0.32f, 0.85f);

            bool clicked = GUILayout.Button(new GUIContent(string.Empty, tooltip), _circleStyle,
                GUILayout.Width(24f), GUILayout.Height(24f));

            GUI.color = previous;

            if (!clicked) return;

            _visible[index] = !_visible[index];
            if (_markers[index] != null) _markers[index].SetActive(_visible[index]);
            UpdateBarAndTrackEnabled();

            ConfigEntry<bool> entry = VisibilityEntry(index);
            if (entry != null) entry.Value = _visible[index];
        }

        /// <summary>
        /// The fourth button: the balance ring itself. Not routed through Toggle because the ring is
        /// not a marker. Its button is an annulus rather than a disc, so its shape says which control
        /// it is, and its state lives in the ShowBalanceRing config entry rather than in _visible,
        /// because both places that gate the ring already read that entry: hiding it this way also
        /// stops the 36-bearing sweep from running at all, which is most of what the gizmo costs.
        ///
        /// The texture carries its own colors, so the tint is white when on, unlike the marker
        /// buttons whose white discs exist to BE tinted. Off gets the same gray as everything else.
        /// </summary>
        private void RingToggle()
        {
            ConfigEntry<bool> entry = Config.Settings.ShowRing;
            bool on = entry == null || entry.Value;

            Color previous = GUI.color;
            GUI.color = on ? Color.white : new Color(0.32f, 0.32f, 0.32f, 0.85f);

            bool clicked = GUILayout.Button(new GUIContent(string.Empty, "Balance ring and waterline"), _ringStyle,
                GUILayout.Width(24f), GUILayout.Height(24f));

            GUI.color = previous;

            if (!clicked || entry == null) return;

            entry.Value = !on;

            if (entry.Value)
            {
                // While the ring is off no sweeps run, so whatever mesh still exists describes the
                // rig as it stood when it was hidden. Shown stale it would be quietly wrong about
                // every edit made since, so re-showing asks for a rebuild instead of a reveal; the
                // debounced recompute sweeps and shows it fresh.
                _ringNeedsRebuild = true;
                ShipyardHooks.MarkDirty("ring shown");
            }
            else
            {
                _ring.SetVisible(false);
                _waterline.SetTarget(_attitude, false);
            }
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

            bool set = Shipyard.FurlSailButton.IsSet(selected);

            Color previous = GUI.color;
            GUI.color = set ? new Color(0.95f, 0.95f, 0.90f) : new Color(0.32f, 0.32f, 0.32f, 0.85f);

            string name = string.IsNullOrEmpty(selected.sailName) ? "this sail" : selected.sailName;
            string key = FurlKeyName();
            bool clicked = GUILayout.Button(
                new GUIContent(string.Empty, (set ? "Furl " : "Set ") + name + (key == null ? "" : " (" + key + ")")),
                _circleStyle, GUILayout.Width(24f), GUILayout.Height(24f));

            GUI.color = previous;
            if (!clicked) return;

            // The reef track already showed where this would land; clicking commits to it.
            Shipyard.FurlSailButton.Toggle(selected);
        }

        /// <summary>The furl key as the config writes it, or null when it is unbound.</summary>
        private static string FurlKeyName()
        {
            var entry = Config.Settings.FurlSelectedSailKey;
            if (entry == null || entry.Value.MainKey == KeyCode.None) return null;
            return entry.Value.ToString();
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
            if (_circleStyle != null)
            {
                // The ring button wears the helm ramp, so it is painted again when the palette changes.
                if (_ringTexPalette != PaletteVersion && _ringStyle != null)
                {
                    _ringTexPalette = PaletteVersion;
                    if (_ringTex != null) Destroy(_ringTex);
                    _ringTex = MakeRingTexture(28);
                    _ringStyle.normal.background = _ringTex;
                    _ringStyle.hover.background = _ringTex;
                    _ringStyle.active.background = _ringTex;
                }
                return;
            }

            _ringTexPalette = PaletteVersion;

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

            // Same box, different face: the ring button shares the circle style's geometry so the
            // row stays even, and only its background is the colored annulus.
            _ringTex = MakeRingTexture(28);
            _ringStyle = new GUIStyle(_circleStyle);
            _ringStyle.normal.background = _ringTex;
            _ringStyle.hover.background = _ringTex;
            _ringStyle.active.background = _ringTex;

            _forceTex = MakeForceTexture(28);
            _forceStyle = new GUIStyle(_circleStyle);
            _forceStyle.normal.background = _forceTex;
            _forceStyle.hover.background = _forceTex;
            _forceStyle.active.background = _forceTex;

            // Boxed rather than a bare label so the text stays legible over the shipyard's pale
            // paper panels as well as over the night sky.
            _tipStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 12,
                wordWrap = true,
                // Centered. Right-alignment existed to park short text against the near edge of a
                // fixed-width box; now that the box fits the text there is no empty side to avoid.
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(10, 10, 7, 7),
                border = new RectOffset(12, 12, 12, 12),
            };
            // Same paper as the panel, and AdoptGameStyle replaces the lettering and ink with the
            // shipyard's own so the label reads as part of the game rather than beside it.
            // No background of its own: every note draws DrawPaper first, which is tinted.
            _tipStyle.normal.background = null;
            _tipStyle.normal.textColor = new Color(0.20f, 0.13f, 0.08f);

            // A plain box, drawn separately from the layout area, so the panel is fixed in place.
            // It used to be a GUI.Window purely to inherit IMGUI's dragging, which made it the one
            // thing in the shipyard that could be moved and read as foreign because of it.
            // Corner radius 10 in a 32 px tile, nine-sliced on a 12 px border so the corners keep
            // their shape at any panel size and only the flat middle stretches.
            _panelTex = MakePanelTexture(32, 10, neutral: true);
            _panelStyle = new GUIStyle(GUIStyle.none)
            {
                border = new RectOffset(12, 12, 12, 12),
            };
            _panelStyle.normal.background = _panelTex;
            _panelInnerStyle = new GUIStyle(GUIStyle.none) { padding = new RectOffset(8, 8, 8, 8) };
        }

        /// <summary>
        /// Draws the panel background using the shipyard's ACTUAL paper, tinted with the shipyard's
        /// actual material color.
        ///
        /// The first attempt stretched the whole sheet across the panel and looked like a rug,
        /// because parchment_alpha is a long scroll with ragged ends and visible fibre. The fix is
        /// not to draw it differently sized but to draw a small patch from the MIDDLE of it: a few
        /// percent of a sheet of paper is nearly uniform, so scaling that patch cannot distort
        /// anything recognizable, and it is still literally the game's own texture and color
        /// rather than an imitation of them.
        /// </summary>
        private void DrawPaper(Rect rect)
        {
            // The color was a constant, after three attempts at deriving it, and is now sampled off
            // the screen (see "paper color"), falling back to the constant. What follows is the
            // history of why it is not read from the material.
            //
            // Drawing the game's texture painted black, because parchment_alpha does not carry the
            // paper in its RGB. Generating the shape and tinting it with the material's _Color
            // painted black too, because that property is black as well. Both attempts multiplied
            // by a value that turns out to be zero, which is why "fixing" it twice changed nothing.
            // Wherever the shipyard's peach actually comes from, it is not in either place I looked,
            // and a fourth guess is worth less than a constant matched to the buttons on screen.
            //
            // The material color is still read, but only to log it.
            // Nine-sliced through the panel style, never stretched. Stretching the 32 pixel tile over
            // a whole panel blurred it and scaled its rounded corners up until they cut into the
            // legend's first and last rows.
            Color previous = GUI.color;
            GUI.color = new Color(_paper.r, _paper.g, _paper.b, 1f);
            GUI.Box(rect, GUIContent.none, _panelStyle);
            GUI.color = previous;
        }

        /// <summary>Matched by eye to the shipyard's own buttons. The fallback when no sample is taken.</summary>
        private static readonly Color PaperColor = new Color(0.96f, 0.81f, 0.67f, 0.97f);
        private static readonly Color PaperEdgeColor = new Color(0.45f, 0.30f, 0.20f, 1.00f);

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
        /// the shipyard's lettering and ink color are still borrowed for the hover label, so the
        /// two sit together.
        ///
        /// The circles need this behind them for a plainer reason: they are saturated colors meant
        /// to match the markers, and against an open sky they have nothing to contrast with.
        /// </summary>
        internal static Texture2D MakePanelTexture(int size, int radius, bool neutral = false)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var pixels = new Color32[size * size];

            Color paper = PaperColor;
            Color edge = PaperEdgeColor;

            // Neutral: white paper and a rim as dark relative to it as the colored one, for drawing
            // under GUI.color, which multiplies. Tinted with PaperColor it is the colored tile again;
            // tinted with a sampled color the rim keeps the same proportion to the paper.
            if (neutral)
            {
                edge = new Color(edge.r / paper.r, edge.g / paper.g, edge.b / paper.b, 1f);
                paper = new Color(1f, 1f, 1f, paper.a);
            }

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

        /// <summary>
        /// The balance ring's button: an annulus wearing the ring's own verdict colors, so the
        /// button is a miniature of the thing it controls. It runs through the same diverging ramp
        /// the rose draws with (BalanceRing.HelmColorAt): dark blue at the top through light blue,
        /// balanced green on the beams, orange, then red at the bottom, and mirror symmetric port for
        /// starboard because the rose it stands for is. Built from the vertical component of each
        /// pixel's direction rather than from its angle, which gives that symmetry with no seam to
        /// hide.
        /// </summary>
        private static Texture2D MakeRingTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float r = size * 0.5f;
            // Proportioned like the drawn rose: a band, not a hoop so thin it reads as an outline,
            // and not so thick it reads as a disc with a hole poked in it.
            float inner = r * 0.52f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    // One pixel of feathering on both edges of the band.
                    float a = Mathf.Clamp01(r - d) * Mathf.Clamp01(d - inner);

                    // Texture row 0 is the bottom, so +dy is up on screen.
                    float t = d > 0.001f ? dy / d : 0f;
                    Color color = BalanceRing.HelmColorAt(t);

                    color.a = a;
                    tex.SetPixel(x, y, color);
                }
            }

            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
