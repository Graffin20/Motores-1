using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StarterAssets.SpringBones
{
    /// <summary>
    /// Secondary motion for a skirt made of bone chains hanging from the hips (Lillian: DEF-Skirt_Base.* ->
    /// DEF-Skirt.* -> *_end). Each bone position is a Verlet particle; the solver then rotates the bones to
    /// point at the particles. Bones are only ever rotated, so segment lengths never stretch.
    ///
    /// Per frame (LateUpdate, after the Animator):
    /// 1. Restore the bones' rest rotations and read the resulting positions = the "animated" target, i.e.
    ///    the skirt rigidly following the hips. The skirt bones aren't in the Humanoid Avatar, so nothing else
    ///    writes them: this component is their single owner.
    /// 2. Carry the particles along with part of the hips' movement (MovementInfluence). Rolls, knockback and
    ///    attack lunges move the CharacterController metres in a fraction of a second; full world-space
    ///    inertia would fling the skirt.
    /// 3. Verlet integration (inertia + gravity + damping) and a pull toward the animated target, stiffer at
    ///    the waist than at the hem.
    /// 4. Constraints: sideways links between neighbouring chains (so gaps can't open between them when a leg
    ///    pushes through; compression is free so it can fold), then each chain top-down: segment length + max
    ///    bend angle, then segment-vs-capsule collision with SpringBoneColliders (thighs/shins). Collision runs
    ///    last so it overrides the angle limit; whole segments collide, not just their end points.
    /// 5. Rotate the bones top-down to match.
    ///
    /// Frame-rate independence: stiffness and damping are defined per 1/60 s and converted with Pow; the
    /// timestep is clamped (MaxDeltaTime) so a hitch can't explode the simulation.
    /// </summary>
    [DefaultExecutionOrder(10000)] // after other LateUpdate pose changes (IK etc.)
    public class SkirtSpringBones : MonoBehaviour
    {
        [Header("Chains")]
        [Tooltip("Top bone of each chain. Leave empty to find them by name (Chain Root Name Filter) under this object. " +
                 "Chains are sorted around the body automatically for the sideways links.")]
        public List<Transform> ChainRoots = new List<Transform>();

        [Tooltip("Name fragment used to find the chain roots when the list above is empty.")]
        public string ChainRootNameFilter = "Skirt_Base";

        [Tooltip("Bone the skirt hangs from; its movement is what MovementInfluence filters. Empty = parent of the first chain root (Lillian: DEF-spine).")]
        public Transform Anchor;

        [Tooltip("Segments from each chain root that follow the animation untouched. 1 = the waistband segment (root -> first skirt bone) stays fixed.")]
        [Min(0)] public int PinnedDepth = 1;

        [Tooltip("Link the last chain to the first (full skirt). Off for an open strip (cape, apron).")]
        public bool ClosedLoop = true;

        [Header("Dynamics")]
        public Vector3 Gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip("Fraction of velocity lost per 1/60 s. Higher = calmer, less swinging.")]
        [Range(0f, 1f)] public float Damping = 0.1f;

        [Tooltip("Pull toward the animated pose per 1/60 s, over the chain's normalized length (0 = waist, 1 = hem).")]
        public AnimationCurve StiffnessByDepth = AnimationCurve.Linear(0f, 0.2f, 1f, 0.03f);

        [Tooltip("Max angle (degrees) a segment may bend away from its animated direction. Keeps the skirt from flipping up on rolls and knockback. Collisions may push past it (a raised leg must win).")]
        [Range(0f, 180f)] public float MaxAngle = 60f;

        [Header("Character Movement")]
        [Tooltip("How much of the character's own movement and turning the skirt lags behind. 0 = moves rigidly with the hips, 1 = full world-space inertia.")]
        [Range(0f, 1f)] public float MovementInfluence = 0.35f;

        [Tooltip("If the anchor moves more than this (metres) in one frame the simulation resets (teleport, respawn).")]
        [Min(0f)] public float TeleportDistance = 2f;

        [Header("Sideways Links")]
        [Tooltip("How strongly neighbouring chains are pulled back together once stretched past their rest distance (per iteration).")]
        [Range(0f, 1f)] public float SidewaysStiffness = 0.5f;

        [Tooltip("Stretch allowed before the sideways links act (0.15 = 15% wider than at rest). Compression is always free so the skirt can fold.")]
        [Min(0f)] public float SidewaysSlack = 0.15f;

        [Header("Leg Drivers")]
        [Tooltip("Chains whose rest direction swings with a thigh (the front panels over each leg), so a raised leg lifts the skirt " +
                 "in the same frame instead of relying on collision to catch it. Neighbouring chains follow through the sideways links.")]
        public List<LegDriver> LegDrivers = new List<LegDriver>();

        [Tooltip("Only follow the thigh while it swings forward of its rest pose; backward swing is ignored (sideways swing still counts). " +
                 "Off = the driven chains follow the leg both ways.")]
        public bool FollowForwardOnly;

        [Header("Collision")]
        public List<SpringBoneCollider> Colliders = new List<SpringBoneCollider>();

        [Tooltip("Thickness of the skirt for collision, in metres.")]
        [Min(0f)] public float ParticleRadius = 0.02f;

        [Header("Solver")]
        [Range(1, 8)] public int Iterations = 3;

        [Tooltip("Largest timestep simulated in one frame; slower frames run in slow motion instead of exploding.")]
        [Min(0.001f)] public float MaxDeltaTime = 1f / 30f;

        [System.Serializable]
        public class LegDriver
        {
            [Tooltip("Root of the driven chain (e.g. DEF-Skirt_Base over the left thigh).")]
            public Transform ChainRoot;

            [Tooltip("Thigh bone it follows (e.g. DEF-thigh.L). Its direction is measured toward its first child.")]
            public Transform Thigh;

            [Tooltip("How much of the thigh's swing the chain's rest direction takes. 1 = moves like a trouser leg; 0.6-0.8 reads as 'pushed by the leg'.")]
            [Range(0f, 1f)] public float FollowWeight = 0.7f;

            [Tooltip("Pull toward the leg-lifted pose per 1/60 s along the WHOLE chain (overrides a lower Stiffness By Depth). " +
                     "Without it the loose hem keeps hanging under gravity while only the waist follows the leg.")]
            [Range(0f, 1f)] public float Hold = 0.4f;
        }

        private class Chain
        {
            public Transform[] Bones;            // root .. tip (the *_end leaf bone gives the last segment a length)
            public Quaternion[] RestLocalRotations;
            public float[] Lengths;              // Lengths[i] = rest distance from bone i-1 to bone i (world)
            public Vector3[] Positions;
            public Vector3[] PreviousPositions;
            public Vector3[] Targets;

            // Optional leg driver.
            public Transform Thigh;
            public Transform ThighChild;
            public Vector3 RestThighDirectionInAnchor;
            public float FollowWeight;
            public float Hold;
        }

        private Chain[] _chains = new Chain[0];
        private float[][] _sidewaysRest;         // [c][i]: rest distance between node i of chain c and of chain c+1
        private Vector3 _previousAnchorPosition;
        private Quaternion _previousAnchorRotation;
        private bool _initialized;

        private void Awake()
        {
            Initialize();
        }

        private void OnEnable()
        {
            if (_initialized) ResetSimulation();
        }

        /// <summary>Puts every particle back on the animated pose with no velocity. Call after teleporting the character.</summary>
        public void ResetSimulation()
        {
            if (!_initialized) return;

            foreach (var chain in _chains)
            {
                RestoreRestPose(chain);
                for (int i = 0; i < chain.Bones.Length; i++)
                {
                    chain.Positions[i] = chain.PreviousPositions[i] = chain.Targets[i] = chain.Bones[i].position;
                }
            }
            _previousAnchorPosition = Anchor.position;
            _previousAnchorRotation = Anchor.rotation;
        }

        private void Initialize()
        {
            if (ChainRoots.Count == 0 && !string.IsNullOrEmpty(ChainRootNameFilter))
            {
                ChainRoots = GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name.Contains(ChainRootNameFilter) &&
                                (t.parent == null || !t.parent.name.Contains(ChainRootNameFilter)))
                    .ToList();
            }

            var chains = new List<Chain>();
            foreach (var root in ChainRoots)
            {
                if (root == null) continue;

                var bones = new List<Transform> { root };
                for (Transform t = root; t.childCount > 0; t = t.GetChild(0)) bones.Add(t.GetChild(0));

                if (bones.Count < PinnedDepth + 2)
                {
                    Debug.LogWarning($"[SkirtSpringBones] Chain '{root.name}' has {bones.Count} bones, too short for PinnedDepth {PinnedDepth}; skipped.", this);
                    continue;
                }

                int n = bones.Count;
                var chain = new Chain
                {
                    Bones = bones.ToArray(),
                    RestLocalRotations = bones.Select(b => b.localRotation).ToArray(),
                    Lengths = new float[n],
                    Positions = new Vector3[n],
                    PreviousPositions = new Vector3[n],
                    Targets = new Vector3[n],
                };
                for (int i = 1; i < n; i++) chain.Lengths[i] = Vector3.Distance(bones[i - 1].position, bones[i].position);
                chains.Add(chain);
            }

            if (chains.Count == 0)
            {
                Debug.LogWarning($"[SkirtSpringBones] No chains found (filter '{ChainRootNameFilter}'); disabled.", this);
                enabled = false;
                return;
            }

            if (Anchor == null) Anchor = chains[0].Bones[0].parent != null ? chains[0].Bones[0].parent : transform;

            _chains = SortAroundBody(chains);
            CaptureSidewaysRestDistances();
            AssignLegDrivers();

            _initialized = true;
            ResetSimulation();
        }

        /// <summary>
        /// Orders the chains by angle around the character's up axis so each one links to its real neighbour.
        /// Uses the first non-root bone (the root may sit near the body centre).
        /// </summary>
        private Chain[] SortAroundBody(List<Chain> chains)
        {
            Vector3 SamplePoint(Chain c) => c.Bones[Mathf.Min(1, c.Bones.Length - 1)].position;

            Vector3 centroid = Vector3.zero;
            foreach (var c in chains) centroid += SamplePoint(c);
            centroid /= chains.Count;

            Quaternion toLocal = Quaternion.Inverse(transform.rotation);
            return chains.OrderBy(c =>
            {
                Vector3 v = toLocal * (SamplePoint(c) - centroid);
                return Mathf.Atan2(v.x, v.z);
            }).ToArray();
        }

        private void CaptureSidewaysRestDistances()
        {
            _sidewaysRest = new float[_chains.Length][];
            for (int c = 0; c < _chains.Length; c++)
            {
                Chain a = _chains[c];
                Chain b = _chains[(c + 1) % _chains.Length];
                int count = Mathf.Min(a.Bones.Length, b.Bones.Length);
                _sidewaysRest[c] = new float[count];
                for (int i = 0; i < count; i++)
                    _sidewaysRest[c][i] = Vector3.Distance(a.Bones[i].position, b.Bones[i].position);
            }
        }

        /// <summary>
        /// Captures each driven thigh's rest direction in the anchor's (hips) space. Runs in Awake, before the
        /// Animator's first evaluation, so this is the file's pose: legs straight down (Lillian's FBX was
        /// re-exported in rest pose for exactly this kind of reason).
        /// </summary>
        private void AssignLegDrivers()
        {
            foreach (var driver in LegDrivers)
            {
                if (driver == null || driver.ChainRoot == null || driver.Thigh == null) continue;

                Chain chain = _chains.FirstOrDefault(c => c.Bones[0] == driver.ChainRoot);
                if (chain == null)
                {
                    Debug.LogWarning($"[SkirtSpringBones] Leg driver chain root '{driver.ChainRoot.name}' is not one of the simulated chains; ignored.", this);
                    continue;
                }
                if (driver.Thigh.childCount == 0)
                {
                    Debug.LogWarning($"[SkirtSpringBones] Leg driver thigh '{driver.Thigh.name}' has no child to measure its direction; ignored.", this);
                    continue;
                }

                chain.Thigh = driver.Thigh;
                chain.ThighChild = driver.Thigh.GetChild(0);
                chain.RestThighDirectionInAnchor = ThighDirectionInAnchor(chain);
                chain.FollowWeight = driver.FollowWeight;
                chain.Hold = driver.Hold;
            }
        }

        private Vector3 ThighDirectionInAnchor(Chain chain)
        {
            return (Quaternion.Inverse(Anchor.rotation) * (chain.ThighChild.position - chain.Thigh.position)).normalized;
        }

        /// <summary>
        /// Rotates the chain's animated targets about its pinned hinge by the thigh's SWING since rest (measured
        /// in hips space, so turning or leaning the whole body doesn't count, and twist about the leg's own axis
        /// is ignored), scaled by FollowWeight. The spring then pulls the chain toward the lifted targets, and the
        /// angle limit is measured from them too.
        /// </summary>
        private void ApplyLegDriver(Chain chain)
        {
            if (chain.Thigh == null || chain.FollowWeight <= 0f) return;

            Vector3 rest = chain.RestThighDirectionInAnchor;
            Vector3 current = ThighDirectionInAnchor(chain);

            if (FollowForwardOnly)
            {
                // Remove only the backward part of the swing: a leg behind its rest pose counts as at rest
                // front-to-back, but still swings the chain sideways.
                Vector3 forward = Quaternion.Inverse(Anchor.rotation) * transform.forward;
                float behind = Vector3.Dot(rest, forward) - Vector3.Dot(current, forward);
                if (behind > 0f) current = (current + forward * behind).normalized;
            }

            Quaternion swing = Quaternion.FromToRotation(rest, current);
            Quaternion worldSwing = Anchor.rotation * Quaternion.Slerp(Quaternion.identity, swing, chain.FollowWeight) * Quaternion.Inverse(Anchor.rotation);

            Vector3 hinge = chain.Targets[PinnedDepth];
            for (int i = PinnedDepth + 1; i < chain.Targets.Length; i++)
                chain.Targets[i] = hinge + worldSwing * (chain.Targets[i] - hinge);
        }

        private void LateUpdate()
        {
            if (!_initialized) return;

            float dt = Mathf.Min(Time.deltaTime, MaxDeltaTime);
            if (dt <= 0f) return;

            if ((Anchor.position - _previousAnchorPosition).sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                ResetSimulation();
                return;
            }

            // 1. Animated targets.
            foreach (var chain in _chains)
            {
                RestoreRestPose(chain);
                for (int i = 0; i < chain.Bones.Length; i++) chain.Targets[i] = chain.Bones[i].position;
                ApplyLegDriver(chain);
            }

            // 2. Carry part of the anchor's movement (translation AND rotation, so turning doesn't swing it out either).
            float carry = 1f - MovementInfluence;
            if (carry > 0f)
            {
                Quaternion deltaRotation = Anchor.rotation * Quaternion.Inverse(_previousAnchorRotation);
                foreach (var chain in _chains)
                {
                    for (int i = PinnedDepth + 1; i < chain.Bones.Length; i++)
                    {
                        chain.Positions[i] = Vector3.Lerp(chain.Positions[i], Carry(chain.Positions[i], deltaRotation), carry);
                        chain.PreviousPositions[i] = Vector3.Lerp(chain.PreviousPositions[i], Carry(chain.PreviousPositions[i], deltaRotation), carry);
                    }
                }
            }

            // 3. Integrate.
            float frames = dt * 60f; // per-1/60 s rates -> this frame
            float velocityKept = Mathf.Pow(1f - Damping, frames);
            Vector3 gravityStep = Gravity * (dt * dt);

            foreach (var chain in _chains)
            {
                int n = chain.Bones.Length;
                for (int i = 0; i <= PinnedDepth; i++)
                {
                    chain.Positions[i] = chain.PreviousPositions[i] = chain.Targets[i];
                }
                for (int i = PinnedDepth + 1; i < n; i++)
                {
                    Vector3 position = chain.Positions[i];
                    Vector3 velocity = (position - chain.PreviousPositions[i]) * velocityKept;
                    chain.PreviousPositions[i] = position;
                    position += velocity + gravityStep;

                    float depth = (float)i / (n - 1);
                    float stiffness = Mathf.Clamp01(StiffnessByDepth.Evaluate(depth));
                    if (chain.Thigh != null) stiffness = Mathf.Max(stiffness, chain.Hold);
                    float pull = 1f - Mathf.Pow(1f - stiffness, frames);
                    chain.Positions[i] = Vector3.Lerp(position, chain.Targets[i], pull);
                }
            }

            // 4. Constraints. Each chain is solved top-down, segment by segment: length + angle, then collision,
            //    so every segment starts from an already-corrected parent and collision always has the last word
            //    (the angle limit is a soft safety net for rolls; a leg is solid).
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                SolveSidewaysLinks();
                foreach (var chain in _chains) SolveChain(chain);
            }

            // 5. Pose the bones. No sideways pass after the last chain solve: it could move particles back into a leg.
            foreach (var chain in _chains) ApplyToBones(chain);

            _previousAnchorPosition = Anchor.position;
            _previousAnchorRotation = Anchor.rotation;
        }

        private Vector3 Carry(Vector3 point, Quaternion deltaRotation)
        {
            return Anchor.position + deltaRotation * (point - _previousAnchorPosition);
        }

        private static void RestoreRestPose(Chain chain)
        {
            for (int i = 0; i < chain.Bones.Length; i++) chain.Bones[i].localRotation = chain.RestLocalRotations[i];
        }

        private void SolveChain(Chain chain)
        {
            float maxRadians = MaxAngle * Mathf.Deg2Rad;
            for (int i = PinnedDepth + 1; i < chain.Bones.Length; i++)
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
        }

        private void SolveSidewaysLinks()
        {
            if (SidewaysStiffness <= 0f || _chains.Length < 2) return;

            int linkCount = ClosedLoop ? _chains.Length : _chains.Length - 1;
            for (int c = 0; c < linkCount; c++)
            {
                Chain a = _chains[c];
                Chain b = _chains[(c + 1) % _chains.Length];
                float[] rest = _sidewaysRest[c];

                for (int i = PinnedDepth + 1; i < rest.Length; i++)
                {
                    Vector3 delta = b.Positions[i] - a.Positions[i];
                    float distance = delta.magnitude;
                    float maxDistance = rest[i] * (1f + SidewaysSlack);
                    if (distance <= maxDistance || distance < 1e-6f) continue;

                    Vector3 correction = delta * ((distance - maxDistance) / distance * 0.5f * SidewaysStiffness);
                    a.Positions[i] += correction;
                    b.Positions[i] -= correction;
                }
            }
        }

        private void ApplyToBones(Chain chain)
        {
            for (int i = PinnedDepth; i < chain.Bones.Length - 1; i++)
            {
                Transform bone = chain.Bones[i];
                Vector3 current = chain.Bones[i + 1].position - bone.position;
                Vector3 wanted = chain.Positions[i + 1] - bone.position;
                if (current.sqrMagnitude < 1e-12f || wanted.sqrMagnitude < 1e-12f) continue;

                bone.rotation = Quaternion.FromToRotation(current, wanted) * bone.rotation;
                chain.Positions[i + 1] = chain.Bones[i + 1].position; // the bone, not the particle, is the truth
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.9f);

            if (_initialized && Application.isPlaying)
            {
                foreach (var chain in _chains)
                {
                    for (int i = 0; i < chain.Positions.Length; i++)
                    {
                        Gizmos.DrawWireSphere(chain.Positions[i], ParticleRadius);
                        if (i > 0) Gizmos.DrawLine(chain.Positions[i - 1], chain.Positions[i]);
                    }
                }

                Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.5f);
                int linkCount = ClosedLoop ? _chains.Length : _chains.Length - 1;
                for (int c = 0; c < linkCount && _chains.Length > 1; c++)
                {
                    Chain a = _chains[c];
                    Chain b = _chains[(c + 1) % _chains.Length];
                    int count = Mathf.Min(a.Positions.Length, b.Positions.Length);
                    for (int i = 0; i < count; i++) Gizmos.DrawLine(a.Positions[i], b.Positions[i]);
                }
                return;
            }

            // Edit mode: just outline the chains that would be simulated.
            foreach (var root in ChainRoots)
            {
                for (Transform t = root; t != null && t.childCount > 0; t = t.GetChild(0))
                    Gizmos.DrawLine(t.position, t.GetChild(0).position);
            }
        }
    }
}
