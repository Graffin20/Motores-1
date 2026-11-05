using System.Collections.Generic;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// The boss's non-combo attacks: fire cone, stomp and grab. BossAI decides WHEN (StartFireCone /
    /// StartStomp / StartGrab); this component only plays them. All timing comes from animation events on
    /// the clips (this must sit on the Animator's GameObject); a per-attack safety timeout ends an attack
    /// whose events never fire. While IsBusy this component owns the boss's facing (BossAI doesn't turn it).
    ///
    /// Clip events:
    ///   Fire cone: AE_FireConeStart (flames + damage on), AE_FireConeEnd (off), AE_SpecialAttackEnd
    ///   Stomp:     AE_StompTakeoff (airborne: fast turn toward the target), AE_StompLand (AOE + stun), AE_SpecialAttackEnd
    ///   Grab:      AE_GrabCheck (connect or whiff), AE_GrabHit (0..n, optional float = damage), AE_GrabRelease, AE_SpecialAttackEnd
    ///
    /// Fire cone and stomp are unblockable (HitInfo.Unblockable): only roll i-frames avoid them. The grab is
    /// only possible while the target is still in the stun THIS boss's stomp caused (Health.StaggerId).
    /// Implements IInterruptibleAction: a stagger or death of the boss cancels the attack and releases a held target.
    /// </summary>
    [RequireComponent(typeof(Health))]
    public class BossSpecialAttacks : MonoBehaviour, IInterruptibleAction
    {
        public enum SpecialAttack { None, FireCone, Stomp, Grab }

        // Windup = before the first gameplay event; Active = flames on / airborne / holding; Recovery = after.
        private enum Phase { Windup, Active, Recovery }

        [Header("Shared")]
        [Tooltip("Layers the attacks can hit (the player's CharacterController layer).")]
        public LayerMask HittableLayers;
        [Tooltip("Optional: geometry that blocks the fire cone (line of sight from the origin). Empty = no check.")]
        public LayerMask ObstacleLayers;

        [Header("Fire Cone")]
        public string FireConeTrigger = "FireCone";
        [Tooltip("Where the flames come from (e.g. an empty at the mouth). Empty = 1.5 m above the boss's root. The cone always points along the boss's forward (yaw), not this object's rotation.")]
        public Transform FireConeOrigin;
        [Tooltip("Optional flame VFX object: activated on AE_FireConeStart, deactivated on AE_FireConeEnd / cancel.")]
        public GameObject FireConeVfx;
        [Tooltip("How far the flames reach (metres).")]
        public float FireConeLength = 6f;
        [Tooltip("Total opening angle of the cone (degrees).")]
        [Range(1f, 180f)]
        public float FireConeAngle = 60f;
        [Tooltip("Seconds between damage ticks while the flames are on. The first tick happens on AE_FireConeStart.")]
        public float FireTickInterval = 0.25f;
        public float FireTickDamage = 5f;
        [Tooltip("Knockback multipliers of the first tick that hits a target (the one that staggers it). Later ticks only deal damage and refresh the slow.")]
        public float FireKnockbackDistanceMultiplier = 0.5f;
        public float FireKnockbackDurationMultiplier = 1f;
        [Tooltip("Locomotion speed multiplier applied by every tick that hits (needs MovementModifiers on the target).")]
        [Range(0f, 1f)]
        public float FireSlowMultiplier = 0.5f;
        [Tooltip("Seconds the slow lasts after the last tick that hit.")]
        public float FireSlowDuration = 3f;
        [Tooltip("Degrees/second the boss may keep turning toward the target while the flames are on (sweeping). 0 = the cone stays where it started.")]
        public float FireConeTurnSpeed = 20f;
        [Tooltip("Dev safety: whole fire cone attack, start to AE_SpecialAttackEnd.")]
        public float FireConeSafetyDuration = 8f;

        [Header("Stomp")]
        public string StompTrigger = "Stomp";
        [Tooltip("Centre of the shockwave. Empty = the boss's root.")]
        public Transform StompOrigin;
        [Tooltip("Optional VFX spawned (unparented) at the origin on AE_StompLand.")]
        public GameObject StompVfxPrefab;
        public float StompVfxLifetime = 3f;
        public float StompRadius = 4f;
        [Tooltip("Targets more than this far ABOVE the origin aren't hit (jumping over the shockwave).")]
        public float StompMaxHeight = 1f;
        public float StompDamage = 25f;
        public float StompKnockbackDistanceMultiplier = 2f;
        public float StompKnockbackDurationMultiplier = 1.5f;
        [Tooltip("Degrees/second the boss turns toward the target between AE_StompTakeoff and AE_StompLand.")]
        public float AirTurnSpeed = 540f;
        [Tooltip("Stagger trigger played on the target (needs AE_StaggerEnd at its end; falls back to Hit). Its length is how long the boss has to walk over and grab.")]
        public string StunTrigger = "Stunned";
        [Tooltip("Dev safety only for the stun (the Stunned clip's AE_StaggerEnd ends it).")]
        public float StunSafetyDuration = 4f;
        [Tooltip("Dev safety: whole stomp attack, start to AE_SpecialAttackEnd.")]
        public float StompSafetyDuration = 6f;

        [Header("Grab")]
        public string GrabTrigger = "Grab";
        [Tooltip("Optional trigger played when AE_GrabCheck finds nothing to grab (a whiff recovery). Without it the Grab clip just keeps playing; its hit/release events are ignored.")]
        public string GrabMissTrigger = "GrabMiss";
        [Tooltip("Where the held target's root (feet) is pinned: an empty under the boss's hand bone. Its blue axis (forward) = the direction the held target faces. Keep it outside the boss's CharacterController capsule.")]
        public Transform GrabSocket;
        [Tooltip("At AE_GrabCheck the target's root must be within this horizontal distance of the socket.")]
        public float GrabRadius = 1.5f;
        [Tooltip("Degrees/second the boss turns toward the target between the start of the grab and AE_GrabCheck.")]
        public float GrabReachTurnSpeed = 180f;
        [Tooltip("Animator trigger played on the held target (a looping held pose).")]
        public string HeldTrigger = "Grabbed";
        [Tooltip("Damage of AE_GrabHit when the event's float parameter is 0.")]
        public float GrabHitDamage = 10f;
        public float GrabReleaseKnockbackDistanceMultiplier = 3f;
        public float GrabReleaseKnockbackDurationMultiplier = 1.5f;
        [Tooltip("Dev safety on the TARGET's side: released if the boss never fires AE_GrabRelease.")]
        public float HoldSafetyDuration = 8f;
        [Tooltip("Dev safety: whole grab attack, start to AE_SpecialAttackEnd.")]
        public float GrabSafetyDuration = 10f;

        public SpecialAttack Current { get; private set; } = SpecialAttack.None;
        public bool IsBusy => Current != SpecialAttack.None;

        private Health _health;
        private Animator _animator;
        private int _fireConeTriggerHash, _stompTriggerHash, _grabTriggerHash, _grabMissTriggerHash;
        private bool _hasFireConeTrigger, _hasStompTrigger, _hasGrabTrigger, _hasGrabMissTrigger;

        private Phase _phase;
        private float _safetyTimer;
        private Transform _target;

        // Fire cone
        private float _tickTimer;
        private readonly HashSet<IDamageable> _fireStaggered = new();
        private readonly HashSet<IDamageable> _tickTargets = new();

        // Stomp -> grab link: who the last stomp stunned, and which stagger that was.
        private Health _stunnedTarget;
        private int _stunnedStaggerId;

        // Grab
        private Health _grabTarget;
        private bool _isHolding;

        private void Awake()
        {
            _health = GetComponent<Health>();
            TryGetComponent(out _animator);

            _fireConeTriggerHash = Animator.StringToHash(FireConeTrigger);
            _stompTriggerHash = Animator.StringToHash(StompTrigger);
            _grabTriggerHash = Animator.StringToHash(GrabTrigger);
            _grabMissTriggerHash = Animator.StringToHash(GrabMissTrigger);
            _hasFireConeTrigger = _animator.HasParameter(_fireConeTriggerHash);
            _hasStompTrigger = _animator.HasParameter(_stompTriggerHash);
            _hasGrabTrigger = _animator.HasParameter(_grabTriggerHash);
            _hasGrabMissTrigger = _animator.HasParameter(_grabMissTriggerHash);

            // A prefab asset (not part of any scene) can't be shown with SetActive (it would edit the asset):
            // spawn one instance under the origin and use that. A child object already in the boss is used as is.
            if (FireConeVfx != null && !FireConeVfx.scene.IsValid())
            {
                Transform parent = FireConeOrigin != null ? FireConeOrigin : transform;
                FireConeVfx = Instantiate(FireConeVfx, parent.position, parent.rotation, parent);
            }
            if (FireConeVfx != null)
            {
                _fireConeParticles = FireConeVfx.GetComponentsInChildren<ParticleSystem>(true);
                FireConeVfx.SetActive(false);
            }
        }

        private ParticleSystem[] _fireConeParticles = new ParticleSystem[0];

        private void ShowFireConeVfx()
        {
            if (FireConeVfx == null) return;
            FireConeVfx.SetActive(true);
            foreach (var ps in _fireConeParticles) ps.Play(false); // restarts emission after a previous Stop
        }

        /// <summary>Particle systems stop emitting and let the live flames fade; anything else is just hidden.</summary>
        private void HideFireConeVfx()
        {
            if (FireConeVfx == null || !FireConeVfx.activeSelf) return;
            if (_fireConeParticles.Length == 0)
            {
                FireConeVfx.SetActive(false);
                return;
            }
            foreach (var ps in _fireConeParticles) ps.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }

        // ------------------------------------------------------------------ Starting (called by BossAI)

        private bool CanStart => !IsBusy && !_health.IsDead && !_health.IsHitStunned;

        public bool StartFireCone(Transform target)
        {
            if (!CanStart || target == null) return false;
            if (!WarnIfMissing(_hasFireConeTrigger, FireConeTrigger)) return false;
            Begin(SpecialAttack.FireCone, target, FireConeSafetyDuration);
            _fireStaggered.Clear();
            _animator.SetTrigger(_fireConeTriggerHash);
            return true;
        }

        public bool StartStomp(Transform target)
        {
            if (!CanStart || target == null) return false;
            if (!WarnIfMissing(_hasStompTrigger, StompTrigger)) return false;
            Begin(SpecialAttack.Stomp, target, StompSafetyDuration);
            _animator.SetTrigger(_stompTriggerHash);
            return true;
        }

        /// <summary>True while target is in the stun caused by this boss's last stomp (and not re-staggered since).</summary>
        public bool CanGrab(Health target)
        {
            return target != null && target == _stunnedTarget
                   && !target.IsDead && !target.IsHeld && target.IsHitStunned
                   && target.StaggerId == _stunnedStaggerId;
        }

        public bool StartGrab(Health target)
        {
            if (!CanStart || !CanGrab(target)) return false;
            if (!WarnIfMissing(_hasGrabTrigger, GrabTrigger)) return false;
            if (GrabSocket == null)
            {
                Debug.LogError($"{nameof(BossSpecialAttacks)} on '{name}': GrabSocket isn't assigned; can't grab.", this);
                return false;
            }
            Begin(SpecialAttack.Grab, target.transform, GrabSafetyDuration);
            _grabTarget = target;
            _animator.SetTrigger(_grabTriggerHash);
            return true;
        }

        private void Begin(SpecialAttack attack, Transform target, float safetyDuration)
        {
            Current = attack;
            _phase = Phase.Windup;
            _target = target;
            _safetyTimer = safetyDuration;
        }

        private bool WarnIfMissing(bool hasTrigger, string trigger)
        {
            if (!hasTrigger) Debug.LogWarning($"{nameof(BossSpecialAttacks)} on '{name}': the Animator has no '{trigger}' trigger; attack skipped.", this);
            return hasTrigger;
        }

        // ------------------------------------------------------------------ Per frame

        private void Update()
        {
            if (!IsBusy) return;

            _safetyTimer -= Time.deltaTime;
            if (_safetyTimer <= 0f)
            {
                Debug.LogWarning($"{nameof(BossSpecialAttacks)} on '{name}': {Current} exceeded its safety duration without " +
                                  "AE_SpecialAttackEnd (or its other events) firing. Check the clip's events. Force-ending.", this);
                Finish();
                return;
            }

            switch (Current)
            {
                case SpecialAttack.FireCone:
                    if (_phase != Phase.Active) break;
                    TurnTowardTarget(FireConeTurnSpeed);
                    _tickTimer -= Time.deltaTime;
                    if (_tickTimer <= 0f)
                    {
                        _tickTimer += Mathf.Max(0.02f, FireTickInterval);
                        FireTick();
                    }
                    break;

                case SpecialAttack.Stomp:
                    if (_phase == Phase.Active) TurnTowardTarget(AirTurnSpeed);
                    break;

                case SpecialAttack.Grab:
                    if (_phase == Phase.Windup) TurnTowardTarget(GrabReachTurnSpeed);
                    break;
            }
        }

        private void TurnTowardTarget(float degreesPerSecond)
        {
            if (_target == null || degreesPerSecond <= 0f) return;
            Vector3 direction = _target.position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(direction.normalized, Vector3.up), degreesPerSecond * Time.deltaTime);
        }

        // ------------------------------------------------------------------ Fire cone

        public void AE_FireConeStart()
        {
            if (Current != SpecialAttack.FireCone || _phase != Phase.Windup) return;
            _phase = Phase.Active;
            _tickTimer = 0f; // first tick this frame (Update after the event... next frame at the latest)
            ShowFireConeVfx();
        }

        public void AE_FireConeEnd()
        {
            if (Current != SpecialAttack.FireCone || _phase != Phase.Active) return;
            _phase = Phase.Recovery;
            HideFireConeVfx();
        }

        private Vector3 FireOrigin => FireConeOrigin != null ? FireConeOrigin.position : transform.position + Vector3.up * 1.5f;

        private void FireTick()
        {
            Vector3 origin = FireOrigin;
            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward.Normalize();
            float halfAngle = FireConeAngle * 0.5f;

            _tickTargets.Clear();
            foreach (var col in Physics.OverlapSphere(origin, FireConeLength, HittableLayers, QueryTriggerInteraction.Ignore))
            {
                if (col.transform.IsChildOf(transform)) continue;

                Vector3 point = WeaponTrace.ContactPoint(col, origin, origin);
                Vector3 toPoint = point - origin;
                if (toPoint.magnitude > FireConeLength) continue;

                Vector3 flat = toPoint;
                flat.y = 0f;
                // A point right at the origin (target overlapping the mouth) counts as inside.
                if (flat.sqrMagnitude > 0.0001f && Vector3.Angle(forward, flat) > halfAngle) continue;

                if (ObstacleLayers.value != 0 && toPoint.sqrMagnitude > 0.0001f
                    && Physics.Raycast(origin, toPoint.normalized, toPoint.magnitude, ObstacleLayers, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                var damageable = col.GetComponentInParent<IDamageable>();
                if (damageable == null || !_tickTargets.Add(damageable)) continue;

                // Only the first tick that actually lands staggers and pushes; the rest are damage-only, so
                // the target isn't stun-locked inside the flames. A tick dodged by i-frames doesn't count.
                bool firstHit = !_fireStaggered.Contains(damageable);
                var hit = new HitInfo(FireTickDamage, transform.position,
                                      firstHit ? FireKnockbackDistanceMultiplier : 0f,
                                      firstHit ? FireKnockbackDurationMultiplier : 0f)
                {
                    Attacker = gameObject,
                    Unblockable = true,
                    DamageOnly = !firstHit,
                    HasContact = true,
                    ContactPoint = point,
                    ContactDirection = toPoint.sqrMagnitude > 0.0001f ? toPoint.normalized : forward,
                };

                HitResult result = damageable.TakeDamage(hit);
                if (result == HitResult.Hit)
                {
                    _fireStaggered.Add(damageable);
                    if (damageable is Component c && c.TryGetComponent(out MovementModifiers modifiers))
                    {
                        modifiers.ApplySlow(FireSlowMultiplier, FireSlowDuration);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ Stomp

        public void AE_StompTakeoff()
        {
            if (Current != SpecialAttack.Stomp || _phase != Phase.Windup) return;
            _phase = Phase.Active;
        }

        public void AE_StompLand()
        {
            if (Current != SpecialAttack.Stomp || _phase == Phase.Recovery) return; // also lands if Takeoff was missing
            _phase = Phase.Recovery;

            Vector3 origin = StompOrigin != null ? StompOrigin.position : transform.position;
            if (StompVfxPrefab != null)
            {
                var vfx = Instantiate(StompVfxPrefab, origin, Quaternion.LookRotation(transform.forward, Vector3.up));
                Destroy(vfx, StompVfxLifetime);
            }

            var struck = new HashSet<IDamageable>();
            foreach (var col in Physics.OverlapSphere(origin, StompRadius, HittableLayers, QueryTriggerInteraction.Ignore))
            {
                if (col.transform.IsChildOf(transform)) continue;

                Vector3 point = WeaponTrace.ContactPoint(col, origin, origin);
                if (point.y - origin.y > StompMaxHeight) continue; // jumped over the shockwave

                var damageable = col.GetComponentInParent<IDamageable>();
                if (damageable == null || !struck.Add(damageable)) continue;

                Vector3 away = point - origin;
                away.y = 0f;
                var hit = new HitInfo(StompDamage, origin, StompKnockbackDistanceMultiplier, StompKnockbackDurationMultiplier)
                {
                    Attacker = gameObject,
                    Unblockable = true,
                    StaggerAnimationTrigger = StunTrigger,
                    StaggerSafetyDuration = StunSafetyDuration,
                    HasContact = true,
                    ContactPoint = point,
                    ContactDirection = away.sqrMagnitude > 0.0001f ? away.normalized : transform.forward,
                };

                // The stun is the Stagger TakeDamage just started, so its StaggerId is the one to remember.
                if (damageable.TakeDamage(hit) == HitResult.Hit && damageable is Health stunned)
                {
                    _stunnedTarget = stunned;
                    _stunnedStaggerId = stunned.StaggerId;
                }
            }
        }

        // ------------------------------------------------------------------ Grab

        public void AE_GrabCheck()
        {
            if (Current != SpecialAttack.Grab || _phase != Phase.Windup) return;

            Health target = _grabTarget;
            bool connected = false;
            if (CanGrab(target))
            {
                Vector3 offset = target.transform.position - GrabSocket.position;
                offset.y = 0f;
                connected = offset.magnitude <= GrabRadius && target.BeginHold(GrabSocket, HeldTrigger, HoldSafetyDuration);
            }

            _stunnedTarget = null; // one grab attempt per stomp, connected or not

            if (connected)
            {
                _isHolding = true;
                _phase = Phase.Active;
            }
            else
            {
                _phase = Phase.Recovery;
                if (_hasGrabMissTrigger) _animator.SetTrigger(_grabMissTriggerHash);
            }
        }

        /// <summary>Animation Event. damage = 0 uses GrabHitDamage. Damage-only on the target's side (held).</summary>
        public void AE_GrabHit(float damage)
        {
            if (Current != SpecialAttack.Grab || !_isHolding) return;
            if (_grabTarget == null || _grabTarget.IsDead || !_grabTarget.IsHeld) { _isHolding = false; return; }

            var hit = new HitInfo(damage > 0f ? damage : GrabHitDamage, transform.position, 0f, 0f)
            {
                Attacker = gameObject,
                Unblockable = true,
                HasContact = true,
                ContactPoint = GrabSocket.position + Vector3.up,
                ContactDirection = transform.forward,
            };
            _grabTarget.TakeDamage(hit);
            if (_grabTarget.IsDead) _isHolding = false; // Health dropped the hold on death
        }

        public void AE_GrabRelease()
        {
            if (Current != SpecialAttack.Grab || !_isHolding) return;
            ReleaseHeld();
            _phase = Phase.Recovery;
        }

        private void ReleaseHeld()
        {
            if (_isHolding && _grabTarget != null)
            {
                _grabTarget.EndHold(transform.position, GrabReleaseKnockbackDistanceMultiplier, GrabReleaseKnockbackDurationMultiplier);
            }
            _isHolding = false;
        }

        // ------------------------------------------------------------------ End / cancel

        /// <summary>Animation Event — end of every special attack clip (control returns to BossAI).</summary>
        public void AE_SpecialAttackEnd()
        {
            if (!IsBusy) return;
            Finish();
        }

        /// <summary>Boss staggered or died (Health): the clip was cut, so clean up what its events would have.</summary>
        public void ForceCancel()
        {
            if (!IsBusy) return;
            if (_animator != null)
            {
                if (_hasFireConeTrigger) _animator.ResetTrigger(_fireConeTriggerHash);
                if (_hasStompTrigger) _animator.ResetTrigger(_stompTriggerHash);
                if (_hasGrabTrigger) _animator.ResetTrigger(_grabTriggerHash);
                if (_hasGrabMissTrigger) _animator.ResetTrigger(_grabMissTriggerHash);
            }
            Finish();
        }

        private void Finish()
        {
            ReleaseHeld(); // a clip missing AE_GrabRelease must never keep the target pinned
            HideFireConeVfx();
            _grabTarget = null;
            _target = null;
            Current = SpecialAttack.None;
        }

        private void OnDisable()
        {
            if (IsBusy) Finish();
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();

            Gizmos.color = new Color(1f, 0.5f, 0f);
            Vector3 origin = FireOrigin;
            Vector3 left = Quaternion.AngleAxis(-FireConeAngle * 0.5f, Vector3.up) * forward * FireConeLength;
            Vector3 right = Quaternion.AngleAxis(FireConeAngle * 0.5f, Vector3.up) * forward * FireConeLength;
            Gizmos.DrawLine(origin, origin + left);
            Gizmos.DrawLine(origin, origin + right);
            Gizmos.DrawLine(origin + left, origin + forward * FireConeLength);
            Gizmos.DrawLine(origin + right, origin + forward * FireConeLength);

            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(StompOrigin != null ? StompOrigin.position : transform.position, StompRadius);

            if (GrabSocket != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(GrabSocket.position, GrabRadius);
            }
        }
    }
}
