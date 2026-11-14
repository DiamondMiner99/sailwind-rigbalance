using System;
using System.Collections.Generic;
using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// Where the sea would meet the hull in the wind the arrow sets, drawn as a line on the hull, with
    /// a red dot wherever water would come aboard.
    ///
    /// The boat itself never moves. The sea's surface is a plane, and heeling the boat in a level sea
    /// is the same picture as tilting the sea around a level boat, so the line is the hull cut by the
    /// solved sea plane, worked out in the boat's own frame. The cut is taken from the hull's own
    /// colliders, by the filter that measures the hull, so it is one clean loop round the outside of
    /// the hull rather than every plank and fitting the visible model is built from.
    ///
    /// The line eases toward a new attitude instead of jumping, so dragging the wind speed reads as
    /// the boat leaning into it. It is not animated beyond that: a static balance has no waves in it
    /// and the line does not pretend to.
    /// </summary>
    public class Waterline
    {
        /// <summary>Seconds for the line to cover most of the way to a new attitude.</summary>
        private const float EaseSeconds = 0.35f;

        /// <summary>Loops shorter than this are cuts through small fittings, not the hull, and are not drawn.</summary>
        private const float MinLoopMeters = 0.75f;
        private const int MaxLoops = 4;

        /// <summary>
        /// Loops shorter than this share of the longest one are dropped too. Solid fittings low on
        /// the hull pass the hull filter (the brig's anchor_winch has a non-trigger MeshCollider) and
        /// their cut is a small closed ring sitting inside the hull outline, a few meters round
        /// against about sixty for the hull. A hull whose cut breaks into several open pieces at a
        /// transom keeps them, because each piece is still a large share of the outline.
        /// </summary>
        private const float MinLoopShareOfLongest = 0.15f;

        private static readonly Color FloodColor = new Color(1f, 0.22f, 0.18f, 1f);

        private readonly List<Vector3[]> _vertices = new List<Vector3[]>();   // boat frame, meters
        private readonly List<int[]> _triangles = new List<int[]>();
        private readonly List<Vector3> _zones = new List<Vector3>();          // splash points, boat frame
        private readonly List<float> _zoneOffsets = new List<float>();

        private readonly List<LineRenderer> _lines = new List<LineRenderer>();
        private readonly List<List<Vector3>> _loops = new List<List<Vector3>>();


        private Transform _parent;
        private Material _lineMaterial;
        private int _layer;

        private SeaAttitude _target, _shown;
        private bool _hasShown;
        private bool _dirty;
        private bool _visible;
        private bool _meshesLogged;

        /// <summary>A point on the low side of the waterline for the heel label, in boat-frame meters.</summary>
        public Vector3 LabelAnchorLocal { get; private set; }

        /// <summary>The attitude being drawn right now, which trails the target while it eases.</summary>
        public SeaAttitude Shown => _shown;

        public bool Visible => _visible && _hasShown && _loops.Count > 0;

        public void Attach(Transform parent, int layer, Material lineMaterial)
        {
            _parent = parent;
            _layer = layer;
            _lineMaterial = lineMaterial;
        }

        /// <summary>
        /// Reads the hull's shape and splash points. Called when the rig or the boat changes, never
        /// per frame: the hull meshes are copied out here once and only cut afterwards.
        /// </summary>
        public void Measure(GameObject boatRoot)
        {
            _vertices.Clear();
            _triangles.Clear();
            _zones.Clear();
            _zoneOffsets.Clear();
            DestroyProbes();
            _castBoat = null;
            _dirty = true;
            if (boatRoot == null) return;

            Transform boat = boatRoot.transform;
            int used = 0, unreadable = 0;
            var names = new List<string>();
            var unreadableColliders = new List<MeshCollider>();

            // The colliders that are switched on first. Only when none of them gives a hull, the ones
            // switched off in the scene: the large junk's hull colliders are all on inactive objects
            // (level24 dump 2026-11-14), and what is on is four 20 cm rope holes. A collider's shape
            // and pose are there whether its object is on or not, and nothing here switches it on.
            bool fromSwitchedOff = false;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    if (used > 0 || HasPlausibleArea(unreadableColliders, boat)) break;
                    fromSwitchedOff = true;
                }

                foreach (Collider collider in RigModel.HullColliders(boatRoot, switchedOffOnly: pass == 1))
                {
                    Mesh mesh = MeshOf(collider);
                    if (mesh == null) continue;
                    // On both passes: an infinite sea plane cuts a stray piece kilometers off just as
                    // readily as the hull, and its loops would outweigh hers.
                    if (!Plausible(collider as MeshCollider, boat)) continue;
                    if (!mesh.isReadable)
                    {
                        unreadable++;
                        var meshCollider = collider as MeshCollider;
                        if (meshCollider != null) unreadableColliders.Add(meshCollider);
                        continue;
                    }

                    try
                    {
                        Vector3[] source = mesh.vertices;
                        int[] triangles = mesh.triangles;
                        if (source.Length == 0 || triangles.Length < 3) continue;

                        Transform frame = collider.transform;
                        var local = new Vector3[source.Length];
                        for (int i = 0; i < source.Length; i++)
                            local[i] = boat.InverseTransformDirection(frame.TransformPoint(source[i]) - boat.position);

                        _vertices.Add(local);
                        _triangles.Add(triangles);
                        used++;
                        if (names.Count < 4) names.Add(collider.name);
                    }
                    catch (Exception e)
                    {
                        unreadable++;
                        Plugin.Log.LogDebug($"[Waterline] '{collider.name}' could not be read: {e.Message}");
                    }
                }
            }
            if (fromSwitchedOff)
                Plugin.Log.LogInfo($"[Waterline] '{boatRoot.name}': no hull collider is switched on, so the waterline "
                    + "is taken from the hull colliders switched off in the scene.");

            try
            {
                foreach (WaveSplashZone zone in boatRoot.GetComponentsInChildren<WaveSplashZone>())
                {
                    if (zone == null || !zone.isActiveAndEnabled) continue;
                    _zones.Add(boat.InverseTransformDirection(zone.transform.position - boat.position));
                    _zoneOffsets.Add(zone.verticalOffset);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("[Waterline] splash points could not be read: " + e.Message);
            }

            // No mesh to cut, so trace the outline with rays instead. Physics can hit a collider whose
            // vertices a mod is not allowed to read, which is every hull collider on the large junk.
            if (used == 0) PrepareCast(boat, unreadableColliders);

            if (!_meshesLogged)
            {
                _meshesLogged = true;
                if (used == 0 && _cast.Count > 0)
                    Plugin.Log.LogInfo($"[Waterline] no readable hull mesh ({unreadable} unreadable), so the "
                        + $"waterline is traced with rays against {_cast.Count} hull collider(s) instead; "
                        + $"{_zones.Count} splash point(s).");
                else if (used == 0)
                    Plugin.Log.LogWarning($"[Waterline] no readable hull mesh ({unreadable} unreadable) and none "
                        + "switched on to trace, so the waterline is not drawn. The heel is still in the helm reading.");
                else
                    Plugin.Log.LogInfo($"[Waterline] cut from {used} hull mesh(es) ({string.Join(", ", names.ToArray())})"
                        + $"{(unreadable > 0 ? $", {unreadable} unreadable" : "")}; {_zones.Count} splash point(s).");
            }
        }

        /// <summary>
        /// The collider's own mesh where it has one that can be read, else the model drawn on the same
        /// object. Primitive colliders are boxes and capsules round fittings and are not cut.
        /// </summary>
        private static Mesh MeshOf(Collider collider)
        {
            var meshCollider = collider as MeshCollider;
            if (meshCollider == null) return null;

            Mesh mesh = meshCollider.sharedMesh;
            if (mesh != null && mesh.isReadable) return mesh;

            var filter = collider.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null && filter.sharedMesh.isReadable) return filter.sharedMesh;

            return mesh;
        }

        /// <summary>The attitude to draw, which the line eases toward. Not Valid hides it.</summary>
        public void SetTarget(SeaAttitude attitude, bool visible)
        {
            _visible = visible && attitude.Valid;
            if (!_visible)
            {
                Hide();
                return;
            }

            _target = attitude;
            if (!_hasShown)
            {
                _shown = attitude;
                _hasShown = true;
            }
            _dirty = true;
        }

        public void Hide()
        {
            foreach (LineRenderer line in _lines) if (line != null) line.enabled = false;
        }

        /// <summary>
        /// Eases toward the target, cuts the hull again if anything moved, and places the lines.
        /// <paramref name="width"/> is the line width at the hull, from the gizmo's own sizing.
        /// </summary>
        public void Tick(Transform boat, float width, float deltaTime)
        {
            if (!_visible || boat == null || !HasHullMesh)
            {
                Hide();
                return;
            }

            float t = 1f - Mathf.Exp(-deltaTime * 3f / EaseSeconds);
            bool moving = Mathf.Abs(_shown.Heel - _target.Heel) > 0.01f
                || Mathf.Abs(_shown.Pitch - _target.Pitch) > 0.01f
                || Mathf.Abs(_shown.OriginHeight - _target.OriginHeight) > 0.0005f;
            if (moving)
            {
                _shown.Heel = Mathf.Lerp(_shown.Heel, _target.Heel, t);
                _shown.Pitch = Mathf.Lerp(_shown.Pitch, _target.Pitch, t);
                _shown.OriginHeight = Mathf.Lerp(_shown.OriginHeight, _target.OriginHeight, t);
                _dirty = true;
            }
            _shown.Valid = true;
            _shown.Overpowered = _target.Overpowered;

            if (_dirty)
            {
                _dirty = false;
                Cut(_shown);
            }

            Place(boat, width);
        }

        private void Place(Transform boat, float width)
        {
            // Red when the sea reaches any of the hull's splash points, the places the game lets water
            // aboard (WaveSplashZone.cs:47-52), so red means she is taking on water. Laid over without
            // water coming in is not red: that is the heel label's plus. A red dot at each wet point
            // was tried first and read as noise (user, 2026-11-14).
            Color color = WetSplashPoints(_shown) > 0 ? FloodColor : BalanceRing.KnobColor;

            for (int i = 0; i < _loops.Count; i++)
            {
                LineRenderer line = Line(i);
                if (line == null) break;

                List<Vector3> loop = _loops[i];
                line.positionCount = loop.Count;
                for (int p = 0; p < loop.Count; p++)
                    line.SetPosition(p, boat.position + boat.TransformDirection(loop[p]));
                line.loop = _closed[i];
                line.widthMultiplier = width;
                line.startColor = color;
                line.endColor = color;
                line.enabled = true;
            }
            for (int i = _loops.Count; i < _lines.Count; i++)
                if (_lines[i] != null) _lines[i].enabled = false;
        }

        // ------------------------------------------------------------------ the cut

        private struct Segment
        {
            public Vector3 A, B;
        }

        private readonly List<Segment> _segments = new List<Segment>();

        /// <summary>
        /// Cuts every hull triangle with the sea plane and joins the pieces into loops.
        ///
        /// A point's height above the sea is attitude.HeightOf(point), which is linear in the point, so
        /// along any edge it changes linearly and the crossing is where it passes zero.
        /// </summary>
        private void Cut(SeaAttitude attitude)
        {
            _segments.Clear();

            if (_vertices.Count == 0 && _cast.Count > 0)
            {
                Cast(attitude);
                FindLabelAnchor(attitude);
                return;
            }

            // HeightOf(p) is OriginHeight + (R p).y, which is OriginHeight + p . (R^-1 up): the same
            // dot product for every vertex, so the rotation is worked out once, not per vertex.
            Vector3 up = Quaternion.Inverse(attitude.Rotation) * Vector3.up;
            float h = attitude.OriginHeight;

            for (int m = 0; m < _vertices.Count; m++)
            {
                Vector3[] v = _vertices[m];
                int[] tri = _triangles[m];

                if (_heights.Length < v.Length) _heights = new float[v.Length];
                for (int i = 0; i < v.Length; i++) _heights[i] = h + Vector3.Dot(up, v[i]);

                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    int a = tri[i], b = tri[i + 1], c = tri[i + 2];

                    // Exactly zero counts as above, so a vertex sitting on the plane cannot make two
                    // crossings on the same edge.
                    bool sa = _heights[a] >= 0f, sb = _heights[b] >= 0f, sc = _heights[c] >= 0f;
                    if (sa == sb && sb == sc) continue;

                    Vector3 p, q;
                    if (sa == sb) { p = Crossing(v, c, a); q = Crossing(v, c, b); }
                    else if (sa == sc) { p = Crossing(v, b, a); q = Crossing(v, b, c); }
                    else { p = Crossing(v, a, b); q = Crossing(v, a, c); }

                    if ((p - q).sqrMagnitude > 1e-8f) _segments.Add(new Segment { A = p, B = q });
                }
            }

            Join();
            Outline(up);
            FindLabelAnchor(attitude);
        }

        private float[] _heights = new float[0];

        // ------------------------------------------------------------------ the outer contour
        //
        // The hull colliders are shells with thickness, an outside skin where the sea is and an
        // inside skin the crew walks on, and several hulls are two mirrored halves that are not
        // joined down the centerline. A level cut through that is two outlines a plank apart, or two
        // half outlines that never meet (2026-11-13 hull showcase log: dhow large two open halves,
        // dhow medium two closed halves plus a transom piece, junk medium an outer and an inner loop).
        // Only the outside skin is where the water meets the hull, so that is what is drawn.
        //
        // A waterline seen from its own middle is star shaped: every direction out from the center
        // crosses the outside skin once, last. So a cut piece belongs to the outline when nothing
        // else lies further out along the line from the center through it, and the outline is those
        // pieces in order of direction. Gaps where the mesh is open, a transom or the centerline
        // seam, are bridged straight, which is where the water meets the hull there too. A cut that
        // leaves a wide gap, a twin hull for one, is not star shaped, and keeps the raw loops.

        private const int Bins = 360;

        /// <summary>Widest gap in direction the outline may bridge before it is judged not one hull.</summary>
        private const float MaxBridgedGapDegrees = 60f;

        private readonly List<int>[] _bins = new List<int>[Bins];
        private Vector2[] _a2 = new Vector2[0], _b2 = new Vector2[0];

        /// <summary>True when the last cut was drawn as one outer contour.</summary>
        public bool OuterFound { get; private set; }

        /// <summary>The widest stretch, in degrees seen from the middle, the outer contour bridged.</summary>
        public float OuterGapDegrees { get; private set; }

        /// <summary>Loops the raw cut made before the outer contour replaced them, and how many were open.</summary>
        public int RawLoopCount { get; private set; }
        public int RawOpenCount { get; private set; }

        private void Outline(Vector3 up)
        {
            RawLoopCount = _loops.Count;
            RawOpenCount = OpenLoopCount;
            OuterFound = false;
            OuterGapDegrees = 360f;

            List<Vector3> outer;
            float gap;
            if (!TryOuterContour(up, out outer, out gap))
            {
                OuterGapDegrees = gap;
                return;
            }

            _loops.Clear();
            _closed.Clear();
            _loops.Add(outer);
            _closed.Add(true);
            OuterFound = true;
            OuterGapDegrees = gap;
        }

        private struct OutlinePoint
        {
            public float Angle, Radius;
            public Vector3 Point;
        }

        private bool TryOuterContour(Vector3 up, out List<Vector3> contour, out float maxGap)
        {
            contour = null;
            maxGap = 360f;
            int n = _segments.Count;
            if (n < 3) return false;

            // Two axes in the sea plane, in the boat's frame.
            Vector3 u = Vector3.ProjectOnPlane(Vector3.right, up);
            if (u.sqrMagnitude < 1e-6f) return false;
            u.Normalize();
            Vector3 w = Vector3.Cross(u, up);

            if (_a2.Length < n)
            {
                _a2 = new Vector2[n * 2];
                _b2 = new Vector2[n * 2];
            }

            // The middle: every piece's midpoint, weighted by its length.
            Vector2 center = Vector2.zero;
            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                Segment s = _segments[i];
                _a2[i] = new Vector2(Vector3.Dot(s.A, u), Vector3.Dot(s.A, w));
                _b2[i] = new Vector2(Vector3.Dot(s.B, u), Vector3.Dot(s.B, w));
                float length = (_b2[i] - _a2[i]).magnitude;
                center += (_a2[i] + _b2[i]) * 0.5f * length;
                total += length;
            }
            if (total <= 0f) return false;
            center /= total;

            // Each piece filed under every degree of direction it spans, so a ray only tests its own.
            for (int b = 0; b < Bins; b++)
            {
                if (_bins[b] == null) _bins[b] = new List<int>(8);
                else _bins[b].Clear();
            }
            for (int i = 0; i < n; i++)
            {
                float angleA = AngleOf(_a2[i] - center), angleB = AngleOf(_b2[i] - center);
                float delta = Mathf.DeltaAngle(angleA, angleB);
                int lo = Mathf.FloorToInt(angleA + Mathf.Min(0f, delta));
                int hi = Mathf.FloorToInt(angleA + Mathf.Max(0f, delta));
                for (int k = lo; k <= hi; k++) _bins[((k % Bins) + Bins) % Bins].Add(i);
            }

            var points = new List<OutlinePoint>(n * 2);
            for (int b = 0; b < Bins; b++) _covered[b] = false;
            for (int i = 0; i < n; i++)
            {
                Vector2 mid = (_a2[i] + _b2[i]) * 0.5f - center;
                float distance = mid.magnitude;
                if (distance < 1e-4f) continue;
                Vector2 direction = mid / distance;

                // Hidden when another piece lies further out along the same line, by more than float
                // noise: that piece is the outside skin and this one is inside it.
                bool hidden = false;
                foreach (int j in _bins[((Mathf.FloorToInt(AngleOf(mid)) % Bins) + Bins) % Bins])
                {
                    if (j == i) continue;
                    float t;
                    if (RayHits(center, direction, _a2[j], _b2[j], out t) && t > distance + 0.002f)
                    {
                        hidden = true;
                        break;
                    }
                }
                if (hidden) continue;

                // The directions this piece covers, so a gap is measured where no kept piece reaches,
                // not between neighboring points: one long plank amidships on a low detail collider
                // spans a hundred degrees by itself.
                MarkCovered(_a2[i] - center, _b2[i] - center);

                points.Add(new OutlinePoint { Angle = AngleOf(_a2[i] - center), Radius = (_a2[i] - center).magnitude, Point = _segments[i].A });
                points.Add(new OutlinePoint { Angle = AngleOf(_b2[i] - center), Radius = (_b2[i] - center).magnitude, Point = _segments[i].B });
            }
            if (points.Count < 3) return false;

            points.Sort((x, y) => x.Angle.CompareTo(y.Angle));

            // Shared ends come in twice; one is enough.
            var kept = new List<OutlinePoint>(points.Count);
            foreach (OutlinePoint p in points)
                if (kept.Count == 0 || (kept[kept.Count - 1].Point - p.Point).sqrMagnitude > 0.005f * 0.005f)
                    kept.Add(p);

            // A ray through a seam a few centimeters wide can see an inside piece past the tip of the
            // bow. It shows as a single point dented inward between two close neighbors; drop those.
            for (int k = kept.Count - 1; k >= 0 && kept.Count > 3; k--)
            {
                OutlinePoint prev = kept[(k - 1 + kept.Count) % kept.Count];
                OutlinePoint next = kept[(k + 1) % kept.Count];
                OutlinePoint p = kept[k];
                bool close = Mathf.Abs(Mathf.DeltaAngle(prev.Angle, p.Angle)) < 1.5f
                    && Mathf.Abs(Mathf.DeltaAngle(p.Angle, next.Angle)) < 1.5f;
                if (close && p.Radius < Mathf.Min(prev.Radius, next.Radius) - 0.02f) kept.RemoveAt(k);
            }
            if (kept.Count < 3) return false;

            maxGap = LongestUncovered();
            if (maxGap > MaxBridgedGapDegrees) return false;

            contour = new List<Vector3>(kept.Count);
            foreach (OutlinePoint p in kept) contour.Add(p.Point);
            return true;
        }

        private readonly bool[] _covered = new bool[Bins];

        private void MarkCovered(Vector2 a, Vector2 b)
        {
            float angleA = AngleOf(a), angleB = AngleOf(b);
            float delta = Mathf.DeltaAngle(angleA, angleB);
            int lo = Mathf.FloorToInt(angleA + Mathf.Min(0f, delta));
            int hi = Mathf.FloorToInt(angleA + Mathf.Max(0f, delta));
            for (int k = lo; k <= hi; k++) _covered[((k % Bins) + Bins) % Bins] = true;
        }

        /// <summary>The longest run of directions, in degrees, no kept piece covers.</summary>
        private float LongestUncovered()
        {
            int longest = 0, run = 0;
            for (int k = 0; k < Bins * 2; k++)
            {
                if (_covered[k % Bins]) run = 0;
                else longest = Mathf.Max(longest, ++run);
            }
            return Mathf.Min(longest, Bins);
        }

        private static float AngleOf(Vector2 v) => Mathf.Repeat(Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg, 360f);

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Where a ray from <paramref name="origin"/> crosses the piece from a to b, as a distance along it.</summary>
        private static bool RayHits(Vector2 origin, Vector2 direction, Vector2 a, Vector2 b, out float t)
        {
            t = 0f;
            Vector2 e = b - a;
            float denominator = Cross(direction, e);
            if (Mathf.Abs(denominator) < 1e-9f) return false;

            Vector2 toA = a - origin;
            t = Cross(toA, e) / denominator;
            float s = Cross(toA, direction) / denominator;
            return t > 0f && s >= -1e-4f && s <= 1f + 1e-4f;
        }

        /// <summary>
        /// Where the plane crosses the edge between two vertices. Always worked from the lower index to
        /// the higher, so the two triangles sharing an edge compute the identical point bit for bit and
        /// their pieces join.
        /// </summary>
        private Vector3 Crossing(Vector3[] v, int i, int j)
        {
            if (i > j) { int k = i; i = j; j = k; }
            float t = _heights[i] / (_heights[i] - _heights[j]);
            return Vector3.LerpUnclamped(v[i], v[j], t);
        }

        /// <summary>
        /// Chains the cut pieces end to end. Neighboring triangles share an edge, so they meet at the
        /// same crossing point; points are matched on a millimeter grid to absorb float noise.
        /// </summary>
        private void Join()
        {
            _loops.Clear();
            _closed.Clear();
            _dropped.Clear();
            _droppedTiny = 0;
            if (_segments.Count == 0) return;

            var ends = new Dictionary<long, List<int>>();
            for (int i = 0; i < _segments.Count; i++)
            {
                AddEnd(ends, Key(_segments[i].A), i);
                AddEnd(ends, Key(_segments[i].B), i);
            }

            var used = new bool[_segments.Count];
            var found = new List<KeyValuePair<float, KeyValuePair<bool, List<Vector3>>>>();

            for (int start = 0; start < _segments.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;

                var chain = new List<Vector3> { _segments[start].A, _segments[start].B };
                Extend(chain, ends, used);
                chain.Reverse();
                Extend(chain, ends, used);

                // A closed loop comes back to where it started; the line renderer closes it itself.
                // One that does not, where the mesh has an open edge such as a transom, stays open:
                // closing it would draw a line straight across the boat.
                bool closed = chain.Count > 3 && Key(chain[0]) == Key(chain[chain.Count - 1]);
                if (closed) chain.RemoveAt(chain.Count - 1);

                float length = 0f;
                for (int i = 1; i < chain.Count; i++) length += Vector3.Distance(chain[i - 1], chain[i]);
                if (length < MinLoopMeters) _droppedTiny++;
                else
                    found.Add(new KeyValuePair<float, KeyValuePair<bool, List<Vector3>>>(length,
                        new KeyValuePair<bool, List<Vector3>>(closed, chain)));
            }

            found.Sort((x, y) => y.Key.CompareTo(x.Key));
            for (int i = 0; i < found.Count; i++)
            {
                if (i >= MaxLoops || found[i].Key < found[0].Key * MinLoopShareOfLongest)
                {
                    _dropped.Add(new KeyValuePair<float, bool>(found[i].Key, found[i].Value.Key));
                    continue;
                }
                _loops.Add(found[i].Value.Value);
                _closed.Add(found[i].Value.Key);
            }
        }

        /// <summary>Loops the last cut set aside as fittings, by length and whether they closed.</summary>
        private readonly List<KeyValuePair<float, bool>> _dropped = new List<KeyValuePair<float, bool>>();

        /// <summary>Pieces the last cut set aside for being under MinLoopMeters.</summary>
        private int _droppedTiny;

        /// <summary>
        /// The waterline at this attitude, written out for the log: each loop kept, with its length,
        /// whether it closed, its extent in the boat's frame and a dozen points along it, then what
        /// was set aside and how many splash points the sea reaches.
        ///
        /// Cuts the hull at the given attitude and marks the line for a fresh cut, so the next Tick
        /// cuts again at the attitude it is drawing. Needs Measure first and no renderers at all, so
        /// it also serves a Waterline that is never attached to anything.
        /// </summary>
        public List<string> Describe(SeaAttitude attitude)
        {
            var lines = new List<string>();
            if (!HasHullMesh)
            {
                lines.Add("no readable hull mesh, so no waterline");
                return lines;
            }

            Cut(attitude);
            _dirty = true;

            bool traced = _vertices.Count == 0;
            if (traced)
            {
                lines.Add($"traced with {CastRays} rays against {_cast.Count} unreadable hull collider(s): {CastHits} hit, "
                    + $"widest miss {OuterGapDegrees:F0} deg{(OuterFound ? "" : ", so NO outline drawn")}; "
                    + $"{WetSplashPoints(attitude)} of {_zones.Count} splash point(s) under the sea");
                if (!OuterFound) return lines;
            }

            string splash = $"{WetSplashPoints(attitude)} of {_zones.Count} splash point(s) under the sea";
            if (!traced && OuterFound)
                lines.Add($"one outer contour from {_segments.Count} cut piece(s) over {_vertices.Count} hull mesh(es) "
                    + $"(the raw cut was {RawLoopCount} loop(s), {RawOpenCount} open; widest stretch bridged "
                    + $"{OuterGapDegrees:F0} deg as seen from the middle); {splash}");
            else if (!traced)
                lines.Add($"NO single outer contour (widest gap {OuterGapDegrees:F0} deg), drawn as the raw cut: "
                    + $"{_loops.Count} loop(s) from {_segments.Count} cut piece(s) over {_vertices.Count} hull mesh(es); {splash}");

            for (int i = 0; i < _loops.Count; i++)
            {
                List<Vector3> loop = _loops[i];
                float length = 0f;
                Vector3 min = loop[0], max = loop[0];
                for (int p = 0; p < loop.Count; p++)
                {
                    if (p > 0) length += Vector3.Distance(loop[p - 1], loop[p]);
                    min = Vector3.Min(min, loop[p]);
                    max = Vector3.Max(max, loop[p]);
                }
                if (_closed[i]) length += Vector3.Distance(loop[loop.Count - 1], loop[0]);

                var sb = new System.Text.StringBuilder();
                sb.Append($"loop {i + 1}: {(_closed[i] ? "closed" : "OPEN")}, {length:F2} m, {loop.Count} points, "
                    + $"x {min.x:F2}..{max.x:F2}, y {min.y:F2}..{max.y:F2}, z {min.z:F2}..{max.z:F2}");
                if (!_closed[i])
                {
                    Vector3 a = loop[0], b = loop[loop.Count - 1];
                    sb.Append($", ends ({a.x:F2}, {a.y:F2}, {a.z:F2}) and ({b.x:F2}, {b.y:F2}, {b.z:F2})");
                }

                // A dozen points evenly along the loop, boat frame (x starboard, y up, z forward).
                sb.Append("; points");
                int samples = Math.Min(12, loop.Count);
                for (int s = 0; s < samples; s++)
                {
                    Vector3 p = loop[s * loop.Count / samples];
                    sb.Append($" ({p.x:F2}, {p.y:F2}, {p.z:F2})");
                }
                lines.Add(sb.ToString());
            }

            if (_dropped.Count > 0 || _droppedTiny > 0)
            {
                var sb = new System.Text.StringBuilder("set aside as fittings:");
                foreach (KeyValuePair<float, bool> d in _dropped)
                    sb.Append($" {d.Key:F2} m {(d.Value ? "closed" : "open")};");
                if (_droppedTiny > 0) sb.Append($" {_droppedTiny} piece(s) under {MinLoopMeters:F2} m");
                lines.Add(sb.ToString());
            }

            return lines;
        }

        /// <summary>Loops kept by the last cut.</summary>
        public int LoopCount => _loops.Count;

        /// <summary>How many of the loops kept by the last cut are open.</summary>
        public int OpenLoopCount
        {
            get
            {
                int open = 0;
                foreach (bool closed in _closed) if (!closed) open++;
                return open;
            }
        }

        /// <summary>Whether any readable hull mesh was found by Measure.</summary>
        public bool HasHullMesh => _vertices.Count > 0 || _cast.Count > 0;

        // ------------------------------------------------------------------ traced by rays

        private const int CastRays = 360;

        private readonly List<CastProbe> _cast = new List<CastProbe>();
        private Transform _castBoat;
        private Vector3 _castCenter;    // boat frame
        private float _castReach;

        /// <summary>Rays that found the hull on the last trace.</summary>
        public int CastHits { get; private set; }

        /// <summary>Keeps the switched-on colliders to trace, and the middle and reach of the hull they make.</summary>
        /// <summary>
        /// Builds the mod's own copy of each unreadable hull collider to trace against.
        ///
        /// The boat's own colliders cannot be used: a switched-off collider cannot be hit by a ray,
        /// and on the large junk most are switched off (2026-11-13 log: 100 of 458 on, and 360 rays
        /// found nothing). Switching the boat's colliders on would be changing the boat. A copy is a
        /// separate object holding the same shape, which physics keeps even when the mesh's vertices
        /// are locked. It sits on a layer nothing else uses and is only switched on for the instant
        /// of a trace, inside one call, so no physics step ever sees it.
        /// </summary>
        private void PrepareCast(Transform boat, List<MeshCollider> colliders)
        {
            var bounds = new Bounds();
            bool any = false;
            var described = new List<string>();
            int layer = ProbeLayer();

            var sized = new List<KeyValuePair<float, string>>();
            foreach (MeshCollider c in colliders)
            {
                if (!Plausible(c, boat)) continue;
                Transform t = c.transform;
                Vector3 size, center;
                WorldBox(c, out size, out center);

                MeshCollider probe = null;
                try
                {
                    var go = new GameObject("RigBalanceWaterlineProbe") { hideFlags = HideFlags.HideAndDontSave };
                    if (layer >= 0) go.layer = layer;
                    probe = go.AddComponent<MeshCollider>();
                    probe.enabled = false;
                    probe.convex = c.convex;
                    probe.sharedMesh = c.sharedMesh;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"[Waterline] no probe for '{c.name}': {e.Message}");
                    if (probe != null) UnityEngine.Object.Destroy(probe.gameObject);
                    continue;
                }

                _cast.Add(new CastProbe { Source = t, Probe = probe });
                sized.Add(new KeyValuePair<float, string>(Mathf.Max(size.x, size.z),
                    $"'{c.name}' ({(c.enabled && c.gameObject.activeInHierarchy ? "on" : "off")}"
                    + $"{(c.convex ? ", convex" : "")}, {size.x:F1} x {size.y:F1} x {size.z:F1} m)"));

                var box = new Bounds(center, size);
                if (!any) { bounds = box; any = true; }
                else bounds.Encapsulate(box);
            }
            if (!any) return;

            _castBoat = boat;
            _castCenter = boat.InverseTransformDirection(bounds.center - boat.position);
            _castReach = bounds.extents.magnitude * 1.5f + 5f;

            // The six largest, which are the ones that make the outline.
            sized.Sort((a, b) => b.Key.CompareTo(a.Key));
            for (int i = 0; i < sized.Count && i < 6; i++) described.Add(sized[i].Value);
            Plugin.Log.LogInfo($"[Waterline] tracing against copies of {_cast.Count} hull collider(s), the largest: "
                + string.Join(", ", described.ToArray()) + ".");
        }

        /// <summary>The collider's mesh box in world space: its size and middle. A switched-off
        /// collider's own .bounds is empty, so this is taken from the mesh.</summary>
        private static void WorldBox(MeshCollider c, out Vector3 size, out Vector3 center)
        {
            Bounds local = c.sharedMesh.bounds;
            size = Vector3.Scale(local.size, RigModel.Abs(c.transform.lossyScale));
            center = c.transform.TransformPoint(local.center);
        }

        /// <summary>
        /// Not something parked far from the boat or the size of a world. The large junk carries
        /// Shipyard Expansion walk pieces about 75 km off (log 2026-11-13), which RigModel sets aside
        /// by the same 150 m test.
        /// </summary>
        private static bool Plausible(MeshCollider c, Transform boat)
        {
            if (c == null || c.sharedMesh == null) return false;
            Vector3 size, center;
            WorldBox(c, out size, out center);
            return RigModel.BoatSized(size, center, boat);
        }

        /// <summary>Whether any of these could be hull rather than a fitting: plausible and at least
        /// 2 m across the sea plane of the boat.</summary>
        private static bool HasPlausibleArea(List<MeshCollider> colliders, Transform boat)
        {
            foreach (MeshCollider c in colliders)
            {
                if (!Plausible(c, boat)) continue;
                Vector3 size, center;
                WorldBox(c, out size, out center);
                if (Mathf.Max(size.x, size.z) >= 2f) return true;
            }
            return false;
        }

        private sealed class CastProbe
        {
            public Transform Source;
            public MeshCollider Probe;
        }


        /// <summary>The highest layer the project never named, so the copies share it with nothing.</summary>
        private static int ProbeLayer()
        {
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return -1;
        }

        private void DestroyProbes()
        {
            foreach (CastProbe p in _cast)
                if (p != null && p.Probe != null) UnityEngine.Object.Destroy(p.Probe.gameObject);
            _cast.Clear();
        }

        /// <summary>
        /// The outline at this attitude, found by shooting rays across the sea plane from well outside
        /// the hull in toward its middle, one per degree, and keeping where each first meets a hull
        /// collider. That is the outside skin by construction. A degree apart, a sharp bow can come out
        /// a little blunt.
        /// </summary>
        private void Cast(SeaAttitude attitude)
        {
            _loops.Clear();
            _closed.Clear();
            _dropped.Clear();
            _droppedTiny = 0;
            RawLoopCount = 0;
            RawOpenCount = 0;
            OuterFound = false;
            OuterGapDegrees = 360f;
            CastHits = 0;
            if (_castBoat == null) return;

            Vector3 up = Quaternion.Inverse(attitude.Rotation) * Vector3.up;
            float h = attitude.OriginHeight;
            Vector3 center = _castCenter - up * (h + Vector3.Dot(up, _castCenter));
            Vector3 u = Vector3.ProjectOnPlane(Vector3.right, up);
            if (u.sqrMagnitude < 1e-6f) return;
            u.Normalize();
            Vector3 w = Vector3.Cross(u, up);

            // The copies take the pose their originals have right now, are switched on for the trace
            // and off again before this returns.
            foreach (CastProbe p in _cast)
            {
                if (p.Probe == null || p.Source == null) continue;
                Transform t = p.Probe.transform;
                t.SetPositionAndRotation(p.Source.position, p.Source.rotation);
                t.localScale = p.Source.lossyScale;
                p.Probe.enabled = true;
            }
            Physics.SyncTransforms();
            try
            {
                TraceRays(center, u, w);
            }
            finally
            {
                foreach (CastProbe p in _cast)
                    if (p.Probe != null) p.Probe.enabled = false;
            }
        }

        private void TraceRays(Vector3 center, Vector3 u, Vector3 w)
        {
            Quaternion rotation = _castBoat.rotation;
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 origin = _castBoat.position;

            var loop = new List<Vector3>(CastRays);
            var hit = new bool[CastRays];
            for (int k = 0; k < CastRays; k++)
            {
                float a = k * (2f * Mathf.PI / CastRays);
                Vector3 outward = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                var ray = new Ray(origin + rotation * (center + outward * _castReach), rotation * -outward);

                float best = float.MaxValue;
                Vector3 point = Vector3.zero;
                foreach (CastProbe p in _cast)
                {
                    if (p.Probe == null) continue;
                    RaycastHit result;
                    if (p.Probe.Raycast(ray, out result, _castReach) && result.distance < best)
                    {
                        best = result.distance;
                        point = result.point;
                    }
                }
                if (best == float.MaxValue) continue;

                hit[k] = true;
                CastHits++;
                loop.Add(inverse * (point - origin));
            }

            int longest = 0, run = 0;
            for (int k = 0; k < CastRays * 2; k++)
            {
                if (hit[k % CastRays]) run = 0;
                else longest = Mathf.Max(longest, ++run);
            }
            OuterGapDegrees = Mathf.Min(longest, CastRays) * (360f / CastRays);
            if (loop.Count < 3 || OuterGapDegrees > MaxBridgedGapDegrees) return;

            _loops.Add(loop);
            _closed.Add(true);
            OuterFound = true;
        }

        /// <summary>Splash points the sea reaches at this attitude.</summary>
        public int WetSplashPoints(SeaAttitude attitude)
        {
            int wet = 0;
            for (int z = 0; z < _zones.Count; z++)
                if (_zoneOffsets[z] - attitude.HeightOf(_zones[z]) >= 0f) wet++;
            return wet;
        }

        private readonly List<bool> _closed = new List<bool>();

        private void Extend(List<Vector3> chain, Dictionary<long, List<int>> ends, bool[] used)
        {
            for (int guard = 0; guard < _segments.Count; guard++)
            {
                Vector3 tail = chain[chain.Count - 1];
                List<int> touching;
                if (!ends.TryGetValue(Key(tail), out touching)) return;

                int next = -1;
                foreach (int s in touching)
                    if (!used[s]) { next = s; break; }
                if (next < 0) return;

                used[next] = true;
                Segment seg = _segments[next];
                chain.Add(Key(seg.A) == Key(tail) ? seg.B : seg.A);
            }
        }

        private static void AddEnd(Dictionary<long, List<int>> ends, long key, int segment)
        {
            List<int> list;
            if (!ends.TryGetValue(key, out list))
            {
                list = new List<int>(2);
                ends[key] = list;
            }
            list.Add(segment);
        }

        /// <summary>A point's cell on a millimeter grid, packed into one number.</summary>
        private static long Key(Vector3 p)
        {
            long x = (long)Mathf.Round(p.x * 1000f) & 0x1FFFFF;
            long y = (long)Mathf.Round(p.y * 1000f) & 0x1FFFFF;
            long z = (long)Mathf.Round(p.z * 1000f) & 0x1FFFFF;
            return (x << 42) | (y << 21) | z;
        }

        /// <summary>
        /// The point of the longest loop furthest out on the low side, where the heel label goes. Level,
        /// it is the starboard side amidships.
        /// </summary>
        private void FindLabelAnchor(SeaAttitude attitude)
        {
            if (_loops.Count == 0) return;

            // Positive heel lifts starboard, so the low side is port.
            float lowSide = attitude.Heel > 0.01f ? -1f : 1f;
            Vector3 best = _loops[0][0];
            float bestX = float.MinValue;
            foreach (Vector3 p in _loops[0])
            {
                float x = p.x * lowSide;
                if (x > bestX) { bestX = x; best = p; }
            }
            LabelAnchorLocal = best;
        }

        // ------------------------------------------------------------------ renderers

        private LineRenderer Line(int index)
        {
            while (_lines.Count <= index)
            {
                LineRenderer line = MakeLine("Waterline", _lineMaterial, 0);
                if (line == null) return null;
                _lines.Add(line);
            }
            return _lines[index];
        }

        private LineRenderer MakeLine(string name, Material material, int caps)
        {
            if (_parent == null || material == null) return null;

            try
            {
                var go = new GameObject(name);
                go.layer = _layer;
                go.transform.SetParent(_parent, false);

                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.numCapVertices = caps;
                line.numCornerVertices = 2;
                line.material = material;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
                line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                line.enabled = false;
                return line;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Waterline] line failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Drops every renderer. The parent object they hang off is the gizmo's, destroyed with it.</summary>
        public void Destroy()
        {
            _lines.Clear();
            _loops.Clear();
            _closed.Clear();
            _vertices.Clear();
            _triangles.Clear();
            _zones.Clear();
            _zoneOffsets.Clear();
            DestroyProbes();
            _castBoat = null;
            _hasShown = false;
            _visible = false;
            _meshesLogged = false;
            _parent = null;
            _lineMaterial = null;
        }
    }
}
