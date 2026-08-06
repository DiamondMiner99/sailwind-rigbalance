using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using SailwindRigBalance.Compat;
using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.Shipyard
{
    /// <summary>
    /// Furls or sets the one sail the player has selected, from a button that lives in the
    /// shipyard's own UI directly beneath Shipyard Expansion's "Furl Sails" button.
    ///
    /// It is a clone of that button rather than anything drawn by this mod, so it matches the
    /// game's paper-and-ink look exactly and sits in the layout the player already reads. The
    /// game's UI is world-space, not a canvas, so there is no screen rectangle to place it in;
    /// cloning an existing object is the only way to get the styling right without shipping art.
    ///
    /// It holds no state. Whether it reads "Furl" or "Unfurl" comes from Sail.currentUnroll every
    /// frame, so it cannot disagree with SE's furl-all button, and it survives a customization
    /// being applied (which destroys every sail and recreates it at currentUnroll = 1, Sail.cs:80).
    /// </summary>
    public class FurlSailButton : GoPointerButton
    {
        private TextMesh _label;
        private static FurlSailButton _instance;

        /// <summary>
        /// Cached in TryCreate rather than gathered every frame. The clone's hierarchy never
        /// changes, and this ran GetComponentsInChildren for the whole shipyard session purely to
        /// toggle a bool, allocating a fresh array each frame.
        /// </summary>
        private Renderer[] _renderers;
        private Collider _collider;

        /// <summary>
        /// Vertical gap below the anchor button, as a multiple of the anchor's own height. The
        /// game's UI is world-space and each hull's shipyard panel is a different size, so this is
        /// expressed relative to the button rather than in meters.
        /// </summary>
        private const float GapMultiplier = 1.15f;

        public static bool Exists => _instance != null;

        /// <summary>
        /// Clones the shipyard's furl-all button, if one is present, and hangs the clone underneath
        /// it. Returns false when no anchor was found, which is the normal case without Shipyard
        /// Expansion installed; the caller then falls back to its own panel button.
        /// </summary>
        public static bool TryCreate()
        {
            if (_instance != null) return true;

            try
            {
                GoPointerButton anchor = FindFurlAllButton();
                if (anchor == null) return false;

                // Instantiate from an INACTIVE source so no Awake runs on the clone. Shipyard
                // Expansion's button caches its TextMesh into a STATIC field in Awake
                // (ShipyardUnfurlButton.cs:9 and :14), so letting a clone's Awake fire would point
                // SE's own label reference at our copy, and SE would rename our button instead of
                // its own on every furl-all press.
                bool wasActive = anchor.gameObject.activeSelf;
                anchor.gameObject.SetActive(false);
                GameObject clone = Instantiate(anchor.gameObject, anchor.transform.parent);
                anchor.gameObject.SetActive(wasActive);

                clone.name = "RigBalanceFurlSelectedSail";

                // Strip every button behavior the clone inherited, including SE's, before adding
                // ours. Otherwise a click would furl the whole rig as well as the selected sail.
                foreach (GoPointerButton inherited in clone.GetComponents<GoPointerButton>())
                {
                    DestroyImmediate(inherited);
                }

                clone.transform.localPosition = anchor.transform.localPosition
                    + new Vector3(0f, -HeightOf(anchor.gameObject) * GapMultiplier, 0f);
                clone.transform.localRotation = anchor.transform.localRotation;
                clone.transform.localScale = anchor.transform.localScale;

                _instance = clone.AddComponent<FurlSailButton>();
                _instance._label = clone.GetComponentInChildren<TextMesh>();
                _instance._renderers = clone.GetComponentsInChildren<Renderer>(true);
                _instance._collider = clone.GetComponent<Collider>();
                clone.SetActive(true);

                Plugin.Log.LogInfo($"[Furl] button cloned from '{anchor.name}' and placed beneath it.");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Furl] could not create the per-sail furl button: " + e.Message);
                _instance = null;
                return false;
            }
        }

        public static void Destroy()
        {
            if (_instance == null) return;
            try { Destroy(_instance.gameObject); }
            catch { }
            _instance = null;
        }

        /// <summary>
        /// The furl-all button. With Shipyard Expansion present it is found BY TYPE and nothing
        /// else in the scene is touched; the label scan below is the fallback for any other mod
        /// that adds a furl-all button, and for an SE that has renamed its class.
        ///
        /// The scan used to be the only path, and it is what a field crash report caught: it walks
        /// the children of every GoPointerButton in the scene, and Unity took a native access
        /// violation inside that walk, on an object some other shipyard-UI mod had not finished
        /// constructing, which passed the managed null check with nothing native behind it. That
        /// cannot be caught from managed code. So when SE is installed and its button is not up
        /// yet, this returns nothing and lets the caller's retry clock come back, rather than
        /// falling through to a scene-wide walk that buys an SE install nothing: SE's is the only
        /// furl-all button that will ever exist there.
        /// </summary>
        private static GoPointerButton FindFurlAllButton()
        {
            if (ModDetect.ShipyardExpansionInstalled)
            {
                Type seType = ResolveSeButtonType();
                if (seType != null) return FindObjectOfType(seType) as GoPointerButton;
                // SE is present but the class is not where this build expects: scan after all.
            }

            return ScanForFurlAllButton();
        }

        /// <summary>
        /// Shipyard Expansion's furl-all button class. ShipyardExpansion.Scripts
        /// .ShipyardUnfurlButton derives from GoPointerButton (SE source,
        /// ShipyardUnfurlButton.cs:7) and SE itself writes its label (ShipyardUnfurlButton.cs:26),
        /// so the type alone identifies the button. Resolved through the chainloader so this
        /// compiles and runs without a reference to SE.
        /// </summary>
        private static Type ResolveSeButtonType()
        {
            try
            {
                BepInEx.PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue(ModDetect.ShipyardExpansionGuid, out info)
                    || info == null || info.Instance == null)
                {
                    return null;
                }

                Type type = info.Instance.GetType().Assembly
                    .GetType("ShipyardExpansion.Scripts.ShipyardUnfurlButton", false);

                if (type == null && !_seTypeMissingLogged)
                {
                    _seTypeMissingLogged = true;
                    Plugin.Log.LogWarning("[Furl] Shipyard Expansion is installed but "
                        + "ShipyardUnfurlButton is not where this build expects; falling back to "
                        + "the label scan.");
                }
                return type;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Furl] SE button lookup failed: " + e.Message);
                return null;
            }
        }

        private static bool _seTypeMissingLogged;

        /// <summary>
        /// The generic path: identified by label rather than by type, so any mod's furl-all button
        /// serves as an anchor. Walks every button in the scene, which is why the caller only runs
        /// it after the admit-frame construction window has passed and only when the typed lookup
        /// does not apply.
        /// </summary>
        private static GoPointerButton ScanForFurlAllButton()
        {
            foreach (GoPointerButton button in FindObjectsOfType<GoPointerButton>())
            {
                if (button == null) continue;
                var text = button.GetComponentInChildren<TextMesh>();
                if (text == null || string.IsNullOrEmpty(text.text)) continue;

                string label = text.text.Replace("\n", " ");
                if (label.IndexOf("furl", StringComparison.OrdinalIgnoreCase) >= 0
                    && label.IndexOf("sails", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return button;
                }
            }
            return null;
        }

        /// <summary>
        /// The anchor button's height expressed in its PARENT's space, because that is the space the
        /// caller adds the gap in.
        ///
        /// It used to take renderer.bounds.size, which is the size of a world-space axis-aligned box
        /// and not a direction, and push it through the button's own InverseTransformVector as
        /// though it were one, then use the result as an offset in the parent's frame: three
        /// coordinate systems in one expression, correct only when the button's local rotation is
        /// identity and its world box happens to line up with it. The shipyard's UI is world-space
        /// geometry tilted toward the camera, so it was neither, and the clone landed overlapping
        /// the anchor or below the panel.
        /// </summary>
        private static float HeightOf(GameObject go)
        {
            // The mesh's own bounds are in the object's local space, so scaling them by the object's
            // own scale gives a height in the PARENT's space with no world-space box involved. This
            // assumes the button is not rotated relative to the panel it sits on, which holds by
            // construction for a column of sibling buttons laid out along the panel's own axes.
            var filter = go.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return 0.25f;

            float height = mesh.bounds.size.y * Mathf.Abs(go.transform.localScale.y);
            return height > 0.001f ? Mathf.Max(height, 0.05f) : 0.25f;
        }

        private void Update()
        {
            Sail selected = SelectedSail();
            bool show = selected != null;

            // "which appears only when a sail is selected"
            if (_renderers != null)
            {
                foreach (Renderer renderer in _renderers)
                {
                    if (renderer != null && renderer.enabled != show) renderer.enabled = show;
                }
            }
            if (_collider != null && _collider.enabled != show) _collider.enabled = show;

            if (!show || _label == null) return;

            // Wording is deliberately not one letter away from SE's "Furl Sails". "this sail"
            // states the scope outright, because the two buttons sit next to each other.
            string wanted = selected.currentUnroll > 0.5f ? "Furl\nthis sail" : "Unfurl\nthis sail";
            if (_label.text != wanted) _label.text = wanted;

            lookText = wanted.Replace("\n", " ");
        }

        public override void OnActivate()
        {
            Sail selected = SelectedSail();
            if (selected == null) return;

            SetUnroll(ShipyardHooks.CurrentBoat, selected, selected.currentUnroll > 0.5f ? 0f : 1f);
        }

        /// <summary>
        /// Sets one sail's furl state, and with it every sail the game will drag along.
        ///
        /// RopeControllerMirror.Update copies parentSail.currentUnroll onto its own sail every
        /// frame, unconditionally and with no shipyard guard (RopeControllerMirror.cs:22). Writing
        /// only the selected sail of a mirrored pair therefore either did nothing at all, because
        /// the next frame put it back before the gizmo's quarter-second rebuild delay had elapsed,
        /// or moved half the area the player was asking about. Either way the button read as dead
        /// and the markers did not move, with nothing in the log to say why.
        ///
        /// So the whole chained set is written, which is what the game is going to do anyway, and
        /// the result is checked on the next frame: if something else owns the field after all, the
        /// player is told rather than left guessing.
        /// </summary>
        public static void SetUnroll(GameObject boatRoot, Sail sail, float unroll)
        {
            if (sail == null) return;

            HashSet<Sail> group = RigModel.UnrollGroupOf(boatRoot, sail);
            foreach (Sail member in group)
            {
                if (member == null) continue;
                member.currentUnroll = unroll;

                // Mirrors what Shipyard Expansion does after writing the field. The boom and cloth
                // reef effects poll currentUnroll every Update and need no prompting, but the
                // animation driven one does, and not every sail carries it.
                try
                {
                    var reef = member.GetComponent<ReefEffectAnimUniversal>();
                    if (reef != null) reef.RefreshCloth();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("[Furl] reef refresh failed: " + e.Message);
                }
            }

            if (group.Count > 1)
            {
                Plugin.Log.LogInfo($"[Furl] '{sail.sailName}' is mirrored; set {group.Count} sails "
                    + $"to unroll {unroll:F2} together.");
            }

            UnrollWatch.Watch(sail, unroll);
            ShipyardHooks.MarkDirty("furl button");
        }

        /// <summary>
        /// The sail the player has selected, via the game's own accessor, which is how vanilla's
        /// ObstructionOutline decides which sail it belongs to (ObstructionOutline.cs:13).
        /// </summary>
        public static Sail SelectedSail()
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
    }
}
