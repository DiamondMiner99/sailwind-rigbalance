using System;
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
        /// Vertical gap below the anchor button, as a multiple of the anchor's own height. The
        /// game's UI is world-space and each hull's shipyard panel is a different size, so this is
        /// expressed relative to the button rather than in metres.
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

                // Strip every button behaviour the clone inherited, including SE's, before adding
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
        /// The furl-all button, identified by its label rather than by its type, so this compiles
        /// and runs without a reference to Shipyard Expansion. Any mod that adds a furl-all button
        /// with that wording would serve equally well as an anchor.
        /// </summary>
        private static GoPointerButton FindFurlAllButton()
        {
            // Once per shipyard entry, never per frame.
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

        private static float HeightOf(GameObject go)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return 0.25f;

            // Local height, so the offset survives whatever scale the shipyard panel is drawn at.
            Vector3 world = renderer.bounds.size;
            Vector3 local = go.transform.InverseTransformVector(world);
            return Mathf.Max(Mathf.Abs(local.y), 0.05f);
        }

        private void Update()
        {
            Sail selected = SelectedSail();
            bool show = selected != null;

            // "which appears only when a sail is selected"
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.enabled != show) renderer.enabled = show;
            }
            var collider = GetComponent<Collider>();
            if (collider != null && collider.enabled != show) collider.enabled = show;

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

            selected.currentUnroll = selected.currentUnroll > 0.5f ? 0f : 1f;

            // Mirrors what Shipyard Expansion does after writing the field. The boom and cloth reef
            // effects poll currentUnroll every Update and need no prompting, but the animation
            // driven one does, and not every sail carries it.
            try
            {
                var reef = selected.GetComponent<ReefEffectAnimUniversal>();
                if (reef != null) reef.RefreshCloth();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("[Furl] reef refresh failed: " + e.Message);
            }

            ShipyardHooks.MarkDirty();
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
