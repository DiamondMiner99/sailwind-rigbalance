using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// The hull showcase: a main menu mode that looks at every boat in the world in turn, with the
    /// shipyard gizmo's waterline, heel label and balance ring drawn on it, and writes every hull's
    /// waterline to the log.
    ///
    /// It lives on the main menu because that is the one place nothing can be disturbed. The menu is
    /// part of the world scene (StartMenu.LoadGameAnimation calls SaveLoadManager.LoadGame with no
    /// scene load), so every boat already exists behind it, and BoatHorizon.UpdateKinematic holds
    /// every boat kinematic while GameState.playing is false (BoatHorizon.cs, the !GameState.playing
    /// branch). No save is loaded, so there is nothing to write back, and the distance switch that
    /// deactivates boats past 10 km (BoatHorizonPerformanceSwitcher) does nothing until playing.
    ///
    /// What it touches, all put back on the way out:
    ///   - its own camera, created here and destroyed on leaving;
    ///   - the main camera's culling mask, set to nothing so the world is not drawn twice;
    ///   - the game's 'UI camera', switched off so the menu is not stamped over the view;
    ///   - Crest's viewpoint, pointed at this camera so the sea is drawn around the boat being
    ///     looked at, then restored to its own raw value (usually null, meaning Camera.main).
    /// The menu's buttons, saving and loading are refused by Harmony prefixes while it runs. No boat,
    /// sail or player is moved; the gizmo's model poses sails inside one call and restores them in a
    /// finally block, exactly as it does in a shipyard.
    /// </summary>
    public class HullShowcase : MonoBehaviour
    {
        public static bool Active { get; private set; }

        /// <summary>The boat being looked at, or null.</summary>
        public static GameObject CurrentBoat { get; private set; }

        /// <summary>The showcase's own camera while it runs, which the gizmo draws through.</summary>
        public static Camera ViewCamera { get; private set; }

        /// <summary>The guard patches at the bottom of this class: menu button, slot button, save, load.</summary>
        public const int GuardCount = 4;

        /// <summary>Set at load when every guard patch applied. Without them the showcase is not offered.</summary>
        public static bool GuardsReady { get; set; }

        private static readonly FieldInfo FStartUI = AccessTools.Field(typeof(StartMenu), "startUI");
        private static readonly FieldInfo FAnimsPlaying = AccessTools.Field(typeof(StartMenu), "animsPlaying");
        private static readonly FieldInfo FViewpoint = AccessTools.Field(typeof(Crest.OceanRenderer), "_viewpoint");

        private StartMenu _menu;
        private float _nextMenuSearch;

        private sealed class Entry
        {
            public GameObject Boat;
            public string Name;
            public string Type;
            public int TypeIndex, CopyIndex, CopyCount;
            public float Distance;
            public string Summary;
        }

        private readonly List<Entry> _boats = new List<Entry>();
        private int _typeCount;
        private int _index;

        // What is put back on leaving.
        private Camera _mainCamera;
        private int _mainMask;
        private bool _mainMaskSet;
        private readonly List<Camera> _uiCameras = new List<Camera>();
        private Crest.OceanRenderer _ocean;
        private object _oceanViewpoint;
        private bool _oceanSet;
        private GameObject _cameraObject;

        // The view, kept from one boat to the next so a chosen angle carries over.
        private const float StartYaw = -135f, StartPitch = 12f;
        private float _yaw = StartYaw, _pitch = StartPitch, _zoom = 1f;
        private Vector3 _targetLocal;
        private float _radius = 10f;
        private bool _orbiting;
        private Vector3 _lastMouse;

        // The survey: every boat read once, one per frame, written to the log.
        private int _surveyNext;
        private bool _surveyDone;
        private int _surveyRead, _surveyChecks;
        private float _farthestRead;
        private readonly List<string> _unread = new List<string>();

        private void Update()
        {
            try
            {
                if (!Active)
                {
                    FindMenu();
                    return;
                }

                // Never alongside a game. The menu's own buttons are refused while this runs, so
                // this only fires if something else started one.
                if (GameState.playing || GameState.currentlyLoading)
                {
                    Stop("a game is starting");
                    return;
                }

                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    Stop("left with Esc");
                    return;
                }

                if (Input.GetKeyDown(KeyCode.RightArrow)) Step(1);
                else if (Input.GetKeyDown(KeyCode.LeftArrow)) Step(-1);
                if (Input.GetKeyDown(KeyCode.Home)) ResetView();

                if (CurrentBoat == null || !CurrentBoat.activeInHierarchy)
                {
                    Plugin.Log.LogWarning("[Showcase] the boat being shown went away; moving on.");
                    if (!Show(_index + 1, 1)) { Stop("no boats left to show"); return; }
                }

                Orbit();
                PlaceCamera();
                UpdateShadows();

                if (!_surveyDone) SurveyNext();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Showcase] stopped by an error: " + e);
                Stop("error");
            }
        }

        private void OnDestroy()
        {
            if (Active) Stop("plugin destroyed");
        }

        // ------------------------------------------------------------------ the menu

        private void FindMenu()
        {
            if (_menu != null || GameState.playing || Time.unscaledTime < _nextMenuSearch) return;
            _nextMenuSearch = Time.unscaledTime + 1f;
            _menu = FindObjectOfType<StartMenu>();
        }

        /// <summary>
        /// The first page of the main menu is up and nothing is animating: no game, no load, no
        /// other menu page open.
        /// </summary>
        private bool MenuShowing()
        {
            if (!GuardsReady || _menu == null || GameState.playing || GameState.currentlyLoading || GameState.changingStartRegion)
                return false;

            try
            {
                if (!(FStartUI?.GetValue(_menu) is GameObject startUi) || !startUi.activeInHierarchy) return false;
                if (FAnimsPlaying?.GetValue(_menu) is int anims && anims > 0) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ start and stop

        private void Begin()
        {
            if (Active || !MenuShowing()) return;

            Camera main = Camera.main;
            if (main == null)
            {
                Plugin.Log.LogWarning("[Showcase] no main camera to copy; not starting.");
                return;
            }

            Collect(main.transform.position);
            if (_boats.Count == 0)
            {
                Plugin.Log.LogWarning("[Showcase] no boats found in the world; not starting.");
                return;
            }

            try
            {
                _cameraObject = new GameObject("RigBalanceShowcaseCamera");
                Camera view = _cameraObject.AddComponent<Camera>();
                view.CopyFrom(main);
                view.depth = main.depth + 0.5f;
                view.farClipPlane = Mathf.Max(main.farClipPlane, 3000f);
                ViewCamera = view;

                _mainCamera = main;
                _mainMask = main.cullingMask;
                main.cullingMask = 0;
                _mainMaskSet = true;

                _uiCameras.Clear();
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam == null || cam == view || cam.name != "UI camera" || !cam.enabled) continue;
                    cam.enabled = false;
                    _uiCameras.Add(cam);
                }

                _ocean = Crest.OceanRenderer.Instance;
                if (_ocean != null && FViewpoint != null)
                {
                    _oceanViewpoint = FViewpoint.GetValue(_ocean);
                    _ocean.Viewpoint = view.transform;
                    _oceanSet = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("[Showcase] could not set up the view: " + e);
                Restore();
                return;
            }

            Active = true;

            // Every boat sits in port with her sails furled, which reads as no wind at all. The model
            // judges them all set instead; the sails themselves stay furled.
            RigModel.AllSailsSet = true;
            _surveyNext = 0;
            _surveyDone = false;
            _surveyRead = _surveyChecks = 0;
            _farthestRead = 0f;
            _unread.Clear();
            ResetView();

            Plugin.Log.LogInfo($"[Showcase] started: {_boats.Count} boat(s) of {_typeCount} hull type(s); "
                + $"ocean viewpoint {(_oceanSet ? "moved to the showcase camera" : "left alone")}, "
                + $"{_uiCameras.Count} UI camera(s) paused. Menu buttons, saving and loading are refused until it ends.");

            if (!Show(0, 1)) Stop("no boat could be shown");
        }

        private void Stop(string reason)
        {
            if (!Active && _cameraObject == null) return;

            Active = false;
            CurrentBoat = null;
            Restore();

            Plugin.Log.LogInfo($"[Showcase] ended ({reason}); camera, sea and menu restored. "
                + (_surveyDone ? "" : $"The survey stopped at {_surveyNext} of {_boats.Count}."));
        }

        /// <summary>Puts back everything Begin changed. Each step on its own, so one failure cannot strand the rest.</summary>
        private void Restore()
        {
            RigModel.AllSailsSet = false;

            try { if (_shadowsOff) QualitySettings.shadowDistance = _shadowDistance; }
            catch (Exception e) { Plugin.Log.LogWarning("[Showcase] could not restore shadows: " + e.Message); }
            _shadowsOff = false;

            try
            {
                if (_oceanSet && _ocean != null && FViewpoint != null) FViewpoint.SetValue(_ocean, _oceanViewpoint);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Showcase] could not restore the ocean viewpoint: " + e.Message); }
            _oceanSet = false;
            _ocean = null;
            _oceanViewpoint = null;

            try
            {
                if (_mainMaskSet && _mainCamera != null) _mainCamera.cullingMask = _mainMask;
            }
            catch (Exception e) { Plugin.Log.LogWarning("[Showcase] could not restore the main camera: " + e.Message); }
            _mainMaskSet = false;
            _mainCamera = null;

            foreach (Camera cam in _uiCameras)
            {
                try { if (cam != null) cam.enabled = true; }
                catch (Exception e) { Plugin.Log.LogWarning("[Showcase] could not restore the UI camera: " + e.Message); }
            }
            _uiCameras.Clear();

            ViewCamera = null;
            if (_cameraObject != null) Destroy(_cameraObject);
            _cameraObject = null;
            _orbiting = false;
        }

        // ------------------------------------------------------------------ shadows

        /// <summary>
        /// Past this distance from the world origin the sun's shadows swim on the hull: the game
        /// keeps the player near the origin by shifting the world (FloatingOriginManager), and every
        /// boat but the menu's own dhow sits kilometers out, where shadow maps lose the precision to
        /// hold still. Seen worst on the Aestrin boats a hundred kilometers out.
        /// </summary>
        private const float ShadowsOffBeyondMeters = 2000f;

        private bool _shadowsOff;
        private float _shadowDistance;

        /// <summary>
        /// Switches the sun's shadows off while a boat far from the origin is shown, and back on for
        /// one near it. Restored on leaving whatever happened in between.
        /// </summary>
        private void UpdateShadows()
        {
            if (CurrentBoat == null) return;
            Vector3 p = CurrentBoat.transform.position;
            bool far = new Vector2(p.x, p.z).magnitude > ShadowsOffBeyondMeters;
            if (far == _shadowsOff) return;

            if (far)
            {
                _shadowDistance = QualitySettings.shadowDistance;
                QualitySettings.shadowDistance = 0f;
                _shadowsOff = true;
            }
            else
            {
                QualitySettings.shadowDistance = _shadowDistance;
                _shadowsOff = false;
            }
            Plugin.Log.LogInfo($"[Showcase] shadows {(far ? "off" : "on")} for '{CurrentBoat.name}', "
                + $"{new Vector2(p.x, p.z).magnitude / 1000f:F1} km from the world origin.");
        }

        // ------------------------------------------------------------------ the roster

        private static readonly Regex CopySuffix = new Regex(@"\s*(\(Clone\)|\(\d+\))\s*$");

        /// <summary>The boat's name without its copy number, which is what tells hull types apart.</summary>
        private static string TypeOf(string name)
        {
            string type = name ?? "";
            for (int guard = 0; guard < 4; guard++)
            {
                string next = CopySuffix.Replace(type, "");
                if (next == type) break;
                type = next;
            }
            return type.Trim();
        }

        /// <summary>
        /// Every player boat in the loaded scene: anything carrying BoatRefs, which houses do not
        /// (0.39 houses are PurchasableBoat with no BoatRefs). Inactive ones are listed and left
        /// alone, since showing one would mean switching it on.
        /// </summary>
        private void Collect(Vector3 eye)
        {
            _boats.Clear();
            var inactive = new List<string>();

            foreach (BoatRefs refs in Resources.FindObjectsOfTypeAll<BoatRefs>())
            {
                if (refs == null) continue;
                GameObject go = refs.gameObject;
                if (!go.scene.IsValid() || go.hideFlags != HideFlags.None) continue;   // prefabs and assets

                if (!go.activeInHierarchy)
                {
                    inactive.Add(go.name);
                    continue;
                }

                _boats.Add(new Entry
                {
                    Boat = go,
                    Name = go.name,
                    Type = TypeOf(go.name),
                    Distance = Vector3.Distance(eye, go.transform.position),
                });
            }

            _boats.Sort((a, b) =>
            {
                int byType = string.CompareOrdinal(a.Type, b.Type);
                return byType != 0 ? byType : string.CompareOrdinal(a.Name, b.Name);
            });

            _typeCount = 0;
            for (int i = 0; i < _boats.Count; i++)
            {
                if (i == 0 || _boats[i].Type != _boats[i - 1].Type) _typeCount++;
                _boats[i].TypeIndex = _typeCount - 1;
            }
            for (int i = 0; i < _boats.Count; i++)
            {
                int first = i, last = i;
                while (first > 0 && _boats[first - 1].TypeIndex == _boats[i].TypeIndex) first--;
                while (last < _boats.Count - 1 && _boats[last + 1].TypeIndex == _boats[i].TypeIndex) last++;
                _boats[i].CopyIndex = i - first;
                _boats[i].CopyCount = last - first + 1;
            }

            Plugin.Log.LogInfo($"[Showcase] roster: {_boats.Count} active boat(s), {_typeCount} hull type(s)"
                + (inactive.Count > 0
                    ? $"; {inactive.Count} inactive in the scene and skipped: {string.Join(", ", inactive.ToArray())}"
                    : "; none inactive."));
            for (int i = 0; i < _boats.Count; i++)
            {
                Entry e = _boats[i];
                Plugin.Log.LogInfo($"[Showcase]   {i + 1}. '{e.Name}' (type '{e.Type}', copy {e.CopyIndex + 1} of "
                    + $"{e.CopyCount}), {e.Distance / 1000f:F1} km from the menu view");
            }
        }

        // ------------------------------------------------------------------ stepping

        /// <summary>
        /// Shows the boat at <paramref name="index"/>, or the next one in <paramref name="direction"/>
        /// that is still there. False when none is.
        /// </summary>
        private bool Show(int index, int direction)
        {
            for (int tries = 0; tries < _boats.Count; tries++)
            {
                int i = ((index % _boats.Count) + _boats.Count) % _boats.Count;
                GameObject boat = _boats[i].Boat;
                if (boat != null && boat.activeInHierarchy)
                {
                    _index = i;
                    CurrentBoat = boat;
                    Frame(boat);
                    Plugin.Log.LogInfo($"[Showcase] showing {i + 1} of {_boats.Count}: '{_boats[i].Name}'.");
                    return true;
                }
                index += direction;
            }
            CurrentBoat = null;
            return false;
        }

        private void Step(int direction) => Show(_index + direction, direction);


        // ------------------------------------------------------------------ the camera

        /// <summary>Aims at the middle of the hull's own colliders, sized to them.</summary>
        private void Frame(GameObject boat)
        {
            var bounds = new Bounds(boat.transform.position, Vector3.zero);
            bool any = false;

            // Only parts that are switched on and of a size a boat part can have. A disabled collider
            // reports an empty box at the world origin, and the large junk carries something hundreds
            // of meters long; either one stretched the box out to sea and the camera looked at water.
            foreach (Collider c in RigModel.HullColliders(boat))
            {
                if (c == null || !c.enabled || !Believable(c.bounds, boat.transform)) continue;
                if (!any) { bounds = c.bounds; any = true; }
                else bounds.Encapsulate(c.bounds);
            }
            if (!any)
            {
                foreach (Renderer r in boat.GetComponentsInChildren<Renderer>())
                {
                    if (r == null || !r.enabled || !Believable(r.bounds, boat.transform)) continue;
                    if (!any) { bounds = r.bounds; any = true; }
                    else bounds.Encapsulate(r.bounds);
                }
            }

            if (any)
            {
                _targetLocal = boat.transform.InverseTransformPoint(bounds.center);
                _radius = Mathf.Clamp(bounds.extents.magnitude, 2f, 80f);
            }
            else
            {
                _targetLocal = Vector3.zero;
                _radius = 15f;
            }
        }

        private static bool Believable(Bounds b, Transform boat)
        {
            return RigModel.BoatSized(b.size, b.center, boat);
        }

        private void ResetView()
        {
            _yaw = StartYaw;
            _pitch = StartPitch;
            _zoom = 1f;
        }

        private void Orbit()
        {
            if (Input.GetMouseButtonDown(1))
            {
                _orbiting = true;
                _lastMouse = Input.mousePosition;
            }
            if (!Input.GetMouseButton(1)) _orbiting = false;

            if (_orbiting)
            {
                Vector3 delta = Input.mousePosition - _lastMouse;
                _lastMouse = Input.mousePosition;
                _yaw += delta.x * 0.3f;
                _pitch = Mathf.Clamp(_pitch - delta.y * 0.3f, -5f, 85f);
            }

            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.01f) _zoom = Mathf.Clamp(_zoom * Mathf.Pow(0.88f, wheel), 0.3f, 4f);
        }

        /// <summary>
        /// In Update rather than LateUpdate, so the gizmo's overlay camera, which copies this one in
        /// its own LateUpdate, never trails it by a frame. The boats are kinematic at the menu, so
        /// nothing moves the target after this.
        /// </summary>
        private void PlaceCamera()
        {
            if (ViewCamera == null || CurrentBoat == null) return;

            Transform boat = CurrentBoat.transform;
            Vector3 target = boat.TransformPoint(_targetLocal);
            Vector3 ahead = Vector3.ProjectOnPlane(boat.forward, Vector3.up);
            if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.forward;
            Quaternion heading = Quaternion.LookRotation(ahead.normalized, Vector3.up);

            float distance = _radius * 2.4f * _zoom;
            Vector3 offset = heading * (Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.back) * distance;
            ViewCamera.transform.position = target + offset;
            ViewCamera.transform.LookAt(target, Vector3.up);
        }

        // ------------------------------------------------------------------ the survey

        private void SurveyNext()
        {
            if (_surveyNext >= _boats.Count)
            {
                _surveyDone = true;
                Plugin.Log.LogInfo($"[Showcase] survey done: {_surveyRead} of {_boats.Count} boat(s) gave a full "
                    + $"reading, the farthest {_farthestRead / 1000f:F1} km from the menu view; "
                    + $"{_surveyChecks} CHECK line(s)."
                    + (_unread.Count > 0 ? " Without a full reading: " + string.Join("; ", _unread.ToArray()) + "." : ""));
                return;
            }

            Entry entry = _boats[_surveyNext];
            int number = ++_surveyNext;
            try
            {
                Survey(entry, number);
            }
            catch (Exception e)
            {
                _unread.Add($"'{entry.Name}' (error: {e.Message})");
                Plugin.Log.LogWarning($"[Showcase] {number}/{_boats.Count} '{entry.Name}' could not be read: {e}");
            }
        }

        /// <summary>
        /// One boat, read without drawing anything: how she floats in calm water and the waterline
        /// there, the waterline at the configured wind, and where the scene placed her, with a CHECK
        /// line for anything that looks wrong. Searching the log for "CHECK" finds every problem.
        /// </summary>
        private void Survey(Entry entry, int number)
        {
            GameObject boat = entry.Boat;
            string head = $"[Showcase] {number}/{_boats.Count} '{entry.Name}'";
            if (boat == null || !boat.activeInHierarchy)
            {
                _unread.Add($"'{entry.Name}' (gone)");
                Plugin.Log.LogWarning(head + ": no longer in the scene.");
                return;
            }

            Transform t = boat.transform;
            var body = boat.GetComponent<Rigidbody>();
            var refs = boat.GetComponent<BoatRefs>();
            var checks = new List<string>();

            int masts = 0, sails = 0;
            if (refs != null && refs.masts != null)
                foreach (Mast mast in refs.masts)
                {
                    if (mast == null || !mast.gameObject.activeInHierarchy) continue;
                    masts++;
                    if (mast.sails != null)
                        foreach (GameObject s in mast.sails) if (s != null) sails++;
                }

            float sea = 0f;
            try { if (Crest.OceanRenderer.Instance != null) sea = Crest.OceanRenderer.Instance.SeaLevel; }
            catch { }

            float placedHeight = t.position.y - sea;
            float placedHeel = Mathf.Asin(Mathf.Clamp(t.right.y, -1f, 1f)) * Mathf.Rad2Deg;
            float placedPitch = -Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            Plugin.Log.LogInfo($"{head} (type '{entry.Type}'): {entry.Distance / 1000f:F1} km from the menu view, "
                + $"{(body == null ? "no rigidbody" : body.isKinematic ? "kinematic" : "NOT kinematic")}, "
                + $"{masts} mast(s), {sails} sail(s). As placed in the scene: origin {placedHeight:F2} m above the "
                + $"sea, heel {placedHeel:F1} deg, pitch {placedPitch:F1} deg.");

            LogParts(head, boat);

            // Calm water, with the center of mass over the buoyancy the way the shipyard reads it.
            SeaAttitude calm;
            Hydrostatics.HullModel hull = RigModel.ShipyardHull(boat, out calm);
            if (hull == null)
            {
                checks.Add("no buoyancy reading (no Crest buoyancy points, or no rigidbody), so no waterline");
            }
            else
            {
                Plugin.Log.LogInfo($"{head}   hull: {hull.Describe()}.");
                if (!calm.Valid) checks.Add("the buoyancy points cannot float her, so no waterline");
                else
                {
                    Plugin.Log.LogInfo($"{head}   calm float: origin {calm.OriginHeight:F2} m above the sea "
                        + $"({calm.OriginHeight - placedHeight:+0.00;-0.00} m from where the scene placed her).");
                }
            }

            // Destroyed however this ends: Measure can make probe colliders that outlive a scene load.
            var waterline = new Waterline();
            try
            {
                SurveyWaterline(entry, head, boat, calm, checks, waterline);
            }
            finally
            {
                waterline.Destroy();
            }
        }

        private void SurveyWaterline(Entry entry, string head, GameObject boat, SeaAttitude calm,
            List<string> checks, Waterline waterline)
        {
            waterline.Measure(boat);
            if (!waterline.HasHullMesh) checks.Add("no readable hull mesh, so no waterline can be cut");

            string summary;
            bool fullReading = false;

            if (calm.Valid && waterline.HasHullMesh)
            {
                foreach (string line in waterline.Describe(calm))
                    Plugin.Log.LogInfo($"{head}   calm waterline: {line}");

                if (waterline.LoopCount == 0) checks.Add("the calm waterline cuts nothing");
                else if (!waterline.OuterFound)
                    checks.Add($"no single outer contour for the calm waterline, drawn as {waterline.LoopCount} raw loop(s)");
                int wet = waterline.WetSplashPoints(calm);
                if (wet > 0) checks.Add($"{wet} splash point(s) under the sea in calm water");
                fullReading = waterline.LoopCount > 0;
            }

            summary = calm.Valid
                ? $"Calm water: origin {calm.OriginHeight:+0.00;-0.00} m, "
                    + (waterline.OuterFound ? "one outer waterline" : $"waterline {waterline.LoopCount} raw loop(s)")
                : "Calm water: no buoyancy reading";

            // The arrow's starting wind, as the shipyard would open with it.
            float angle = Config.Settings.WindAngle != null ? Config.Settings.WindAngle.Value : 45f;
            float speed = Config.Settings.WindSpeed != null ? Config.Settings.WindSpeed.Value : RigModel.SyntheticWindSpeed;
            RigBalance balance;
            bool evaluated = RigModel.TryEvaluate(boat, angle, out balance, null, 1f, 0f, null, speed);
            if (!evaluated)
            {
                checks.Add("the rig model gave no reading for this boat");
                fullReading = false;
            }
            else if (balance.Attitude.Valid)
            {
                SeaAttitude a = balance.Attitude;
                Plugin.Log.LogInfo($"{head}   all sails set, at {angle:F0} deg, {speed:F1} m/s: heel {a.Heel:F1} deg, pitch "
                    + $"{a.Pitch:F1} deg, origin {a.OriginHeight:F2} m above the sea"
                    + $"{(a.Overpowered ? ", no balance before the heel limit" : "")}; helm {balance.HelmMeters:F2} m, "
                    + $"hull {balance.HullLength:F1} m.");
                if (waterline.HasHullMesh)
                    foreach (string line in waterline.Describe(a))
                        Plugin.Log.LogInfo($"{head}   waterline at {angle:F0} deg: {line}");
                summary += $". All sails set, {angle:F0}° at {speed:F0} m/s: heel {Mathf.Abs(a.Heel):F0}°"
                    + (a.Overpowered ? " (laid over)" : "");
            }
            else
            {
                Plugin.Log.LogInfo($"{head}   at {angle:F0} deg: read upright, heel not modeled for this boat.");
            }

            foreach (string check in checks)
                Plugin.Log.LogWarning($"{head}   CHECK: {check}.");
            _surveyChecks += checks.Count;

            if (fullReading)
            {
                _surveyRead++;
                _farthestRead = Mathf.Max(_farthestRead, entry.Distance);
            }
            else
            {
                _unread.Add($"'{entry.Name}' ({(checks.Count > 0 ? checks[0] : "incomplete")})");
            }

            entry.Summary = summary + ".";
        }

        /// <summary>
        /// What the boat is drawn and measured from: renderers and colliders, how many are switched
        /// on, and whether their meshes can be read. A boat that cannot be seen or cut shows why here.
        /// </summary>
        private static void LogParts(string head, GameObject boat)
        {
            int renderers = 0, renderersOn = 0, batched = 0, lods = 0;
            foreach (Renderer r in boat.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                renderers++;
                if (r.enabled && r.gameObject.activeInHierarchy) renderersOn++;
                if (r.isPartOfStaticBatch) batched++;
            }
            lods = boat.GetComponentsInChildren<LODGroup>(true).Length;

            int colliders = 0, collidersOn = 0, meshColliders = 0, readable = 0;
            var unreadable = new List<string>();
            foreach (Collider c in boat.GetComponentsInChildren<Collider>(true))
            {
                if (c == null) continue;
                colliders++;
                if (c.enabled && c.gameObject.activeInHierarchy) collidersOn++;
                var mc = c as MeshCollider;
                if (mc == null || c.isTrigger) continue;
                meshColliders++;
                if (mc.sharedMesh != null && mc.sharedMesh.isReadable) readable++;
                else if (unreadable.Count < 6)
                    unreadable.Add($"'{c.name}' ({(mc.sharedMesh == null ? "no mesh" : mc.sharedMesh.name)}"
                        + $"{(c.enabled ? "" : ", disabled")})");
            }

            string path = boat.name;
            for (Transform t = boat.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;

            Plugin.Log.LogInfo($"{head}   parts: {renderersOn} of {renderers} renderer(s) on, {batched} static batched, "
                + $"{lods} LOD group(s); {collidersOn} of {colliders} collider(s) on, {readable} of {meshColliders} "
                + $"solid mesh collider(s) readable{(unreadable.Count > 0 ? ", unreadable: " + string.Join(", ", unreadable.ToArray()) : "")}; "
                + $"layer {boat.layer}, path '{path}'.");
        }

        // ------------------------------------------------------------------ interface

        private GUIStyle _buttonStyle, _textStyle, _titleStyle, _smallButtonStyle;
        private Texture2D _paperTex;
        private Font _font;
        private static readonly Color Ink = new Color(0.20f, 0.13f, 0.08f);

        private void EnsureStyles()
        {
            if (_buttonStyle != null) return;

            _paperTex = ShipyardGizmo.MakePanelTexture(32, 10);
            try
            {
                if (_menu != null)
                {
                    TextMesh text = _menu.GetComponentInChildren<TextMesh>(true);
                    if (text != null) _font = text.font;
                }
            }
            catch { }

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 17,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(12, 12, 12, 12),
                padding = new RectOffset(14, 14, 6, 6),
            };
            _buttonStyle.normal.background = _paperTex;
            _buttonStyle.hover.background = _paperTex;
            _buttonStyle.active.background = _paperTex;
            _buttonStyle.normal.textColor = Ink;
            _buttonStyle.hover.textColor = new Color(0.45f, 0.22f, 0.08f);
            _buttonStyle.active.textColor = Color.black;
            if (_font != null) _buttonStyle.font = _font;

            _smallButtonStyle = new GUIStyle(_buttonStyle) { fontSize = 14, padding = new RectOffset(10, 10, 4, 4) };

            _textStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
            _textStyle.normal.textColor = Ink;
            if (_font != null) _textStyle.font = _font;

            _titleStyle = new GUIStyle(_textStyle) { fontSize = 18, fontStyle = FontStyle.Bold };

            // Nine-sliced, so the rounded corners keep their shape on a panel this size.
            _panelStyle = new GUIStyle(GUIStyle.none) { border = new RectOffset(12, 12, 12, 12) };
            _panelStyle.normal.background = _paperTex;
        }

        private GUIStyle _panelStyle;

        private void OnGUI()
        {
            try
            {
                if (Active) DrawHud();
                else if ((Config.Settings.ShowHullShowcaseButton == null || Config.Settings.ShowHullShowcaseButton.Value)
                         && MenuShowing())
                    DrawMenuButton();
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug("[Showcase] interface skipped: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ the menu button

        private const float ButtonWidth = 200f, ButtonHeight = 44f, ButtonRadius = 10f;

        /// <summary>How far the glow reaches past the button, in pixels.</summary>
        private const int GlowMargin = 12;

        private Texture2D _glowTex;
        private Color32[] _glowPixels;
        private float _glowBuiltAt = -1f;

        /// <summary>
        /// Top left, with a rainbow running round its edge and a soft glow off it, so it catches the
        /// eye on a menu where nothing else moves.
        /// </summary>
        private void DrawMenuButton()
        {
            EnsureStyles();
            var rect = new Rect(24f, 24f, ButtonWidth, ButtonHeight);

            if (Event.current.type == EventType.Repaint)
            {
                UpdateGlow();
                if (_glowTex != null)
                    GUI.DrawTexture(new Rect(rect.x - GlowMargin, rect.y - GlowMargin,
                        _glowTex.width, _glowTex.height), _glowTex);
            }

            if (GUI.Button(rect, "Hull showcase", _buttonStyle)) Begin();
        }

        /// <summary>
        /// Repaints the glow, at most thirty times a second: a ring hugging the button's rounded edge
        /// and a halo fading out from it, the hue going round the button and turning over time. Drawn
        /// at one texel per pixel, so it stays sharp.
        /// </summary>
        private void UpdateGlow()
        {
            int w = (int)ButtonWidth + 2 * GlowMargin, h = (int)ButtonHeight + 2 * GlowMargin;
            if (_glowTex == null)
            {
                _glowTex = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Point,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _glowPixels = new Color32[w * h];
            }

            float now = Time.unscaledTime;
            if (_glowBuiltAt >= 0f && now - _glowBuiltAt < 1f / 30f) return;
            _glowBuiltAt = now;

            float halfW = ButtonWidth * 0.5f - ButtonRadius, halfH = ButtonHeight * 0.5f - ButtonRadius;
            float cx = w * 0.5f, cy = h * 0.5f;
            float turn = now * 0.25f;
            const float Ring = 2.5f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Signed distance from the button's rounded outline, positive outside it.
                    float px = Mathf.Abs(x + 0.5f - cx) - halfW;
                    float py = Mathf.Abs(y + 0.5f - cy) - halfH;
                    float outside = new Vector2(Mathf.Max(px, 0f), Mathf.Max(py, 0f)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(px, py), 0f) - ButtonRadius;

                    float alpha;
                    if (d < -1f) alpha = 0f;
                    else if (d <= Ring) alpha = 1f;
                    else
                    {
                        float k = 1f - (d - Ring) / (GlowMargin - Ring);
                        alpha = k <= 0f ? 0f : 0.55f * k * k;
                    }

                    int i = y * w + x;
                    if (alpha <= 0f)
                    {
                        _glowPixels[i] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    // Around the button by angle, stretched to its shape so the colors spread evenly.
                    float angle = Mathf.Atan2((y + 0.5f - cy) / ButtonHeight, (x + 0.5f - cx) / ButtonWidth);
                    float hue = Mathf.Repeat(angle / (2f * Mathf.PI) + turn, 1f);
                    Color c = Color.HSVToRGB(hue, 0.7f, 1f);
                    _glowPixels[i] = new Color32((byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f),
                        (byte)(alpha * 255f));
                }
            }

            _glowTex.SetPixels32(_glowPixels);
            _glowTex.Apply(false);
        }

        // ------------------------------------------------------------------ the panel

        private void DrawHud()
        {
            EnsureStyles();
            if (_boats.Count == 0) return;

            Entry e = _boats[Mathf.Clamp(_index, 0, _boats.Count - 1)];
            const float Width = 400f, Pad = 16f;
            float inner = Width - 2f * Pad;

            string name = $"Boat {_index + 1} of {_boats.Count}: {e.Name}"
                + (e.CopyCount > 1 ? $" (copy {e.CopyIndex + 1} of {e.CopyCount})" : "");
            string summary = e.Summary ?? "Reading...";

            float height = Pad
                + _titleStyle.CalcHeight(new GUIContent("Hull showcase"), inner)
                + _textStyle.CalcHeight(new GUIContent(name), inner)
                + _textStyle.CalcHeight(new GUIContent(summary), inner)
                + 6f + 30f + Pad;
            var rect = new Rect(20f, 20f, Width, height);
            GUI.Box(rect, GUIContent.none, _panelStyle);

            GUILayout.BeginArea(new Rect(rect.x + Pad, rect.y + Pad - 4f, inner, rect.height - 2f * Pad + 8f));
            GUILayout.Label("Hull showcase", _titleStyle);
            GUILayout.Label(name, _textStyle);
            GUILayout.Label(summary, _textStyle);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Previous", _smallButtonStyle)) Step(-1);
            if (GUILayout.Button("Next", _smallButtonStyle)) Step(1);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Leave", _smallButtonStyle)) Stop("left with the button");
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void OnDisable()
        {
            if (_glowTex != null) Destroy(_glowTex);
            _glowTex = null;
        }

        // ------------------------------------------------------------------ guards

        /// <summary>The main menu's buttons do nothing while the showcase runs.</summary>
        [HarmonyPatch(typeof(StartMenu), nameof(StartMenu.ButtonClick), new Type[] { typeof(StartMenuButtonType) })]
        private static class BlockMenuButton
        {
            private static bool Prefix() => !Active;
        }

        /// <summary>The save slot buttons, which load a game, likewise.</summary>
        [HarmonyPatch(typeof(StartMenu), nameof(StartMenu.ButtonClick), new Type[] { typeof(int), typeof(int) })]
        private static class BlockSlotButton
        {
            private static bool Prefix() => !Active;
        }

        /// <summary>Nothing is saved while the showcase runs, whoever asks.</summary>
        [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveGame))]
        private static class BlockSave
        {
            private static bool Prefix()
            {
                if (!Active) return true;
                Plugin.Log.LogWarning("[Showcase] a save was asked for during the showcase and refused.");
                return false;
            }
        }

        /// <summary>Nor is anything loaded.</summary>
        [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadGame))]
        private static class BlockLoad
        {
            private static bool Prefix()
            {
                if (!Active) return true;
                Plugin.Log.LogWarning("[Showcase] a load was asked for during the showcase and refused.");
                return false;
            }
        }
    }
}
