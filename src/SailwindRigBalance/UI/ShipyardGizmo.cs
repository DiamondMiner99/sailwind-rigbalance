using System;
using System.Collections.Generic;
using System.Globalization;
using SailwindRigBalance.Core;
using SailwindRigBalance.Shipyard;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// Draws the rig balance in the shipyard: a world-space marker per sail centre of effort, one
    /// for the combined CE, one for the hull origin (where the hull's lateral resistance acts), and
    /// a panel with the signed helm number and a round show/hide button per marker.
    ///
    /// Rendering choices, both with working precedent in this game:
    ///  - markers are primitive meshes with Shader.Find("Standard") forced onto layer 0, which the
    ///    shipyard's orbit camera provably renders (its measured culling mask includes layer 0);
    ///  - text is IMGUI anchored with Camera.main.WorldToScreenPoint rather than a Canvas, because
    ///    the game ships no gameplay uGUI canvas and no TextMeshPro at all.
    ///
    /// Nothing is parented to a Sail: Sail.UpdateInstallPosition toggles the sail GameObject off and
    /// on again on every add and every move, which would kill any child component's coroutines.
    /// </summary>
    public class ShipyardGizmo : MonoBehaviour
    {
        private const float CloseHauledDegrees = 45f;
        private const float BeamReachDegrees = 90f;

        private static readonly Color TowardWind = new Color(0.30f, 0.75f, 1.00f);
        private static readonly Color AwayFromWind = new Color(1.00f, 0.55f, 0.20f);
        private static readonly Color FrozenCe = new Color(1.00f, 0.15f, 0.15f);
        private static readonly Color CeColour = new Color(1.00f, 0.92f, 0.35f);
        private static readonly Color ClrColour = new Color(0.45f, 1.00f, 0.55f);

        private readonly List<GameObject> _sailMarkers = new List<GameObject>();
        private readonly List<bool> _sailVisible = new List<bool>();
        private readonly List<Color> _sailColours = new List<Color>();
        private GameObject _ceMarker, _clrMarker, _container;
        private bool _ceVisible = true, _clrVisible = true;

        private RigBalance _closeHauled, _beamReach;
        private bool _valid, _beamValid;
        private string _status;

        private GUIStyle _textStyle, _headingStyle, _circleStyle;
        private Texture2D _circleTex;

        /// <summary>
        /// Seconds to wait after the last change event before rebuilding.
        ///
        /// This is not a performance tweak, it is a correctness one. Applying a customization
        /// destroys and re-instantiates every sail, and Unity defers Object.Destroy to the end of
        /// the frame, so a Mast.sails enumerated in the same frame still contains the OLD sails
        /// alongside the new ones. Rebuilding immediately therefore drew a marker for every dead
        /// sail too. The collision sweep also calls UpdateOrder repeatedly over many frames, so
        /// waiting for the events to settle collapses all of that into one rebuild.
        /// </summary>
        private const float RebuildDelay = 0.25f;

        private float _rebuildAt = -1f;

        private void Update()
        {
            if (!ShipyardHooks.InShipyard)
            {
                if (_sailMarkers.Count > 0 || _ceMarker != null) Clear();
                _rebuildAt = -1f;
                return;
            }

            // Recompute only on a configuration change, never per frame, and only once the
            // changes have stopped arriving.
            if (ShipyardHooks.ConsumeDirty()) _rebuildAt = Time.unscaledTime + RebuildDelay;

            if (_rebuildAt > 0f && Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = -1f;
                Recompute();
            }
        }

        private void Recompute()
        {
            // Remember what was hidden so an edit does not silently un-hide everything.
            List<bool> previousVisibility = new List<bool>(_sailVisible);

            ClearMarkers();
            _status = null;

            GameObject boat = ShipyardHooks.CurrentBoat;
            if (boat == null) return;

            if (!RigModel.Available)
            {
                _status = "The game's sail force fields did not resolve.\n"
                    + "Sailwind has probably changed shape; this readout is disabled.";
                return;
            }

            _valid = RigModel.TryEvaluate(boat, CloseHauledDegrees, out _closeHauled);
            _beamValid = RigModel.TryEvaluate(boat, BeamReachDegrees, out _beamReach);

            if (!_valid)
            {
                _status = "No sails on this boat yet.";
                return;
            }

            BuildMarkers(_closeHauled, previousVisibility);
        }

        private void BuildMarkers(RigBalance balance, List<bool> previousVisibility)
        {
            for (int i = 0; i < balance.Sails.Count; i++)
            {
                SailContribution c = balance.Sails[i];

                // Size carries the magnitude of that sail's yaw contribution so the dominant sails
                // read at a glance; colour carries the direction it pushes the bow.
                float size = Mathf.Clamp(0.35f + Mathf.Sqrt(Mathf.Abs(c.YawMoment)) * 0.012f, 0.35f, 1.6f);
                Color colour = c.CeTracksResizes
                    ? (c.YawMoment < 0f ? TowardWind : AwayFromWind)
                    : FrozenCe;

                bool visible = i < previousVisibility.Count ? previousVisibility[i] : true;
                GameObject marker = MakeMarker(c.CentreOfEffort, size, colour);
                if (marker != null) marker.SetActive(visible);

                _sailMarkers.Add(marker);
                _sailVisible.Add(visible);
                _sailColours.Add(colour);
            }

            _ceMarker = MakeMarker(balance.CombinedCentreOfEffort, 1.9f, CeColour);
            _clrMarker = MakeMarker(balance.HullOrigin, 1.9f, ClrColour);
            if (_ceMarker != null) _ceMarker.SetActive(_ceVisible);
            if (_clrMarker != null) _clrMarker.SetActive(_clrVisible);

            Plugin.Log.LogInfo($"[Gizmo] rebuilt: {balance.Sails.Count} sail(s), "
                + $"{(_container == null ? 0 : _container.transform.childCount)} marker(s) total");
        }

        /// <summary>
        /// All markers live under one container we own, so a rebuild can guarantee nothing survives
        /// it regardless of what went wrong last time.
        /// </summary>
        private GameObject Container()
        {
            if (_container == null)
            {
                _container = new GameObject("RigBalanceMarkers");
                _container.transform.position = Vector3.zero;
            }
            return _container;
        }

        private GameObject MakeMarker(Vector3 worldPos, float size, Color colour)
        {
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "RigBalanceMarker";
                var collider = go.GetComponent<Collider>();
                if (collider != null) Destroy(collider);

                go.layer = 0;                       // the orbit camera renders layer 0
                go.transform.SetParent(Container().transform, false);
                go.transform.position = worldPos;
                go.transform.localScale = Vector3.one * size;

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    Shader shader = Shader.Find("Standard");
                    var material = shader != null ? new Material(shader) : new Material(renderer.material);
                    material.color = colour;
                    renderer.material = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }

                return go;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] marker failed: " + e.Message);
                return null;
            }
        }

        private void ClearMarkers()
        {
            _sailMarkers.Clear();
            _sailVisible.Clear();
            _sailColours.Clear();
            _ceMarker = null;
            _clrMarker = null;
            _valid = false;
            _beamValid = false;

            // Destroy the whole container rather than tracked references. If anything ever leaks a
            // marker, it still goes, because it cannot be anywhere else.
            if (_container != null)
            {
                Destroy(_container);
                _container = null;
            }
        }

        private void Clear()
        {
            ClearMarkers();
            _status = null;
        }

        // ---------------------------------------------------------------- UI

        private void OnGUI()
        {
            if (!ShipyardHooks.InShipyard) return;
            EnsureStyles();

            float width = 340f;
            float height = _valid ? 190f + _sailMarkers.Count * 26f : 96f;
            GUILayout.BeginArea(new Rect(18f, 18f, width, height), GUI.skin.box);

            GUILayout.Label("RIG BALANCE", _headingStyle);

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Label(_status, _textStyle);
                GUILayout.EndArea();
                return;
            }

            if (!_valid)
            {
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label(Row("close hauled", _closeHauled.SignedHelm), _textStyle);
            if (_beamValid) GUILayout.Label(Row("beam reach", _beamReach.SignedHelm), _textStyle);

            float lever = Mathf.Abs(_closeHauled.TotalSideForce) > 0.01f
                ? _closeHauled.YawMoment / _closeHauled.TotalSideForce
                : 0f;
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "CE sits {0:F2} m {1} of the CLR", Mathf.Abs(lever), lever >= 0f ? "forward" : "aft"), _textStyle);

            GUILayout.Space(6f);

            GUILayout.BeginHorizontal();
            _ceVisible = CircleToggle(_ceVisible, CeColour);
            GUILayout.Label("CE", _textStyle, GUILayout.Width(34f));
            _clrVisible = CircleToggle(_clrVisible, ClrColour);
            GUILayout.Label("CLR", _textStyle, GUILayout.Width(40f));
            if (GUILayout.Button(AllShown() ? "hide all" : "show all", GUILayout.Width(70f))) ToggleAll();
            GUILayout.EndHorizontal();
            if (_ceMarker != null) _ceMarker.SetActive(_ceVisible);
            if (_clrMarker != null) _clrMarker.SetActive(_clrVisible);

            GUILayout.Space(4f);

            for (int i = 0; i < _sailMarkers.Count && i < _closeHauled.Sails.Count; i++)
            {
                SailContribution c = _closeHauled.Sails[i];
                GUILayout.BeginHorizontal();
                bool wasVisible = _sailVisible[i];
                bool nowVisible = CircleToggle(wasVisible, _sailColours[i]);
                if (nowVisible != wasVisible)
                {
                    _sailVisible[i] = nowVisible;
                    if (_sailMarkers[i] != null) _sailMarkers[i].SetActive(nowVisible);
                }
                GUILayout.Label(Truncate(c.Name, 22), _textStyle, GUILayout.Width(170f));
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "{0,8:F0} Nm", c.YawMoment), _textStyle);
                GUILayout.EndHorizontal();
            }

            if (_closeHauled.AnySailHasFrozenCe)
            {
                GUILayout.Space(4f);
                GUILayout.Label("Red: this sail's centre of effort does not\nfollow its size, so its number is unreliable.", _textStyle);
            }

            GUILayout.EndArea();
        }

        /// <summary>
        /// A small round button. Filled in the marker's own colour when shown, hollow grey when
        /// hidden, so the panel legend and the spheres read as the same thing.
        /// </summary>
        private bool CircleToggle(bool shown, Color colour)
        {
            Color previous = GUI.color;
            GUI.color = shown ? colour : new Color(0.35f, 0.35f, 0.35f, 0.8f);
            bool clicked = GUILayout.Button(GUIContent.none, _circleStyle,
                GUILayout.Width(18f), GUILayout.Height(18f));
            GUI.color = previous;
            return clicked ? !shown : shown;
        }

        private bool AllShown()
        {
            if (!_ceVisible || !_clrVisible) return false;
            foreach (bool v in _sailVisible)
            {
                if (!v) return false;
            }
            return true;
        }

        private void ToggleAll()
        {
            bool target = !AllShown();
            _ceVisible = target;
            _clrVisible = target;
            for (int i = 0; i < _sailVisible.Count; i++)
            {
                _sailVisible[i] = target;
                if (_sailMarkers[i] != null) _sailMarkers[i].SetActive(target);
            }
            if (_ceMarker != null) _ceMarker.SetActive(target);
            if (_clrMarker != null) _clrMarker.SetActive(target);
        }

        private static string Row(string label, float signedHelm)
        {
            string verdict = signedHelm > 500f ? "weather helm"
                : (signedHelm < -500f ? "lee helm" : "balanced");
            return string.Format(CultureInfo.InvariantCulture, "{0,-13}{1,9:F0} Nm   {2}",
                label, signedHelm, verdict);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(unnamed)";
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        private void OnDestroy()
        {
            ClearMarkers();
            if (_circleTex != null) Destroy(_circleTex);
        }

        private void EnsureStyles()
        {
            if (_textStyle != null) return;

            _textStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            _textStyle.normal.textColor = Color.white;

            _headingStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _headingStyle.normal.textColor = Color.white;

            _circleTex = MakeCircleTexture(24);
            _circleStyle = new GUIStyle(GUI.skin.button) { padding = new RectOffset(0, 0, 0, 0) };
            _circleStyle.normal.background = _circleTex;
            _circleStyle.hover.background = _circleTex;
            _circleStyle.active.background = _circleTex;
        }

        /// <summary>A filled white disc, tinted per button via GUI.color.</summary>
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
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // One pixel of feathering so the edge is not jagged.
                    float a = Mathf.Clamp01(r - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
