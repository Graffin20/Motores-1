using UnityEngine;

namespace StarterAssets.SpringBones
{
    /// <summary>
    /// Sphere/capsule that spring-bone particles (SkirtSpringBones) are pushed out of. Not a UnityEngine
    /// Collider on purpose: a real collider on the player's bones would also block the CharacterController,
    /// hitbox overlaps, lock-on and interact casts. This one is only read by the spring-bone solver.
    ///
    /// Sizes are in WORLD metres and the shape follows only the transform's position and rotation, never its
    /// scale: imported Blender rigs often carry a 100x bone scale, which would make scaled values meaningless.
    /// </summary>
    public class SpringBoneCollider : MonoBehaviour
    {
        public enum Axis { X, Y, Z }

        [Tooltip("Radius in metres.")]
        [Min(0f)] public float Radius = 0.08f;

        [Tooltip("Total length in metres, caps included. At or below 2 * Radius it is a sphere.")]
        [Min(0f)] public float Height = 0.4f;

        [Tooltip("Local axis the capsule runs along. Blender bones point along +Y.")]
        public Axis Direction = Axis.Y;

        [Tooltip("Offset from this transform along its local axes, in metres (not scaled by the bone).")]
        public Vector3 Center = new Vector3(0f, 0.2f, 0f);

        private Vector3 LocalAxis => Direction == Axis.X ? Vector3.right : Direction == Axis.Y ? Vector3.up : Vector3.forward;

        public void GetSegment(out Vector3 a, out Vector3 b)
        {
            Quaternion rotation = transform.rotation;
            Vector3 center = transform.position + rotation * Center;
            Vector3 axis = rotation * LocalAxis;
            float halfSegment = Mathf.Max(0f, Height * 0.5f - Radius);
            a = center - axis * halfSegment;
            b = center + axis * halfSegment;
        }

        /// <summary>Moves <paramref name="point"/> to the surface if it is closer than Radius + particleRadius.</summary>
        public void PushOut(ref Vector3 point, float particleRadius)
        {
            GetSegment(out Vector3 a, out Vector3 b);
            Vector3 closest = ClosestPointOnSegment(point, a, b);
            Vector3 offset = point - closest;
            float minDistance = Radius + particleRadius;
            float sqrDistance = offset.sqrMagnitude;

            // Exactly on the axis there is no direction to push along; it resolves next frame.
            if (sqrDistance >= minDistance * minDistance || sqrDistance < 1e-10f) return;

            point = closest + offset * (minDistance / Mathf.Sqrt(sqrDistance));
        }

        /// <summary>
        /// Keeps the whole segment start -> end (a skirt bone) outside the capsule by rotating it about
        /// <paramref name="start"/> (its parent joint, already solved), preserving its length. Point-only
        /// collision let the capsule slip between two particles, which is how a raised thigh went through the
        /// top of the skirt.
        ///
        /// Side: normally the segment is pushed out the nearest way. But a fast limb (the knee end of a thigh)
        /// can carry a point deep inside, past the axis, where "nearest" is the wrong side and the skirt pops
        /// through the leg. The segment's animated/leg-driven target (<paramref name="targetStart"/> ->
        /// <paramref name="targetEnd"/>) says where it belongs: if that target is outside the capsule and on the
        /// other side of the axis, the point is pushed out on the target's side instead.
        /// </summary>
        public void PushOutSegment(Vector3 start, ref Vector3 end, float particleRadius, Vector3 targetStart, Vector3 targetEnd)
        {
            // Contacts closer to the pivot than this fraction of the segment get the move of this fraction:
            // a contact right at the pivot can't be fixed by rotating this segment (that's the parent's job).
            const float MinLeverage = 0.2f;

            GetSegment(out Vector3 a, out Vector3 b);
            ClosestPointsBetweenSegments(start, end, a, b, out float s, out Vector3 onBone, out Vector3 onAxis);

            Vector3 offset = onBone - onAxis;
            float minDistance = Radius + particleRadius;
            float sqrDistance = offset.sqrMagnitude;
            if (sqrDistance >= minDistance * minDistance) return;

            // Which side the target says this contact belongs on (only trusted when the target itself is clear of the capsule).
            Vector3 targetPoint = Vector3.Lerp(targetStart, targetEnd, s);
            Vector3 targetOffset = targetPoint - ClosestPointOnSegment(targetPoint, a, b);
            bool targetIsOutside = targetOffset.sqrMagnitude >= Radius * Radius;

            Vector3 push;
            if (targetIsOutside && (sqrDistance < 1e-10f || Vector3.Dot(offset, targetOffset) < 0f))
            {
                push = onAxis + targetOffset.normalized * minDistance - onBone;
            }
            else
            {
                if (sqrDistance < 1e-10f) return; // exactly on the axis with no usable hint: resolves next frame
                float distance = Mathf.Sqrt(sqrDistance);
                push = offset / distance * (minDistance - distance);
            }

            // Rotating about the pivot moves the contact point by s * (end's move), so the end moves by push / s.
            float length = Vector3.Distance(start, end);
            Vector3 movedEnd = end + push / Mathf.Max(s, MinLeverage);
            Vector3 direction = movedEnd - start;
            if (direction.sqrMagnitude < 1e-12f) return;
            end = start + direction.normalized * length;
        }

        /// <summary>Closest points between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision Detection 5.1.9). s = parameter on the first.</summary>
        private static void ClosestPointsBetweenSegments(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
                                                         out float s, out Vector3 c1, out Vector3 c2)
        {
            const float Epsilon = 1e-10f;
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float t;

            if (a <= Epsilon && e <= Epsilon)
            {
                s = t = 0f;
            }
            else if (a <= Epsilon)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= Epsilon)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denominator = a * e - b * b;
                    s = denominator > Epsilon ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }

            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float sqrLength = ab.sqrMagnitude;
            if (sqrLength < 1e-10f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / sqrLength);
            return a + ab * t;
        }

        private void OnDrawGizmosSelected()
        {
            GetSegment(out Vector3 a, out Vector3 b);
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireSphere(a, Radius);
            Gizmos.DrawWireSphere(b, Radius);

            if ((b - a).sqrMagnitude < 1e-10f) return;
            Vector3 axis = (b - a).normalized;
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 side2 = Vector3.Cross(axis, side);
            foreach (var s in new[] { side, -side, side2, -side2 })
                Gizmos.DrawLine(a + s * Radius, b + s * Radius);
        }
    }
}
