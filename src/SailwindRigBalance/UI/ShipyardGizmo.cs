using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SailwindRigBalance.Core;
using SailwindRigBalance.Shipyard;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// Draws the rig balance in the shipyard: a world-space marker per sail centre of effort, one
    /// for the combined CE, one for the hull origin (where the hull's lateral resistance acts), and
    /// an on-screen panel with the signed helm number.
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

        private readonly List<GameObject> _markers = new List<GameObject>();
        private readonly List<KeyValuePair<Vector3, string>> _labels = new List<KeyValuePair<Vector3, string>>();

        private RigBalance _closeHauled;
        private bool _valid;
        private string _panelText = "";

        private GUIStyle _panelStyle;
        private GUIStyle _labelStyle;

        private void Update()
        {
            if (!ShipyardHooks.InShipyard)
            {
                if (_markers.Count > 0) Clear();
                return;
            }

            // Recompute only on a configuration change, never per frame. The change events are the
            // shipyard's own, so an idle shipyard costs nothing.
            if (ShipyardHooks.ConsumeDirty()) Recompute();
        }

        private void Recompute()
        {
            Clear();

            GameObject boat = ShipyardHooks.CurrentBoat;
            if (boat == null) return;

            if (!RigModel.Available)
            {
                _panelText = "Rig Balance: the game's sail force fields did not resolve.\n"
                    + "Sailwind has probably changed shape; this readout is disabled.";
                _valid = true;
                return;
            }

            RigBalance beam;
            _valid = RigModel.TryEvaluate(boat, CloseHauledDegrees, out _closeHauled);
            bool beamOk = RigModel.TryEvaluate(boat, BeamReachDegrees, out beam);

            if (!_valid)
            {
                _panelText = "Rig Balance: no sails on this boat.";
                return;
            }

            BuildMarkers(_closeHauled);
            BuildPanel(_closeHauled, beam, beamOk);
        }

        private void BuildMarkers(RigBalance balance)
        {
            foreach (SailContribution c in balance.Sails)
            {
                // Size carries the magnitude of that sail's yaw contribution so the dominant sails
                // read at a glance; colour carries the direction it pushes the bow.
                float mag = Mathf.Abs(c.YawMoment);
                float size = Mathf.Clamp(0.35f + Mathf.Sqrt(mag) * 0.012f, 0.35f, 1.6f);
                Color colour = c.YawMoment < 0f
                    ? new Color(0.30f, 0.75f, 1.00f)    // pushes the bow toward the wind
                    : new Color(1.00f, 0.55f, 0.20f);   // pushes it away
                if (!c.CeTracksResizes) colour = new Color(1f, 0.15f, 0.15f);
                AddMarker(c.CentreOfEffort, size, colour);
            }

            AddMarker(balance.CombinedCentreOfEffort, 1.9f, new Color(1f, 0.92f, 0.35f));
            AddMarker(balance.HullOrigin, 1.9f, new Color(0.45f, 1f, 0.55f));

            _labels.Clear();
            _labels.Add(new KeyValuePair<Vector3, string>(balance.CombinedCentreOfEffort, "CE"));
            _labels.Add(new KeyValuePair<Vector3, string>(balance.HullOrigin, "CLR"));
        }

        private void AddMarker(Vector3 worldPos, float size, Color colour)
        {
            try
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "RigBalanceMarker";
                var collider = go.GetComponent<Collider>();
                if (collider != null) Destroy(collider);

                go.layer = 0;                       // the orbit camera renders layer 0
                go.transform.position = worldPos;
                go.transform.localScale = Vector3.one * size;

                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var material = new Material(Shader.Find("Standard"));
                    material.color = colour;
                    // Push it past opaque geometry so a marker inside the hull is still readable.
                    material.renderQueue = 3050;
                    renderer.material = material;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }

                _markers.Add(go);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Gizmo] marker failed: " + e.Message);
            }
        }

        private void BuildPanel(RigBalance close, RigBalance beam, bool beamOk)
        {
            var sb = new StringBuilder();
            sb.AppendLine("RIG BALANCE");
            sb.AppendLine();
            sb.AppendLine(Fmt("close hauled   {0,10} Nm   {1}", close.SignedHelm, Verdict(close.SignedHelm)));
            if (beamOk)
                sb.AppendLine(Fmt("beam reach     {0,10} Nm   {1}", beam.SignedHelm, Verdict(beam.SignedHelm)));
            sb.AppendLine();

            float lever = Mathf.Abs(close.TotalSideForce) > 0.01f
                ? close.YawMoment / close.TotalSideForce
                : 0f;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "CE is {0:F2} m {1} of the hull origin", Mathf.Abs(lever), lever >= 0f ? "forward" : "aft"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0} sails, side force {1:F0} N at {2:F0} m/s apparent",
                close.Sails.Count, close.TotalSideForce, RigModel.SyntheticWindSpeed));

            if (close.AnySailHasFrozenCe)
            {
                sb.AppendLine();
                sb.AppendLine("WARNING: a sail's centre of effort does not follow its size.");
                sb.AppendLine("Its marker is red. The number for that sail is unreliable.");
            }

            _panelText = sb.ToString().TrimEnd();
        }

        private static string Verdict(float signedHelm)
        {
            if (signedHelm > 500f) return "weather helm";
            if (signedHelm < -500f) return "lee helm";
            return "balanced";
        }

        private static string Fmt(string format, float value, string verdict) =>
            string.Format(CultureInfo.InvariantCulture, format, value.ToString("F0", CultureInfo.InvariantCulture), verdict);

        private void Clear()
        {
            foreach (GameObject go in _markers)
            {
                if (go != null) Destroy(go);
            }
            _markers.Clear();
            _labels.Clear();
            _valid = false;
            _panelText = "";
        }

        private void OnGUI()
        {
            if (!ShipyardHooks.InShipyard || string.IsNullOrEmpty(_panelText)) return;

            EnsureStyles();

            var size = _panelStyle.CalcSize(new GUIContent(_panelText));
            var rect = new Rect(18f, 18f, size.x + 24f, size.y + 18f);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 12f, rect.y + 9f, size.x, size.y), _panelText, _panelStyle);

            Camera cam = Camera.main;
            if (cam == null || !_valid) return;

            foreach (var label in _labels)
            {
                Vector3 screen = cam.WorldToScreenPoint(label.Key);
                if (screen.z < 0f) continue;                       // behind the camera
                screen.y = Screen.height - screen.y;               // GUI space is Y-flipped
                var content = new GUIContent(label.Value);
                var s = _labelStyle.CalcSize(content);
                GUI.Label(new Rect(screen.x - s.x * 0.5f, screen.y - s.y - 14f, s.x, s.y), content, _labelStyle);
            }
        }

        private void EnsureStyles()
        {
            if (_panelStyle != null) return;
            _panelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = false,
                alignment = TextAnchor.UpperLeft,
            };
            _panelStyle.normal.textColor = Color.white;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
            _labelStyle.normal.textColor = Color.white;
        }
    }
}
