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
    /// Read it exactly like the markers: a ring that is green or red all the way round is a boat
    /// that behaves; a ring with blue in it is a boat that develops lee helm on some heading, and
    /// the coloured arc tells you which.
    ///
    /// Built as one mesh with per-segment vertex colours rather than a line renderer per segment.
    /// LineRenderer's gradient tops out at eight keys, far short of the resolution this needs, and
    /// two dozen renderers to draw one ring would be silly. The unlit shader the markers already
    /// use multiplies by vertex colour, so the same material works unchanged.
    /// </summary>
    public class BalanceRing
    {
        /// <summary>
        /// Segments around the full circle. Balance is symmetric port and starboard, so only half
        /// are evaluated and the rest mirrored: 24 segments costs 13 evaluations, not 24.
        /// </summary>
        private const int Segments = 24;

        /// <summary>
        /// Below this apparent wind angle Sail.cs:416 ramps every sail's force to nothing, so there
        /// is no balance to report and the ring says so in grey rather than inventing a verdict.
        /// </summary>
        private const float NoSailAngle = 16f;

        private static readonly Color DeadColour = new Color(0.45f, 0.45f, 0.45f, 0.55f);

        private GameObject _go;
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private Vector3 _centreLocal;

        public bool Exists => _go != null;

        public void SetVisible(bool visible)
        {
            if (_go != null && _go.activeSelf != visible) _go.SetActive(visible);
        }

        public void Destroy()
        {
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _mesh = null;
            _go = null;
            _filter = null;
            _renderer = null;
        }

        /// <summary>Keeps the ring on the boat without parenting to it, as the markers do.</summary>
        public void Follow(Transform boat)
        {
            if (_go == null || boat == null) return;
            _go.transform.SetPositionAndRotation(boat.TransformPoint(_centreLocal), boat.rotation);
        }

        /// <summary>
        /// Sweeps the apparent wind right round the boat and rebuilds the ring from the verdicts.
        /// Runs on the debounced rebuild, never per frame.
        /// </summary>
        public void Rebuild(GameObject boatRoot, Transform boat, RigBalance current,
            float balancedMetres, Material material, int layer, Transform parent)
        {
            try
            {
                if (_go == null) Create(material, layer, parent);
                if (_go == null) return;

                _centreLocal = boat.InverseTransformPoint(current.HullOrigin);

                float radius = Mathf.Max(current.HullLength * 0.55f, 3f);
                float band = Mathf.Max(current.HullLength * 0.030f, 0.25f);

                // Half the circle, mirrored. 0 is dead ahead, 180 dead astern.
                int half = Segments / 2;
                var colours = new Color[Segments];

                for (int i = 0; i <= half; i++)
                {
                    float angle = 180f * i / half;
                    Color colour = VerdictAt(boatRoot, angle, balancedMetres);

                    colours[i % Segments] = colour;
                    // Mirror onto the starboard half. Segment 0 and the one at 180 are on the
                    // centreline and have no mirror image, hence the guard.
                    int mirrored = (Segments - i) % Segments;
                    colours[mirrored] = colour;
                }

                BuildMesh(radius, band, colours);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Ring] rebuild failed: " + e.Message);
            }
        }

        private Color VerdictAt(GameObject boatRoot, float angle, float balancedMetres)
        {
            if (angle < NoSailAngle) return DeadColour;

            RigBalance balance;
            if (!RigModel.TryEvaluate(boatRoot, angle, out balance)) return DeadColour;

            if (Mathf.Abs(balance.TotalSideForce) < 0.01f) return DeadColour;

            float lever = balance.YawMoment / balance.TotalSideForce;
            float helm = -lever;    // positive = weather

            if (Mathf.Abs(helm) < balancedMetres) return ShipyardGizmo.BalancedColour;
            return helm > 0f ? ShipyardGizmo.WeatherHelmColour : ShipyardGizmo.LeeHelmColour;
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
        }

        /// <summary>
        /// One flat-coloured quad per segment, with its own four vertices so colours do not blend
        /// into each other. At 24 segments that is 96 vertices, which is nothing.
        /// </summary>
        private void BuildMesh(float radius, float band, Color[] colours)
        {
            float inner = radius - band * 0.5f;
            float outer = radius + band * 0.5f;

            var vertices = new Vector3[Segments * 4];
            var vertexColours = new Color[Segments * 4];
            var triangles = new int[Segments * 6];

            for (int i = 0; i < Segments; i++)
            {
                // Bearing the wind comes FROM. RigModel puts the wind on the port bow, so angle
                // grows to port: at 0 the source is dead ahead, at 90 it is on the port beam.
                float a0 = 360f * i / Segments * Mathf.Deg2Rad;
                float a1 = 360f * (i + 1) / Segments * Mathf.Deg2Rad;

                Vector3 d0 = new Vector3(-Mathf.Sin(a0), 0f, Mathf.Cos(a0));
                Vector3 d1 = new Vector3(-Mathf.Sin(a1), 0f, Mathf.Cos(a1));

                int v = i * 4;
                vertices[v + 0] = d0 * inner;
                vertices[v + 1] = d0 * outer;
                vertices[v + 2] = d1 * outer;
                vertices[v + 3] = d1 * inner;

                Color colour = colours[i];
                vertexColours[v + 0] = colour;
                vertexColours[v + 1] = colour;
                vertexColours[v + 2] = colour;
                vertexColours[v + 3] = colour;

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
    }
}
