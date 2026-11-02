using System.Collections.Generic;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>One collider touched by a weapon trace step, with where and in which direction.</summary>
    public struct TraceHit
    {
        public Collider Collider;
        public Vector3 Point;
        public Vector3 Direction;
    }

    /// <summary>
    /// The damaging part of a weapon (a sword blade, an enemy's hand/claws) as a capsule from Base to Tip.
    /// MeleeCombatController traces it during an attack's Active window (AE_AttackActive -> AE_AttackRecovery)
    /// instead of using its one-frame hitbox sphere, so hits follow the animation. IsTracing is true during
    /// that window (WeaponTrail emits while it is).
    ///
    /// Swept, not sampled: a fast swing can move the tip half a metre in one frame and skip straight past an
    /// arm, so each step checks capsules interpolated between last frame's and this frame's blade, at most
    /// MaxStepDistance apart (measured at whichever end moved more, normally the tip).
    ///
    /// Contact point: the overlap query only says WHICH collider was touched, so the point is found from the
    /// blade capsule of the step that touched it: the collider's closest surface point to the blade segment
    /// (Collider.ClosestPoint, two refinement passes). Direction = how the blade was moving at that step.
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

        /// <summary>True from BeginTrace to EndTrace (the attack's Active window).</summary>
        public bool IsTracing { get; private set; }

        /// <summary>Starts a new trace from the blade's current pose (checked on the first Sweep).</summary>
        public void BeginTrace()
        {
            if (!IsValid) return;
            _previousBase = Base.position;
            _previousTip = Tip.position;
            _checkStartPose = true;
            IsTracing = true;
        }

        public void EndTrace() => IsTracing = false;

        /// <summary>Adds every collider (on mask, triggers ignored) the blade touched since the last call, with
        /// its contact point. May add the same collider more than once; the caller dedupes per target and the
        /// first entry (earliest step) is the real contact.</summary>
        public void Sweep(LayerMask mask, List<TraceHit> results)
        {
            if (!IsValid) return;

            Vector3 basePos = Base.position, tipPos = Tip.position;
            float moved = Mathf.Max((basePos - _previousBase).magnitude, (tipPos - _previousTip).magnitude);
            int steps = Mathf.Clamp(Mathf.CeilToInt(moved / Mathf.Max(0.01f, MaxStepDistance)), 1, Mathf.Max(1, MaxStepsPerFrame));

            // Blade movement this frame, used as the hit direction (tip motion, falling back to the blade axis
            // for a perfectly still blade, e.g. the start pose).
            Vector3 motion = tipPos - _previousTip;
            Vector3 direction = motion.sqrMagnitude > 1e-8f ? motion.normalized : (tipPos - basePos).normalized;

            for (int i = _checkStartPose ? 0 : 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector3 a = Vector3.Lerp(_previousBase, basePos, t);
                Vector3 b = Vector3.Lerp(_previousTip, tipPos, t);
                int count = Physics.OverlapCapsuleNonAlloc(a, b, Radius, _buffer, mask, QueryTriggerInteraction.Ignore);
                for (int c = 0; c < count; c++)
                {
                    results.Add(new TraceHit
                    {
                        Collider = _buffer[c],
                        Point = ContactPoint(_buffer[c], a, b),
                        Direction = direction,
                    });
                }
            }

            _checkStartPose = false;
            _previousBase = basePos;
            _previousTip = tipPos;
        }

        /// <summary>Closest point on the collider's surface to the blade segment a-b (or a point inside it if
        /// the blade is already inside).</summary>
        public static Vector3 ContactPoint(Collider collider, Vector3 a, Vector3 b)
        {
            // ClosestPoint only supports primitives and convex meshes.
            bool supported = !(collider is MeshCollider mesh) || mesh.convex;

            Vector3 onBlade = ClosestOnSegment(a, b, collider.bounds.center);
            Vector3 onCollider = supported ? collider.ClosestPoint(onBlade) : collider.ClosestPointOnBounds(onBlade);
            onBlade = ClosestOnSegment(a, b, onCollider); // refine once toward the actual surface
            return supported ? collider.ClosestPoint(onBlade) : collider.ClosestPointOnBounds(onBlade);
        }

        private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float lengthSq = ab.sqrMagnitude;
            if (lengthSq < 1e-8f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSq);
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
