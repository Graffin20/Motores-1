using System.Collections.Generic;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// The damaging part of a weapon (a sword blade, an enemy's hand/claws) as a capsule from Base to Tip.
    /// MeleeCombatController traces it during an attack's Active window (AE_AttackActive -> AE_AttackRecovery)
    /// instead of using its one-frame hitbox sphere, so hits follow the animation.
    ///
    /// Swept, not sampled: a fast swing can move the tip half a metre in one frame and skip straight past an
    /// arm, so each step checks capsules interpolated between last frame's and this frame's blade, at most
    /// MaxStepDistance apart (measured at whichever end moved more, normally the tip).
    ///
    /// Base and Tip are Transforms (empty children placed on the weapon/hand) rather than local offsets, so
    /// the bones' 100x scale doesn't matter; Radius is in world metres.
    /// </summary>
    public class WeaponTrace : MonoBehaviour
    {
        [Tooltip("Blade start (e.g. just above the guard, or the wrist for a claw swipe).")]
        public Transform Base;
        [Tooltip("Blade end (sword tip, fingertips).")]
        public Transform Tip;
        [Tooltip("Capsule radius in world metres. A little thicker than the mesh feels fairer than exact.")]
        public float Radius = 0.08f;
        [Tooltip("Max distance the blade end may move between two checks; smaller = no gaps on fast swings, more queries.")]
        public float MaxStepDistance = 0.1f;
        [Tooltip("Cap on checks per frame (protects against huge jumps, e.g. a hitch or a teleport).")]
        public int MaxStepsPerFrame = 12;

        private const int BufferSize = 32;
        private readonly Collider[] _buffer = new Collider[BufferSize];
        private Vector3 _previousBase, _previousTip;
        private bool _checkStartPose;

        public bool IsValid => Base != null && Tip != null;

        /// <summary>Starts a new trace from the blade's current pose (checked on the first Sweep).</summary>
        public void BeginTrace()
        {
            if (!IsValid) return;
            _previousBase = Base.position;
            _previousTip = Tip.position;
            _checkStartPose = true;
        }

        /// <summary>Adds every collider (on mask, triggers ignored) the blade touched since the last call.
        /// May add the same collider more than once; the caller dedupes per target.</summary>
        public void Sweep(LayerMask mask, List<Collider> results)
        {
            if (!IsValid) return;

            Vector3 basePos = Base.position, tipPos = Tip.position;
            float moved = Mathf.Max((basePos - _previousBase).magnitude, (tipPos - _previousTip).magnitude);
            int steps = Mathf.Clamp(Mathf.CeilToInt(moved / Mathf.Max(0.01f, MaxStepDistance)), 1, Mathf.Max(1, MaxStepsPerFrame));

            for (int i = _checkStartPose ? 0 : 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                int count = Physics.OverlapCapsuleNonAlloc(Vector3.Lerp(_previousBase, basePos, t),
                                                           Vector3.Lerp(_previousTip, tipPos, t),
                                                           Radius, _buffer, mask, QueryTriggerInteraction.Ignore);
                for (int c = 0; c < count; c++) results.Add(_buffer[c]);
            }

            _checkStartPose = false;
            _previousBase = basePos;
            _previousTip = tipPos;
        }

        private void OnDrawGizmosSelected()
        {
            if (!IsValid) return;
            Gizmos.color = Color.red;
            Vector3 a = Base.position, b = Tip.position;
            Gizmos.DrawWireSphere(a, Radius);
            Gizmos.DrawWireSphere(b, Radius);

            // Four lines along the capsule's sides.
            Vector3 axis = b - a;
            if (axis.sqrMagnitude < 1e-8f) return;
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis.normalized, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized * Radius;
            Vector3 side2 = Vector3.Cross(axis.normalized, side);
            foreach (var offset in new[] { side, -side, side2, -side2 })
                Gizmos.DrawLine(a + offset, b + offset);
        }
    }
}
