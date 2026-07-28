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
    ///   magenta  centre of effort      where the whole sail plan pushes
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
        private static float WindAngleDegrees
        {
            get { return Config.Settings.WindAngle != null ? Config.Settings.WindAngle.Value : 45f; }
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

        private static readonly Color WeatherHelmColour = new Color(1.00f, 0.35f, 0.20f);
        private static readonly Color LeeHelmColour = new Color(0.30f, 0.80f, 1.00f);
        private static readonly Color BalancedColour = new Color(0.35f, 1.00f, 0.45f);

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

        // The reef track: where the combined centre of effort travels as the selected sail goes
        // between fully set and fully furled. Two endpoints describe every setting in between.
        private LineRenderer _track;
        private Material _trackMaterial;
        private Vector3 _trackFrom, _trackTo;   // boat-local
        private bool _trackValid;
        private Sail _trackSail;        // owned by Recompute
        private Sail _polledSelection;  // owned by the Update poll, never by Recompute

        private Transform _boat;
        private Camera _overlay, _source;
        private int _layer = -1;
        private Shader _unlit;
        private bool _shaderResolved;

        private bool _valid;
        private bool _unreliable;
        private float _helmMetres;      // positive = weather helm, in metres of CE offset

        private float _rebuildAt = -1f;

        private GUIStyle _circleStyle, _tipStyle, _panelStyle, _panelInnerStyle;
        private Texture2D _circleTex;
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

            if (ShipyardHooks.ConsumeDirty()) _rebuildAt = Time.unscaledTime + RebuildDelay;

            // Selecting a different sail changes which reef track is meaningful, and the shipyard
            // gives no event for it, so it is polled. Cheap: one accessor, no allocation.
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

            if (_rebuildAt > 0f && Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = -1f;
                Recompute();
            }
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

            // The offset that IS the answer, in metres, along the boat's own fore-aft axis.
            // Dividing the yaw moment by the side force turns a size-dependent torque into a lever
            // arm, which compares honestly between a dinghy and a brig.
            float lever = Mathf.Abs(balance.TotalSideForce) > 0.01f
                ? balance.YawMoment / balance.TotalSideForce
                : 0f;
            _helmMetres = -lever;               // positive = CE aft of CLR = weather helm
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

            Repaint(Effort, _unreliable ? UnreliableColour : EffortColour);
            UpdateLinkColour();
            UpdateTrack(boat, balance);
            SetValid(true);

            string verdict = Mathf.Abs(_helmMetres) < _balancedMetres ? "balanced"
                : (_helmMetres > 0f ? "weather helm" : "lee helm");
            Plugin.Log.LogInfo($"[Gizmo] {balance.Sails.Count} sail(s); CE offset "
                + $"{_helmMetres:F2} m ({verdict}, band {_balancedMetres:F2} m, hull {balance.HullLength:F1} m)"
                + $"{(_unreliable ? "; a sail's CE does not track its size" : "")}");

            // Per sail, so a furl that does not move the balance can be told apart from a furl that
            // never reached the model. Note Sail.cs:415 floors the multiplier at
            // Debugger.debugYardMult (0.015), so a furled sail keeps 1.5% of its force as bare-yard
            // windage rather than dropping to nothing.
            foreach (SailContribution c in balance.Sails)
            {
                Plugin.Log.LogInfo($"[Gizmo]   {c.Name}: unroll={c.Unroll:F2} "
                    + $"fwd={c.ForwardForce:F0} side={c.SideForce:F0} lever={c.LeverFromOrigin:F2} m "
                    + $"yaw={c.YawMoment:F0}");
            }
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
            if (_overlay != null && !valid) _overlay.enabled = false;
        }

        private void UpdateLinkColour()
        {
            Color colour = Mathf.Abs(_helmMetres) < _balancedMetres ? BalancedColour
                : (_helmMetres > 0f ? WeatherHelmColour : LeeHelmColour);
            if (_link == null) return;
            _link.startColor = colour;
            _link.endColor = colour;
            Tint(_linkMaterial, colour);
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
        /// Works out where the combined centre of effort would end up if the selected sail were
        /// taken to the opposite extreme of its furl state, and colours the track by the verdict at
        /// that destination. Blue means furling this sail would give you lee helm; red, weather.
        ///
        /// Only two evaluations are needed for a continuous answer. The path between them is a
        /// straight segment, because the combined centre is a force-weighted average of fixed
        /// points and reefing sweeps exactly one weight (see TryEvaluate's overrideSail).
        /// </summary>
        private void UpdateTrack(GameObject boat, RigBalance current)
        {
            _trackValid = false;
            _trackSail = SelectedSail();

            bool wanted = Config.Settings.ShowReefTrack == null || Config.Settings.ShowReefTrack.Value;
            if (!wanted || _trackSail == null || _boat == null) return;

            float destinationUnroll = _trackSail.currentUnroll > 0.5f ? 0f : 1f;

            RigBalance other;
            if (!RigModel.TryEvaluate(boat, WindAngleDegrees, out other, _trackSail, destinationUnroll))
                return;

            _trackFrom = _boat.InverseTransformPoint(current.CombinedCentreOfEffort);
            _trackTo = _boat.InverseTransformPoint(other.CombinedCentreOfEffort);

            float otherLever = Mathf.Abs(other.TotalSideForce) > 0.01f
                ? other.YawMoment / other.TotalSideForce
                : 0f;
            float otherHelm = -otherLever;

            Color colour = Mathf.Abs(otherHelm) < _balancedMetres ? BalancedColour
                : (otherHelm > 0f ? WeatherHelmColour : LeeHelmColour);

            if (_track != null)
            {
                _track.startColor = colour;
                _track.endColor = colour;
                Tint(_trackMaterial, colour);
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
                // Markers stay on the default layer so the shipyard camera still renders them; the
                // two inside the hull will simply be occluded.
                _layer = 0;
                foreach (GameObject marker in _markers)
                {
                    if (marker != null) marker.layer = 0;
                }
                if (_link != null) _link.gameObject.layer = 0;
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
                Plugin.Log.LogInfo("[Gizmo] marker shader: "
                    + (_unlit != null ? _unlit.name : "none found, falling back to emissive Standard"));
            }

            Material material;
            if (_unlit != null)
            {
                material = new Material(_unlit);
                // The colour is already in the texture, so any tint slot must be neutral or it
                // multiplies twice and the markers come out muddy.
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", Color.white);
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

        private void Tint(Material material, Color colour)
        {
            if (material == null) return;
            if (material.HasProperty("_Color") && _unlit != null) material.SetColor("_Color", colour);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", colour);
            if (_unlit == null) material.SetColor("_EmissionColor", colour);
        }

        /// <summary>
        /// Searched by name and in order, because a Unity build only contains the shaders something
        /// in it actually references. Every candidate is unlit, textured and alpha blended, which
        /// is what a round marker on a square quad needs. Which one exists is a property of this
        /// build, not something to assume, so the resolved name is logged.
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
                "Unlit/Transparent",
            };

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
            _furlAttempts = 0;
            _valid = false;
            _source = null;
            _overlay = null;
            _link = null;
            _linkMaterial = null;
            _track = null;
            _trackMaterial = null;
            _trackValid = false;
            _trackSail = null;
            _polledSelection = null;
            _boat = null;

            for (int i = 0; i < _markers.Length; i++)
            {
                _markers[i] = null;
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
            float width = PanelWidth
                + (!Shipyard.FurlSailButton.Exists && SelectedSail() != null ? 27f : 0f);
            _panel = new Rect(Screen.width - width - PanelMargin, PanelMargin, width, PanelHeight);

            _hoverTip = null;
            GUI.Box(_panel, GUIContent.none, _panelStyle);
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

                    _panelStyle.normal.background = paper;
                    _panelStyle.border = new RectOffset(8, 8, 8, 8);

                    if (text.font != null)
                    {
                        _tipStyle.font = text.font;
                        _tipStyle.fontSize = 13;
                    }
                    // The shipyard's ink, so hover text reads as a label on the same paper.
                    _tipStyle.normal.textColor = text.color;
                    _tipStyle.normal.background = paper;

                    Plugin.Log.LogInfo($"[Gizmo] adopted the shipyard's paper from '{button.name}' "
                        + $"(texture '{paper.name}', font '{(text.font == null ? "none" : text.font.name)}').");
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
            Sail selected = SelectedSail();
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
            selected.currentUnroll = set ? 0f : 1f;

            // Mirrors what Shipyard Expansion does after writing the field. The boom and cloth reef
            // effects poll currentUnroll every Update and need no prompting, but the animation-driven
            // one does, and not every sail carries it.
            try
            {
                var reef = selected.GetComponent<ReefEffectAnimUniversal>();
                if (reef != null) reef.RefreshCloth();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] reef refresh failed: " + e.Message);
            }

            ShipyardHooks.MarkDirty();
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
                fontSize = 11,
                wordWrap = true,
                alignment = TextAnchor.MiddleRight,
                padding = new RectOffset(8, 8, 6, 6),
            };
            _tipStyle.normal.textColor = Color.white;

            // A plain box, drawn separately from the layout area, so the panel is fixed in place.
            // It used to be a GUI.Window purely to inherit IMGUI's dragging, which made it the one
            // thing in the shipyard that could be moved and read as foreign because of it.
            _panelStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(0, 0, 0, 0) };
            _panelInnerStyle = new GUIStyle(GUIStyle.none) { padding = new RectOffset(8, 8, 8, 8) };
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
