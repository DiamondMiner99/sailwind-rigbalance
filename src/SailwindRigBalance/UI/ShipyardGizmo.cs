using System;
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
    ///   yellow   centre of mass        the hull's own balance point
    ///
    /// The answer is the fore-and-aft offset between magenta and blue, and nothing else. Magenta
    /// aft of blue is weather helm, magenta forward is lee helm. A coloured link between the two
    /// carries that verdict so it reads from any camera angle without a number on screen.
    ///
    /// Three rendering decisions, all of them load-bearing:
    ///  - markers are UNLIT. A lit sphere is invisible at night and changes colour with the time of
    ///    day, which makes a colour-coded readout useless. An unlit shader is searched for by name
    ///    and, if this build ships none of them, Standard with black albedo and emission is the
    ///    fallback, which self-lights to the same effect.
    ///  - markers are drawn by a second camera that clears depth only, so they show THROUGH the
    ///    hull. Two of the three sit inside the boat; without this the user simply sees nothing.
    ///  - the marker objects are created once per shipyard session and only ever MOVED. Rebuilding
    ///    them was what produced a screenful of spheres: applying a customization destroys and
    ///    re-instantiates every sail, Unity defers Object.Destroy to end of frame, and the collision
    ///    sweep calls UpdateOrder repeatedly. A fixed pool of three cannot express that bug.
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

        private const int Effort = 0, Resistance = 1, Mass = 2;

        private static readonly Color EffortColour = new Color(1.00f, 0.25f, 0.90f);
        private static readonly Color ResistanceColour = new Color(0.25f, 0.60f, 1.00f);
        private static readonly Color MassColour = new Color(1.00f, 0.92f, 0.20f);
        private static readonly Color UnreliableColour = new Color(1.00f, 0.15f, 0.15f);

        private static readonly Color WeatherHelmColour = new Color(1.00f, 0.35f, 0.20f);
        private static readonly Color LeeHelmColour = new Color(0.30f, 0.80f, 1.00f);
        private static readonly Color BalancedColour = new Color(0.35f, 1.00f, 0.45f);

        private readonly GameObject[] _markers = new GameObject[3];
        private readonly Material[] _materials = new Material[3];
        private readonly bool[] _visible = { true, true, true };

        private GameObject _root;
        private LineRenderer _link;
        private Material _linkMaterial;

        private Camera _overlay, _source;
        private int _layer = -1;
        private Shader _unlit;
        private bool _shaderResolved;

        private bool _valid;
        private bool _unreliable;
        private float _helmMetres;      // positive = weather helm, in metres of CE offset

        private float _rebuildAt = -1f;

        private GUIStyle _circleStyle, _tipStyle;
        private Texture2D _circleTex;

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
        /// Camera follow and size hold happen after everything else has moved for the frame, so the
        /// overlay never trails the shipyard's orbit camera by a frame.
        /// </summary>
        private void LateUpdate()
        {
            if (_root == null || !_valid) return;

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

            // Hold a roughly constant on-screen size. A fixed world size is a pinhead on a brig
            // when zoomed out and swallows a dinghy when zoomed in.
            Vector3 eye = source.transform.position;
            for (int i = 0; i < _markers.Length; i++)
            {
                if (_markers[i] == null || !_markers[i].activeSelf) continue;
                float size = MarkerSize(eye, _markers[i].transform.position);
                _markers[i].transform.localScale = Vector3.one * size;
            }

            if (_link != null && _link.enabled)
            {
                float width = MarkerSize(eye, _link.GetPosition(0)) * 0.18f;
                _link.startWidth = width;
                _link.endWidth = width;
            }
        }

        private static float MarkerSize(Vector3 eye, Vector3 point)
        {
            return Mathf.Clamp(Vector3.Distance(eye, point) * 0.028f, 0.30f, 3.0f);
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

            var body = boat.GetComponent<Rigidbody>();
            Vector3 centreOfMass = body != null ? body.worldCenterOfMass : balance.HullOrigin;

            // The offset that IS the answer, in metres, taken along the boat's own fore-aft axis.
            // Dividing the yaw moment by the side force turns a size-dependent torque into a lever
            // arm, which compares honestly between a dinghy and a brig.
            float lever = Mathf.Abs(balance.TotalSideForce) > 0.01f
                ? balance.YawMoment / balance.TotalSideForce
                : 0f;
            _helmMetres = -lever;               // positive = CE aft of CLR = weather helm
            _unreliable = balance.AnySailHasFrozenCe;

            EnsureVisuals();
            SetValid(true);

            Place(Effort, balance.CombinedCentreOfEffort);
            Place(Resistance, balance.HullOrigin);
            Place(Mass, centreOfMass);

            SetColour(Effort, _unreliable ? UnreliableColour : EffortColour);
            UpdateLink(balance.HullOrigin, balance.CombinedCentreOfEffort);

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

        private void Place(int index, Vector3 worldPosition)
        {
            if (_markers[index] != null) _markers[index].transform.position = worldPosition;
        }

        private void SetColour(int index, Color colour)
        {
            if (_materials[index] != null) Tint(_materials[index], colour);
        }

        private void UpdateLink(Vector3 from, Vector3 to)
        {
            if (_link == null) return;

            _link.SetPosition(0, from);
            _link.SetPosition(1, to);

            Color colour = Mathf.Abs(_helmMetres) < BalancedMetres ? BalancedColour
                : (_helmMetres > 0f ? WeatherHelmColour : LeeHelmColour);
            _link.startColor = colour;
            _link.endColor = colour;
            if (_linkMaterial != null) Tint(_linkMaterial, colour);
        }

        // ------------------------------------------------------------------ visuals

        private void EnsureVisuals()
        {
            if (_root != null) return;

            _root = new GameObject("RigBalanceGizmo");
            _layer = ResolveMarkerLayer();

            _markers[Effort] = MakeMarker("CentreOfEffort", EffortColour, Effort);
            _markers[Resistance] = MakeMarker("CentreOfLateralResistance", ResistanceColour, Resistance);
            _markers[Mass] = MakeMarker("CentreOfMass", MassColour, Mass);

            MakeLink();
            MakeOverlayCamera();
        }

        private GameObject MakeMarker(string name, Color colour, int index)
        {
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;

                var collider = go.GetComponent<Collider>();
                if (collider != null) Destroy(collider);

                go.layer = _layer;
                go.transform.SetParent(_root.transform, false);
                go.SetActive(false);

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    _materials[index] = MakeMaterial(colour);
                    renderer.material = _materials[index];
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                }

                return go;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Gizmo] marker '{name}' failed: {e.Message}");
                return null;
            }
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
                _link.positionCount = 2;
                _link.numCapVertices = 4;
                _link.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _link.receiveShadows = false;
                _link.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                _link.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                _linkMaterial = MakeMaterial(BalancedColour);
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

        private Material MakeMaterial(Color colour)
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
            }
            else
            {
                // No unlit shader is present in this build. Standard with a black albedo and the
                // colour in emission self-lights to the same result: it reads identically at
                // midnight and at noon, which is the whole requirement.
                material = new Material(Shader.Find("Standard"));
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            Tint(material, colour);
            return material;
        }

        private void Tint(Material material, Color colour)
        {
            if (material == null) return;
            if (_unlit != null)
            {
                if (material.HasProperty("_Color")) material.SetColor("_Color", colour);
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", colour);
            }
            else
            {
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.black);
                material.SetColor("_EmissionColor", colour);
            }
        }

        /// <summary>
        /// Searched by name and in order, because a Unity build only contains the shaders something
        /// in it actually references. Every candidate here is unlit and tintable; which one exists
        /// is a property of this build, not something to assume.
        /// </summary>
        private static Shader FindUnlitShader()
        {
            string[] candidates =
            {
                "Unlit/Color",
                "Sprites/Default",
                "UI/Default",
                "Particles/Standard Unlit",
                "GUI/Text Shader",
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
            for (int i = 0; i < _markers.Length; i++)
            {
                _markers[i] = null;
                _materials[i] = null;
            }

            if (_root != null)
            {
                Destroy(_root);
                _root = null;
            }
        }

        // ------------------------------------------------------------------ UI

        /// <summary>
        /// Three round buttons and nothing else. The buttons carry the marker colours, so the strip
        /// doubles as the legend; hovering one names it. No readout, by design: the gizmo is the
        /// output, and a number on screen just invites reading the number instead of the rig.
        /// </summary>
        private void OnGUI()
        {
            if (!ShipyardHooks.InShipyard || !_valid) return;
            EnsureStyles();

            GUILayout.BeginArea(new Rect(18f, 18f, 132f, _unreliable ? 78f : 40f));
            GUILayout.BeginHorizontal();
            Toggle(Effort, _unreliable ? UnreliableColour : EffortColour, "Centre of effort");
            Toggle(Resistance, ResistanceColour, "Centre of lateral resistance");
            Toggle(Mass, MassColour, "Centre of mass");
            GUILayout.EndHorizontal();

            // The one case where silence would be dishonest: a sail whose centre of effort does not
            // follow its own size makes the magenta marker wrong, not merely imprecise.
            if (_unreliable)
                GUILayout.Label("A sail's centre of effort does not\nfollow its size. Marker unreliable.", _tipStyle);
            else if (!string.IsNullOrEmpty(GUI.tooltip))
                GUILayout.Label(GUI.tooltip, _tipStyle);

            GUILayout.EndArea();
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
            _circleStyle = new GUIStyle(GUI.skin.button) { padding = new RectOffset(0, 0, 0, 0) };
            _circleStyle.normal.background = _circleTex;
            _circleStyle.hover.background = _circleTex;
            _circleStyle.active.background = _circleTex;

            _tipStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
            _tipStyle.normal.textColor = Color.white;
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
