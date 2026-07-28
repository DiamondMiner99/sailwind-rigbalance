using System;
using SailwindRigBalance.Core;
using SailwindRigBalance.Shipyard;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// The shipyard gizmo: two world-space markers, read the way KSP's CoM and CoL are read.
    ///
    ///   magenta  centre of effort      where the whole sail plan pushes
    ///   blue     centre of resistance  where the hull's lateral drag pushes back
    ///
    /// There is deliberately no centre-of-mass marker. BoatMass.UpdateMass adds a mast's and a
    /// sail's mass to the rigidbody's mass but never to its centre-of-mass offset, which is only
    /// ever moved by loose cargo and by the player standing somewhere. So the centre of mass is
    /// deaf to the rig: you can hang an entirely different sail plan and it will not move a
    /// millimetre. A marker that cannot respond to the only thing this mod exists to change is
    /// worse than no marker, because it invites the reading that it did respond.
    ///
    /// The answer is the fore-and-aft offset between magenta and blue, and nothing else. Magenta
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
        /// Close hauled. Balance is judged where it actually bites; a run has no side force to
        /// balance and would report every rig as neutral.
        /// </summary>
        private const float WindAngleDegrees = 45f;

        /// <summary>
        /// Seconds to wait after the last change event before recomputing. Not a performance tweak:
        /// it lets the destroyed sails of a customization actually go away before Mast.sails is
        /// enumerated, and collapses the shipyard's repeated UpdateOrder calls into one pass.
        /// </summary>
        private const float RebuildDelay = 0.25f;

        /// <summary>Offset below which a rig reads as balanced rather than one-handed.</summary>
        private const float BalancedMetres = 0.3f;

        private const int Effort = 0, Resistance = 1, MarkerCount = 2;

        private static readonly Color EffortColour = new Color(1.00f, 0.25f, 0.90f);
        private static readonly Color ResistanceColour = new Color(0.25f, 0.60f, 1.00f);
        private static readonly Color UnreliableColour = new Color(1.00f, 0.15f, 0.15f);

        private static readonly Color WeatherHelmColour = new Color(1.00f, 0.35f, 0.20f);
        private static readonly Color LeeHelmColour = new Color(0.30f, 0.80f, 1.00f);
        private static readonly Color BalancedColour = new Color(0.35f, 1.00f, 0.45f);

        private readonly GameObject[] _markers = new GameObject[MarkerCount];
        private readonly Material[] _materials = new Material[MarkerCount];
        private readonly Texture2D[] _textures = new Texture2D[MarkerCount];
        private readonly Color[] _painted = new Color[MarkerCount];
        private readonly Vector3[] _local = new Vector3[MarkerCount];
        private readonly bool[] _visible = { true, true };

        private GameObject _root;
        private LineRenderer _link;
        private Material _linkMaterial;
        private Texture2D _linkTexture;

        private Transform _boat;
        private Camera _overlay, _source;
        private int _layer = -1;
        private Shader _unlit;
        private bool _shaderResolved;

        private bool _valid;
        private bool _unreliable;
        private float _helmMetres;      // positive = weather helm, in metres of CE offset

        private float _rebuildAt = -1f;

        private GUIStyle _circleStyle, _tipStyle, _panelStyle;
        private Texture2D _circleTex;

        /// <summary>Arbitrary but stable, so IMGUI keeps the drag state across frames.</summary>
        private const int PanelWindowId = 0x52474D;

        // Sized to the buttons and nothing more: 8 padding, then 24 wide buttons separated by 3 of
        // margin. Any slack here shows up as dead background beside the last button.
        private const float PanelWidth = 8f + 3f + 24f + 3f + 24f + 3f + 8f;
        private const float PanelHeight = 8f + 3f + 24f + 3f + 8f;
        private const float PanelMargin = 20f;

        private Rect _panel;
        private bool _panelPlaced;
        private string _hoverTip;

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
        }

        /// <summary>
        /// Size proportional to distance, which holds a roughly constant size on screen. A fixed
        /// world size is a pinhead on a brig when zoomed out and swallows a dinghy when zoomed in.
        /// </summary>
        private static float MarkerSize(Vector3 eye, Vector3 point)
        {
            return Mathf.Clamp(Vector3.Distance(eye, point) * 0.030f, 0.30f, 3.0f);
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

            // The offset that IS the answer, in metres, along the boat's own fore-aft axis.
            // Dividing the yaw moment by the side force turns a size-dependent torque into a lever
            // arm, which compares honestly between a dinghy and a brig.
            float lever = Mathf.Abs(balance.TotalSideForce) > 0.01f
                ? balance.YawMoment / balance.TotalSideForce
                : 0f;
            _helmMetres = -lever;               // positive = CE aft of CLR = weather helm
            _unreliable = balance.AnySailHasFrozenCe;

            EnsureVisuals();

            // Held in the boat's frame, because the shipyard lifts the boat after admitting it and
            // keeps moving it while an order is being fitted.
            _boat = boat.transform;
            _local[Effort] = _boat.InverseTransformPoint(balance.CombinedCentreOfEffort);
            _local[Resistance] = _boat.InverseTransformPoint(balance.HullOrigin);

            Repaint(Effort, _unreliable ? UnreliableColour : EffortColour);
            UpdateLinkColour();
            SetValid(true);

            string verdict = Mathf.Abs(_helmMetres) < BalancedMetres ? "balanced"
                : (_helmMetres > 0f ? "weather helm" : "lee helm");
            Plugin.Log.LogInfo($"[Gizmo] {balance.Sails.Count} sail(s); CE offset "
                + $"{_helmMetres:F2} m ({verdict}){(_unreliable ? "; a sail's CE does not track its size" : "")}");
        }

        private void SetValid(bool valid)
        {
            _valid = valid;
            for (int i = 0; i < _markers.Length; i++)
            {
                if (_markers[i] != null) _markers[i].SetActive(valid && _visible[i]);
            }
            if (_link != null) _link.enabled = valid && _visible[Effort] && _visible[Resistance];
            if (_overlay != null && !valid) _overlay.enabled = false;
        }

        private void UpdateLinkColour()
        {
            Color colour = Mathf.Abs(_helmMetres) < BalancedMetres ? BalancedColour
                : (_helmMetres > 0f ? WeatherHelmColour : LeeHelmColour);
            if (_link == null) return;
            _link.startColor = colour;
            _link.endColor = colour;
            Tint(_linkMaterial, colour);
        }

        // ------------------------------------------------------------------ visuals

        private void EnsureVisuals()
        {
            if (_root != null) return;

            _root = new GameObject("RigBalanceGizmo");
            _layer = ResolveMarkerLayer();

            MakeMarker(Effort, "CentreOfEffort", EffortColour);
            MakeMarker(Resistance, "CentreOfLateralResistance", ResistanceColour);

            MakeLink();
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

        /// <summary>
        /// A camera that clears depth and draws only the marker layer, so the markers sit on top of
        /// the hull instead of inside it. It copies the live camera every frame rather than being
        /// parented to it, because the shipyard swaps cameras and a parented object would be
        /// orphaned or dragged along with the wrong one.
        /// </summary>
        private void MakeOverlayCamera()
        {
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
            _valid = false;
            _source = null;
            _overlay = null;
            _link = null;
            _linkMaterial = null;
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
        /// It sits in the top right corner, clear of the shipyard's own panels, and it is
        /// draggable. Text never goes inside it: a tooltip long enough to be useful would either
        /// pad the panel out with dead background or wrap into an unreadable column, so hover text
        /// and the unreliable-marker warning are drawn underneath it instead.
        /// </summary>
        private void OnGUI()
        {
            if (!ShipyardHooks.InShipyard || !_valid) return;
            EnsureStyles();

            if (!_panelPlaced)
            {
                _panel = new Rect(Screen.width - PanelWidth - PanelMargin, PanelMargin,
                    PanelWidth, PanelHeight);
                _panelPlaced = true;
            }

            _hoverTip = null;
            _panel = GUI.Window(PanelWindowId, _panel, DrawPanel, GUIContent.none, _panelStyle);

            // Keep it reachable if the window is resized or the game changes resolution.
            _panel.x = Mathf.Clamp(_panel.x, 0f, Mathf.Max(0f, Screen.width - _panel.width));
            _panel.y = Mathf.Clamp(_panel.y, 0f, Mathf.Max(0f, Screen.height - _panel.height));

            // The one case where silence would be dishonest: a sail whose centre of effort does not
            // follow its own size makes the magenta marker wrong, not merely imprecise.
            string note = _unreliable
                ? "A sail's centre of effort does not follow its size. The magenta marker is unreliable."
                : _hoverTip;
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
            GUILayout.BeginHorizontal();
            Toggle(Effort, _unreliable ? UnreliableColour : EffortColour, "Centre of effort");
            Toggle(Resistance, ResistanceColour, "Centre of lateral resistance");
            GUILayout.EndHorizontal();

            // Captured here rather than read after the window returns, because GUI.tooltip is only
            // populated while the hovered control is being drawn.
            if (!string.IsNullOrEmpty(GUI.tooltip)) _hoverTip = GUI.tooltip;

            // Last, so the buttons above have already taken their clicks. Everything else drags.
            GUI.DragWindow();
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

            // A window rather than a box, purely to inherit IMGUI's dragging. The default window
            // style reserves room for a title bar the panel does not have, so padding is reset.
            _panelStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(8, 8, 8, 8) };
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
