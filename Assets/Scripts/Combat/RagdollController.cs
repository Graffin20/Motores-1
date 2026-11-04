using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Keeps a ragdoll (built with Unity's Ragdoll Wizard) asleep while the character is alive, and hands the
    /// body over to physics partway through the death animation.
    ///
    /// Alive: every ragdoll rigidbody is kinematic, so the Animator owns the bones. With CollidersAsHurtboxes the
    /// limb colliders stay on and follow the animation as hurtboxes for WeaponTrace; their layer must not collide
    /// with any CharacterController layer (Physics collision matrix), or the enemy's own arms block it.
    /// Without it they're off until the ragdoll takes over.
    ///
    /// Death (Health.Died): the death animation plays as usual while bone poses are recorded each frame. At
    /// AE_EnableRagdoll on the death clip (or after RagdollSafetyDelay if the event is missing, or immediately
    /// if the Animator has no Die trigger) the Animator is disabled and the bodies go dynamic, each starting
    /// with the velocity its bone had in the animation, so a falling clip keeps falling instead of stopping
    /// dead and dropping. A push away from the killing hit is added on top (KillPushSpeed, 0 = off).
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class RagdollController : MonoBehaviour
    {
        /// <summary>A bone that isn't a child of the limb it belongs to (IK-style rigs: hands/feet parented to
        /// the armature root and animated in place).</summary>
        [Serializable]
        public struct BoneAttachment
        {
            [Tooltip("Bone that must follow the ragdoll (e.g. Hand.L).")]
            public Transform Bone;
            [Tooltip("Ragdoll bone it hangs from (e.g. Arm2.L).")]
            public Transform AttachTo;
        }
        [Tooltip("Optional. Empty = found automatically: every child Rigidbody with a Joint, plus the bodies " +
                 "those joints connect to (the hips). Fill it in if something else under the character has a " +
                 "Rigidbody + Joint that isn't part of the ragdoll.")]
        public List<Rigidbody> Bodies = new();

        [Tooltip("Keep the limb colliders on (kinematic) while alive, as hurtboxes for weapon traces. Off = " +
                 "colliders only exist once the ragdoll takes over.")]
        public bool CollidersAsHurtboxes = true;

        [Tooltip("Rigs whose hands/feet are NOT children of the forearm/shin (parented to the armature root and " +
                 "animated directly): with the Animator off nothing moves them, so they freeze in mid-air. Each " +
                 "Bone is re-parented to AttachTo (keeping its world pose) when the ragdoll takes over. Not undone: " +
                 "the corpse is never revived.")]
        public List<BoneAttachment> AttachOnRagdoll = new();

        [Header("Hand-off")]
        [Tooltip("Dev safety fallback only: ragdoll after this many seconds of death if AE_EnableRagdoll never fires.")]
        public float RagdollSafetyDelay = 3f;

        [Header("Kill Push")]
        [Tooltip("Speed (m/s) added to every body away from the killing hit when the ragdoll takes over. 0 = none.")]
        public float KillPushSpeed = 2f;
        [Tooltip("Upward part of the push direction (0 = flat, 1 = 45 degrees up).")]
        public float KillPushUpward = 0.3f;

        private Health _health;
        private Animator _animator;
        private Collider[] _colliders;
        private Vector3[] _previousPositions;
        private Quaternion[] _previousRotations;

        private bool _dying;            // dead, animation still in control
        private bool _switchRequested;  // AE_EnableRagdoll / timeout: switch in the next LateUpdate
        private bool _ragdollActive;
        private bool _hasPreviousPose;
        private float _deathTimer;
        private Vector3 _killSourcePosition;

        public bool IsRagdollActive => _ragdollActive;

        private void Awake()
        {
            _health = GetComponent<Health>();
            TryGetComponent(out _animator);

            if (Bodies.Count == 0) Bodies = FindRagdollBodies();
            Bodies.RemoveAll(b => b == null);
            if (Bodies.Count == 0)
                Debug.LogWarning($"{nameof(RagdollController)} on '{name}' found no ragdoll bodies (child Rigidbodies " +
                                 "with Joints). Run the Ragdoll Wizard or fill in Bodies.", this);

            // Only the colliders that belong to the ragdoll bodies; never the root CharacterController.
            var bodySet = new HashSet<Rigidbody>(Bodies);
            _colliders = GetComponentsInChildren<Collider>(true)
                .Where(c => !(c is CharacterController) && c.attachedRigidbody != null && bodySet.Contains(c.attachedRigidbody))
                .ToArray();

            _previousPositions = new Vector3[Bodies.Count];
            _previousRotations = new Quaternion[Bodies.Count];

            SetRagdoll(false);
        }

        private void OnEnable() => _health.Died += OnDied;
        private void OnDisable() => _health.Died -= OnDied;

        private void OnDied(Vector3 killSourcePosition)
        {
            if (_dying || _ragdollActive) return;
            _killSourcePosition = killSourcePosition;
            _dying = true;
            _deathTimer = 0f;

            // No animated death to wait for: go limp right away.
            bool hasDeathAnimation = _animator != null && _animator.enabled &&
                                     _animator.HasParameter(Animator.StringToHash(_health.DieAnimationTrigger));
            if (!hasDeathAnimation) _switchRequested = true;
        }

        /// <summary>Animation event on the death clip: the frame physics takes over (e.g. as the knees give).</summary>
        public void AE_EnableRagdoll()
        {
            if (_dying) _switchRequested = true;
        }

        private void Update()
        {
            if (!_dying || _switchRequested) return;

            _deathTimer += Time.deltaTime;
            if (_deathTimer >= RagdollSafetyDelay)
            {
                Debug.LogWarning($"{nameof(RagdollController)} on '{name}': AE_EnableRagdoll never fired; " +
                                 $"forcing the ragdoll after {RagdollSafetyDelay}s. Add the event to the death clip.", this);
                _switchRequested = true;
            }
        }

        // LateUpdate = after the Animator has posed this frame, so the recorded pose (and the velocity taken
        // from the last two frames) is the one actually shown.
        private void LateUpdate()
        {
            if (!_dying) return;

            if (_switchRequested)
            {
                ActivateRagdoll();
                return;
            }

            RecordPose();
        }

        private void ActivateRagdoll()
        {
            float dt = Time.deltaTime;
            bool hasVelocity = _hasPreviousPose && dt > 0f;

            if (_animator != null) _animator.enabled = false; // bones keep the current pose as the start

            // Done with the Animator already off, so nothing writes these bones by their old paths afterwards.
            foreach (var attachment in AttachOnRagdoll)
            {
                if (attachment.Bone != null && attachment.AttachTo != null)
                    attachment.Bone.SetParent(attachment.AttachTo, worldPositionStays: true);
            }

            SetRagdoll(true);

            Vector3 push = Vector3.zero;
            if (KillPushSpeed > 0f)
            {
                Vector3 away = transform.position - _killSourcePosition;
                away.y = 0f;
                if (away.sqrMagnitude < 1e-6f) away = -transform.forward; // hit from inside: fall backwards
                push = (away.normalized + Vector3.up * KillPushUpward).normalized * KillPushSpeed;
            }

            for (int i = 0; i < Bodies.Count; i++)
            {
                Rigidbody body = Bodies[i];
                Vector3 velocity = Vector3.zero, angular = Vector3.zero;
                if (hasVelocity)
                {
                    velocity = (body.transform.position - _previousPositions[i]) / dt;
                    (body.transform.rotation * Quaternion.Inverse(_previousRotations[i])).ToAngleAxis(out float angle, out Vector3 axis);
                    if (angle > 180f) angle -= 360f;
                    if (!float.IsInfinity(axis.x)) angular = axis * (angle * Mathf.Deg2Rad / dt);
                }
                body.linearVelocity = velocity + push;
                body.angularVelocity = angular;
            }

            _dying = false;
            _switchRequested = false;
            _ragdollActive = true;
        }

        private void RecordPose()
        {
            for (int i = 0; i < Bodies.Count; i++)
            {
                // Transform, not Rigidbody: kinematic bodies' positions follow the Animator only at the
                // next physics step.
                _previousPositions[i] = Bodies[i].transform.position;
                _previousRotations[i] = Bodies[i].transform.rotation;
            }
            _hasPreviousPose = true;
        }

        private void SetRagdoll(bool active)
        {
            foreach (var body in Bodies)
            {
                body.isKinematic = !active;
                body.interpolation = active ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            }
            foreach (var col in _colliders) col.enabled = active || CollidersAsHurtboxes;
        }

        private List<Rigidbody> FindRagdollBodies()
        {
            var result = new List<Rigidbody>();
            foreach (var joint in GetComponentsInChildren<Joint>(true))
            {
                if (joint.transform == transform) continue;
                if (joint.TryGetComponent(out Rigidbody body) && !result.Contains(body)) result.Add(body);
                if (joint.connectedBody != null && joint.connectedBody.transform != transform &&
                    !result.Contains(joint.connectedBody))
                    result.Add(joint.connectedBody);
            }
            return result;
        }
    }
}
