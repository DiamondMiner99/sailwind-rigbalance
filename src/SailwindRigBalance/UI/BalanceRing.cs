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
        private float _radius = 10f;

        private GameObject _arrowGo;
        private Mesh _arrowMesh;
        private bool _dragging;
        private bool _angleChanged;

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
                float radius = _radius;
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
                UpdateArrow(boat);
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

            // Grey rather than a verdict. Downwind the side force vanishes and the helm ratio is
            // meaningless, which is what painted the whole ring red.
            if (!balance.HelmMeaningful) return DeadColour;

            float lever = balance.YawMoment / balance.TotalSideForce;
            float helm = -lever;    // positive = weather

            if (Mathf.Abs(helm) < balancedMetres) return ShipyardGizmo.BalancedColour;
            return helm > 0f ? ShipyardGizmo.WeatherHelmColour : ShipyardGizmo.LeeHelmColour;
        }

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
