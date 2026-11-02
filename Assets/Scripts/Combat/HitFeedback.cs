using System;
using Unity.Cinemachine;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Defender-side hit feedback: on every hit this character receives (Health.HitReceived) it spawns the FX
    /// for that outcome at the weapon's contact point, freezes this character and the attacker (hit-stop) and
    /// shakes the camera. Lives on the DEFENDER so each character type picks its own look (blood colour,
    /// armour sparks) without weapons knowing anything about their targets.
    ///
    /// Every slot is optional: an empty Fx prefab spawns nothing, 0 hit-stop / shake = none. Camera shake
    /// uses a Cinemachine Impulse Source on this object; the gameplay cameras need a Cinemachine Impulse
    /// Listener for it to be felt. Put the shake on the hits the player cares about: the player's own
    /// component (getting hit) and the enemies' (the player hitting them).
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class HitFeedback : MonoBehaviour
    {
        [Serializable]
        public class Feedback
        {
            [Tooltip("Spawned at the contact point, facing along the swing. Empty = no FX.")]
            public GameObject Fx;
            [Tooltip("Seconds both characters' animation freezes. 0 = none.")]
            public float HitStop;
            [Tooltip("Camera shake force (Impulse Source). 0 = none.")]
            public float Shake;
        }

        [Header("Per outcome")]
        public Feedback Hit = new() { HitStop = 0.06f, Shake = 0.3f };
        [Tooltip("Killing blow. Empty FX falls back to Hit's.")]
        public Feedback Killed = new() { HitStop = 0.12f, Shake = 0.6f };
        public Feedback Blocked = new() { HitStop = 0.04f, Shake = 0.2f };
        public Feedback GuardBroken = new() { HitStop = 0.1f, Shake = 0.5f };
        public Feedback Parried = new() { HitStop = 0.15f, Shake = 0.6f };
        [Tooltip("Hit absorbed by i-frames / post-parry invulnerability.")]
        public Feedback Invulnerable = new();

        [Header("Spawning")]
        [Tooltip("Seconds before a spawned FX object is destroyed (set it longer than the longest effect).")]
        public float FxLifetime = 3f;
        [Tooltip("Where FX spawn when a hit has no contact point (not from a weapon), relative to this character.")]
        public Vector3 FallbackFxOffset = new(0f, 1.2f, 0f);

        private Health _health;
        private HitStop _hitStop;
        private CinemachineImpulseSource _impulse;

        private void Awake()
        {
            _health = GetComponent<Health>();
            if (!TryGetComponent(out _hitStop)) _hitStop = gameObject.AddComponent<HitStop>();
            TryGetComponent(out _impulse); // optional: null = no camera shake
        }

        private void OnEnable() => _health.HitReceived += OnHitReceived;
        private void OnDisable() => _health.HitReceived -= OnHitReceived;

        private void OnHitReceived(HitInfo hit, HitResult result)
        {
            Feedback feedback = For(result);
            if (feedback == null) return;

            SpawnFx(feedback.Fx != null || result != HitResult.Killed ? feedback.Fx : Hit.Fx, hit);

            if (feedback.HitStop > 0f)
            {
                _hitStop.Freeze(feedback.HitStop);
                if (hit.Attacker != null)
                {
                    // TryGetComponent, not GetComponent ?? : in the Editor a missing component is a fake-null
                    // object that ?? doesn't see.
                    if (!hit.Attacker.TryGetComponent(out HitStop attackerStop)) attackerStop = hit.Attacker.AddComponent<HitStop>();
                    attackerStop.Freeze(feedback.HitStop);
                }
            }

            if (feedback.Shake > 0f && _impulse != null) _impulse.GenerateImpulseWithForce(feedback.Shake);
        }

        private Feedback For(HitResult result)
        {
            switch (result)
            {
                case HitResult.Hit: return Hit;
                case HitResult.Killed: return Killed;
                case HitResult.Blocked: return Blocked;
                case HitResult.GuardBroken: return GuardBroken;
                case HitResult.Parried: return Parried;
                case HitResult.Invulnerable: return Invulnerable;
                default: return null;
            }
        }

        private void SpawnFx(GameObject prefab, in HitInfo hit)
        {
            if (prefab == null) return;

            Vector3 position = hit.HasContact ? hit.ContactPoint : transform.TransformPoint(FallbackFxOffset);
            Vector3 direction = hit.HasContact && hit.ContactDirection.sqrMagnitude > 1e-6f
                ? hit.ContactDirection
                : position - hit.SourcePosition; // away from the attacker
            Quaternion rotation = direction.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(direction) : Quaternion.identity;

            // Not parented: the effect stays where the hit happened even as the character moves or dies.
            var fx = Instantiate(prefab, position, rotation);
            if (FxLifetime > 0f) Destroy(fx, FxLifetime);
        }
    }
}
