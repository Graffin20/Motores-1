using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StarterAssets.SpringBones
{
    /// <summary>
    /// General-purpose spring bones: jiggle bones (one bone), hair strands, tails, ribbons. Each root is an
    /// independent chain (root -> first child -> ...); unlike SkirtSpringBones there are no links between
    /// chains and no leg drivers, so a single bone is enough.
    ///
    /// Same model as the skirt: every node is a Verlet particle pulled toward the bone's rest pose (relative to
    /// its moving parent), with inertia, gravity and damping; the bones are then only rotated to point at the
    /// particles, so nothing stretches. The root's position stays attached to its parent; only its rotation
    /// is simulated.
    ///
    /// The last bone of a chain has no child to point at, so a virtual tip TailLength along its local axis is
    /// added. Blender exports carry "_end" leaf bones, which are used instead when present.
    ///
    /// Bones are restored to their rest rotation every frame, so this suits bones the Animator doesn't drive
    /// (anything outside the Humanoid Avatar: breasts, hair, cloth). Animated bones would lose their animation.
    /// </summary>
    [DefaultExecutionOrder(10000)] // after other LateUpdate pose changes (IK etc.)
    public class SpringBoneChains : MonoBehaviour
    {
        [Header("Bones")]
        [Tooltip("First bone of each chain (e.g. DEF-breast.L, a hair strand's root). The bone itself rotates; everything below it swings.")]
        public List<Transform> Roots = new List<Transform>();

        [Tooltip("Length in metres of the virtual tip added after a bone with no children.")]
        [Min(0.001f)] public float TailLength = 0.1f;

        [Tooltip("Local axis the virtual tip extends along. Blender bones point along +Y.")]
        public SpringBoneCollider.Axis TailAxis = SpringBoneCollider.Axis.Y;

        [Header("Dynamics")]
        [Tooltip("Hair: full gravity. Jiggle bones: 0 or very little, otherwise they sag.")]
        public Vector3 Gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip("Fraction of velocity lost per 1/60 s. Low = bouncy and wobbly (jiggle), high = calm.")]
        [Range(0f, 1f)] public float Damping = 0.1f;

        [Tooltip("Pull toward the rest pose per 1/60 s at the root. Jiggle: 0.1-0.2. Hair: 0.05-0.1.")]
        [Range(0f, 1f)] public float Stiffness = 0.1f;

        [Tooltip("Pull at the last node; in between is interpolated. Lower than Stiffness = looser toward the ends (hair).")]
        [Range(0f, 1f)] public float TipStiffness = 0.05f;

        [Tooltip("Max angle (degrees) a segment may bend away from its rest direction. Jiggle: 15-30. Hair: 60-90.")]
        [Range(0f, 180f)] public float MaxAngle = 60f;

        [Header("Character Movement")]
        [Tooltip("How much of the parent bone's movement and turning the chain lags behind. 0 = rigid, 1 = full world-space inertia. " +
                 "Rolls and knockback move the character metres in a fraction of a second; full inertia flings things.")]
        [Range(0f, 1f)] public float MovementInfluence = 0.5f;

        [Tooltip("If a chain's parent moves more than this (metres) in one frame the chain resets (teleport, respawn).")]
        [Min(0f)] public float TeleportDistance = 2f;

        [Header("Collision")]
        public List<SpringBoneCollider> Colliders = new List<SpringBoneCollider>();

        [Tooltip("Thickness for collision, in metres.")]
        [Min(0f)] public float ParticleRadius = 0.02f;

        [Header("Solver")]
        [Tooltip("Largest timestep simulated in one frame; slower frames run in slow motion instead of exploding.")]
        [Min(0.001f)] public float MaxDeltaTime = 1f / 30f;

        private class Chain
        {
            public Transform Anchor;              // the root's parent: what the chain hangs from
            public Transform[] Bones;             // real bones, root first
            public Quaternion[] RestLocalRotations;
            public bool HasVirtualTip;            // last node is TailLength past the last bone, not a bone
            public float[] Lengths;               // Lengths[i] = rest distance node i-1 -> node i
            public Vector3[] Positions;           // one per node (bones + optional virtual tip)
            public Vector3[] PreviousPositions;
            public Vector3[] Targets;
            public Vector3 PreviousAnchorPosition;
            public Quaternion PreviousAnchorRotation;
        }

        private Chain[] _chains = new Chain[0];

        private Vector3 LocalTailAxis =>
            TailAxis == SpringBoneCollider.Axis.X ? Vector3.right : TailAxis == SpringBoneCollider.Axis.Y ? Vector3.up : Vector3.forward;

        private void Awake()
        {
            var chains = new List<Chain>();
            foreach (var root in Roots.Where(r => r != null).Distinct())
            {
                var bones = new List<Transform> { root };
                for (Transform t = root; t.childCount > 0; t = t.GetChild(0)) bones.Add(t.GetChild(0));

                // Blender "_end" leaves already give the last real bone its direction; anything else gets a virtual tip.
                bool virtualTip = bones.Count == 1 || !bones[bones.Count - 1].name.EndsWith("_end");
                int nodes = bones.Count + (virtualTip ? 1 : 0);
                var chain = new Chain
                {
                    Anchor = root.parent != null ? root.parent : root,
                    Bones = bones.ToArray(),
                    RestLocalRotations = bones.Select(b => b.localRotation).ToArray(),
                    HasVirtualTip = virtualTip,
                    Lengths = new float[nodes],
                    Positions = new Vector3[nodes],
                    PreviousPositions = new Vector3[nodes],
                    Targets = new Vector3[nodes],
                };
                ReadTargets(chain);
                for (int i = 1; i < nodes; i++) chain.Lengths[i] = Vector3.Distance(chain.Targets[i - 1], chain.Targets[i]);
                chains.Add(chain);
            }
            _chains = chains.ToArray();

            if (_chains.Length == 0)
            {
                Debug.LogWarning("[SpringBoneChains] No roots assigned; disabled.", this);
                enabled = false;
                return;
            }
            ResetSimulation();
        }

        private void OnEnable()
        {
            ResetSimulation();
        }

        /// <summary>Puts every particle back on the rest pose with no velocity. Call after teleporting the character.</summary>
        public void ResetSimulation()
        {
            foreach (var chain in _chains) ResetChain(chain);
        }

        private void ResetChain(Chain chain)
        {
            RestoreRestPose(chain);
            ReadTargets(chain);
            for (int i = 0; i < chain.Positions.Length; i++) chain.Positions[i] = chain.PreviousPositions[i] = chain.Targets[i];
            chain.PreviousAnchorPosition = chain.Anchor.position;
            chain.PreviousAnchorRotation = chain.Anchor.rotation;
        }

        private void LateUpdate()
        {
            float dt = Mathf.Min(Time.deltaTime, MaxDeltaTime);
            if (dt <= 0f) return;

            float frames = dt * 60f; // per-1/60 s rates -> this frame
            float velocityKept = Mathf.Pow(1f - Damping, frames);
            Vector3 gravityStep = Gravity * (dt * dt);
            float carry = 1f - MovementInfluence;

            foreach (var chain in _chains)
            {
                if ((chain.Anchor.position - chain.PreviousAnchorPosition).sqrMagnitude > TeleportDistance * TeleportDistance)
                {
                    ResetChain(chain);
                    continue;
                }

                RestoreRestPose(chain);
                ReadTargets(chain);
                int nodes = chain.Positions.Length;

                // Carry part of the parent's movement (translation and rotation).
                if (carry > 0f)
                {
                    Quaternion deltaRotation = chain.Anchor.rotation * Quaternion.Inverse(chain.PreviousAnchorRotation);
                    for (int i = 1; i < nodes; i++)
                    {
                        chain.Positions[i] = Vector3.Lerp(chain.Positions[i], Carry(chain, chain.Positions[i], deltaRotation), carry);
                        chain.PreviousPositions[i] = Vector3.Lerp(chain.PreviousPositions[i], Carry(chain, chain.PreviousPositions[i], deltaRotation), carry);
                    }
                }

                // Integrate. Node 0 is the root joint: it stays attached to its parent.
                chain.Positions[0] = chain.PreviousPositions[0] = chain.Targets[0];
                for (int i = 1; i < nodes; i++)
                {
                    Vector3 position = chain.Positions[i];
                    Vector3 velocity = (position - chain.PreviousPositions[i]) * velocityKept;
                    chain.PreviousPositions[i] = position;
                    position += velocity + gravityStep;

                    float stiffness = nodes > 2 ? Mathf.Lerp(Stiffness, TipStiffness, (i - 1f) / (nodes - 2f)) : Stiffness;
                    float pull = 1f - Mathf.Pow(1f - stiffness, frames);
                    chain.Positions[i] = Vector3.Lerp(position, chain.Targets[i], pull);
                }

                // Constraints top-down: length + max angle, then collision (no links between chains, so one pass is exact).
                float maxRadians = MaxAngle * Mathf.Deg2Rad;
                for (int i = 1; i < nodes; i++)
                {
                    Vector3 parent = chain.Positions[i - 1];
                    Vector3 restDirection = (chain.Targets[i] - chain.Targets[i - 1]).normalized;
                    Vector3 direction = chain.Positions[i] - parent;
                    direction = direction.sqrMagnitude > 1e-12f ? direction.normalized : restDirection;
                    if (MaxAngle < 180f) direction = Vector3.RotateTowards(restDirection, direction, maxRadians, 0f);
                    chain.Positions[i] = parent + direction * chain.Lengths[i];

                    foreach (var collider in Colliders)
                    {
                        if (collider != null && collider.isActiveAndEnabled)
                            collider.PushOutSegment(parent, ref chain.Positions[i], ParticleRadius, chain.Targets[i - 1], chain.Targets[i]);
                    }
                }

                ApplyToBones(chain);
                chain.PreviousAnchorPosition = chain.Anchor.position;
                chain.PreviousAnchorRotation = chain.Anchor.rotation;
            }
        }

        private static Vector3 Carry(Chain chain, Vector3 point, Quaternion deltaRotation)
        {
            return chain.Anchor.position + deltaRotation * (point - chain.PreviousAnchorPosition);
        }

        private static void RestoreRestPose(Chain chain)
        {
            for (int i = 0; i < chain.Bones.Length; i++) chain.Bones[i].localRotation = chain.RestLocalRotations[i];
        }

        private void ReadTargets(Chain chain)
        {
            for (int i = 0; i < chain.Bones.Length; i++) chain.Targets[i] = chain.Bones[i].position;
            if (chain.HasVirtualTip) chain.Targets[chain.Bones.Length] = VirtualTip(chain);
        }

        /// <summary>Rotation only, not scale: the tip length is in world metres even under a 100x bone scale.</summary>
        private Vector3 VirtualTip(Chain chain)
        {
            Transform last = chain.Bones[chain.Bones.Length - 1];
            return last.position + last.rotation * LocalTailAxis * TailLength;
        }

        private void ApplyToBones(Chain chain)
        {
            int nodes = chain.Positions.Length;
            for (int i = 0; i < nodes - 1; i++)
            {
                Transform bone = chain.Bones[i];
                Vector3 next = i + 1 < chain.Bones.Length ? chain.Bones[i + 1].position : VirtualTip(chain);
                Vector3 current = next - bone.position;
                Vector3 wanted = chain.Positions[i + 1] - bone.position;
                if (current.sqrMagnitude < 1e-12f || wanted.sqrMagnitude < 1e-12f) continue;

                bone.rotation = Quaternion.FromToRotation(current, wanted) * bone.rotation;
                // The bone, not the particle, is the truth for the next segment.
                chain.Positions[i + 1] = i + 1 < chain.Bones.Length ? chain.Bones[i + 1].position : VirtualTip(chain);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 1f, 0.5f, 0.9f);

            if (Application.isPlaying)
            {
                foreach (var chain in _chains)
                {
                    for (int i = 0; i < chain.Positions.Length; i++)
                    {
                        Gizmos.DrawWireSphere(chain.Positions[i], ParticleRadius);
                        if (i > 0) Gizmos.DrawLine(chain.Positions[i - 1], chain.Positions[i]);
                    }
                }
                return;
            }

            // Edit mode: outline the chains, including the virtual tip of childless bones.
            foreach (var root in Roots)
            {
                if (root == null) continue;
                Transform t = root;
                for (; t.childCount > 0; t = t.GetChild(0)) Gizmos.DrawLine(t.position, t.GetChild(0).position);
                if (t == root || !t.name.EndsWith("_end")) Gizmos.DrawLine(t.position, t.position + t.rotation * LocalTailAxis * TailLength);
            }
        }
    }
}
