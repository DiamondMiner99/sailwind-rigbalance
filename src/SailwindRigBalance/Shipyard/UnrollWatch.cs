using UnityEngine;

namespace SailwindRigBalance.Shipyard
{
    /// <summary>
    /// Checks, a few frames after a furl, that the value the mod wrote is still there.
    ///
    /// Sail.currentUnroll is a public field with several owners, and two of them can write it back
    /// without asking. RopeControllerMirror.Update copies its parent sail's value onto its own every
    /// frame (RopeControllerMirror.cs:22); the furl path handles that by writing the whole mirrored
    /// set, so this is the check that the handling worked. RopeControllerSailReef.UpdateSailUnroll
    /// forces an uninstalled shipyard sail back to 1 (RopeControllerSailReef.cs:103-106) whenever
    /// its reef rope has moved since the last frame.
    ///
    /// The gizmo does not look at the result for a quarter second, so without this a write that was
    /// silently undone looked exactly like a sail whose furl state does not affect the balance: the
    /// button appeared dead, the markers did not move, and nothing in the log distinguished the two.
    /// A warning naming the sail is the whole point; there is nothing useful to do about it beyond
    /// saying so, because whichever component reclaimed the field owns it by design.
    /// </summary>
    public static class UnrollWatch
    {
        private const int SettleFrames = 3;

        private static Sail _sail;
        private static float _wanted;
        private static int _framesLeft;

        public static void Watch(Sail sail, float unroll)
        {
            _sail = sail;
            _wanted = unroll;
            _framesLeft = SettleFrames;
        }

        public static void Clear()
        {
            _sail = null;
            _framesLeft = 0;
        }

        /// <summary>Called once per frame from the gizmo, which is already running an Update.</summary>
        public static void Tick()
        {
            if (_framesLeft <= 0) return;

            if (_sail == null)
            {
                // The sail was destroyed, which a customization does to every sail on the boat.
                // Nothing to report.
                Clear();
                return;
            }

            if (--_framesLeft > 0) return;

            Sail sail = _sail;
            float wanted = _wanted;
            Clear();

            if (Mathf.Abs(sail.currentUnroll - wanted) < 0.01f) return;

            string name = string.IsNullOrEmpty(sail.sailName) ? sail.name : sail.sailName;
            Plugin.Log.LogWarning($"[Furl] '{name}' was set to unroll {wanted:F2} but reads "
                + $"{sail.currentUnroll:F2} {SettleFrames} frames later, so something else on the "
                + "boat owns that field. The balance reading is for the state the sail is actually "
                + "in, not the one the button asked for.");
        }
    }
}
