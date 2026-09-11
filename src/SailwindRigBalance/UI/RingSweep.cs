using SailwindRigBalance.Core;
using UnityEngine;

namespace SailwindRigBalance.UI
{
    /// <summary>
    /// One finished sweep of the balance ring, kept after the ring has been painted from it, so
    /// it can be read back at any wind angle.
    ///
    /// The ghost markers need this and cannot get it any other way. The ghost shows the rig as
    /// owned, and by the time it is drawn that rig no longer exists: the pending order has added,
    /// removed or resized the very sails it was made of, so it cannot be evaluated again. What
    /// survives is what was measured while it still existed, which is this: every bearing the
    /// ring evaluated, with the center of effort taken into the boat's own frame at the instant
    /// it was evaluated.
    ///
    /// A single reading at the arrow's angle would not do. The player swings the arrow to compare
    /// two rigs on a reach after setting them up close hauled, and a ghost captured at 45 degrees
    /// and still drawn at 90 would be comparing two different questions.
    /// </summary>
    internal sealed class RingSweep
    {
        /// <summary>Which sweep this was, so a caller can tell a fresh one from one it has seen.</summary>
        public readonly int Serial;

        /// <summary>The sweep's own yardsticks, so it can be painted the way the ring was.</summary>
        public readonly float MaxOutput;
        public readonly float BalancedMeters;

        private readonly RigBalance[] _sampled;
        private readonly bool[] _usable;
        private readonly Vector3[] _centerLocal;

        private readonly bool[] _evaluated;

        public RingSweep(int serial, RigBalance[] sampled, bool[] evaluated, Vector3[] centerLocal,
            float maxOutput, float balancedMeters)
        {
            Serial = serial;
            MaxOutput = maxOutput;
            BalancedMeters = balancedMeters;
            _evaluated = (bool[])evaluated.Clone();
            int n = sampled.Length;
            _sampled = (RigBalance[])sampled.Clone();
            _centerLocal = (Vector3[])centerLocal.Clone();
            _usable = new bool[n];

            // A bearing where no sail drew has no center of effort at all: the combined center is
            // a weighted mean with zero total weight, and whatever it holds is not a place. The
            // ring paints those gray; here they are simply not used.
            for (int i = 0; i < n; i++)
                _usable[i] = evaluated[i] && sampled[i].Sails != null && sampled[i].Sails.Count > 0;
        }

        /// <summary>The ring's colors for this sweep, bearing by bearing, as the ring painted them.</summary>
        public Color[] Colors()
        {
            var colors = new Color[_sampled.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = _evaluated[i]
                    ? BalanceRing.VerdictFor(_sampled[i], MaxOutput, BalancedMeters)
                    : BalanceRing.NoVerdictColor;
            }
            return colors;
        }

        /// <summary>
        /// Whether two sweeps describe the same rig as far as anything drawn from them can show:
        /// the same bearings drawing, the center of effort within five centimeters, the same helm
        /// to the centimeter and the same kind of verdict. Used to decide whether a ghost ring
        /// would show anything, so an order that changes nothing the ring can see draws none.
        /// </summary>
        public bool SameAs(RingSweep other)
        {
            if (other == null || other._sampled.Length != _sampled.Length) return false;

            for (int i = 0; i < _sampled.Length; i++)
            {
                if (_usable[i] != other._usable[i]) return false;
                if (!_usable[i]) continue;

                RigBalance a = _sampled[i], b = other._sampled[i];
                if ((_centerLocal[i] - other._centerLocal[i]).magnitude > 0.05f) return false;
                if (a.HelmMeaningful != b.HelmMeaningful || a.HelmOffScale != b.HelmOffScale) return false;
                if (Mathf.Abs(a.HelmMeters - b.HelmMeters) > 0.01f) return false;
            }

            return true;
        }

        /// <summary>
        /// The reading at <paramref name="degrees"/> off the bow, interpolated between the two
        /// evaluated bearings either side of it the same way the ring's colors are. Exact at the
        /// bearings themselves and close between them; a trim that flips between two neighbors
        /// is the one place the in-between value is an average of two answers rather than an
        /// answer. False when neither neighbor has a rig that is drawing.
        /// </summary>
        public bool TryAt(float degrees, out Vector3 centerLocal, out float helmMeters, out bool meaningful)
        {
            centerLocal = Vector3.zero;
            helmMeters = 0f;
            meaningful = false;

            int n = _sampled.Length;
            if (n == 0) return false;

            // Sample i covers bearing 360 * (i + 0.5) / n, so the position between samples is
            // shifted by half a step.
            float position = Mathf.Repeat(degrees, 360f) / 360f * n - 0.5f;
            int i0 = Mathf.FloorToInt(position);
            float t = position - i0;
            i0 = ((i0 % n) + n) % n;
            int i1 = (i0 + 1) % n;

            bool u0 = _usable[i0], u1 = _usable[i1];
            if (!u0 && !u1) return false;
            if (!u0) t = 1f;
            else if (!u1) t = 0f;

            centerLocal = Vector3.Lerp(_centerLocal[i0], _centerLocal[i1], t);

            // A helm figure only where both ends of the interpolation have one. Lerping a real
            // lever toward a suppressed one would invent a reading halfway between an answer and
            // a refusal to answer.
            RigBalance a = _sampled[i0], b = _sampled[i1];
            if (t <= 0f) { meaningful = a.HelmMeaningful; helmMeters = a.HelmMeters; }
            else if (t >= 1f) { meaningful = b.HelmMeaningful; helmMeters = b.HelmMeters; }
            else if (a.HelmMeaningful && b.HelmMeaningful)
            {
                meaningful = true;
                helmMeters = Mathf.Lerp(a.HelmMeters, b.HelmMeters, t);
            }

            return true;
        }
    }
}
