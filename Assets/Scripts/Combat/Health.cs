using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    public class Health : MonoBehaviour, IDamageable
    {
        [field: SerializeField] public float MaxHealth { get; set; } = 100f;
        public float CurrentHealth { get; set; }

        [Header("Hit Reaction")]
        [Tooltip("Animator trigger fired when damage is taken (and the hit doesn't kill). Also the fallback for any stagger whose own trigger doesn't exist on this Animator.")]
        public string HitAnimationTrigger = "Hit";
        [Tooltip("Safety fallback: if AE_HitReactionEnd() never fires on the Hit clip, the stun ends automatically after this long instead of getting stuck forever.")]
        public float HitStunSafetyDuration = 1f;

        [Header("Death")]
        public string DieAnimationTrigger = "Die";
        public float DestroyDelayAfterDeath = 5f;

        [Header("Knockback")]
        [Tooltip("Horizontal distance covered by the knockback push.")]
        public float KnockbackDistance = 1f;
        public float KnockbackDuration = 0.2f;

        private CharacterController _controller;
        private bool _isBeingKnockedBack;
        private float _knockbackTimer;
        private float _knockbackSpeed;
        private Vector3 _knockbackDirection;

        private Animator _animator;
        private bool _hasAnimator;
        private int _hitTriggerHash;
        private int _dieTriggerHash;
        private MeleeCombatController _meleeCombat;
        private RollController _roll;
        private BlockController _block;

        // Every sibling that can grant immunity (roll i-frames, post-parry window, ...), gathered once
        // so Health doesn't need to know which concrete components provide it.
        private IInvulnerabilitySource[] _invulnerabilitySources;

        private bool _isHitStunned;
        private float _hitStunTimer;

        // Scripts to disable on death, gathered once at Awake. Deliberately NOT the
        // CharacterController itself — see the note in Die() below for why.
        private Behaviour[] _behavioursToDisableOnDeath;

        public bool IsDead { get; private set; }

        /// <summary>True during any stagger — a regular hit reaction, being parried, or a guard break
        /// (see Stagger()). Everything that refuses to act while stunned reads this, so new stagger
        /// sources are gated automatically.</summary>
        public bool IsHitStunned => _isHitStunned;

        /// <summary>True while a knockback push is moving this character. AI steering reads this so it
        /// doesn't fight the push with its own movement.</summary>
        public bool IsBeingKnockedBack => _isBeingKnockedBack;

        public bool IsInvulnerable
        {
            get
            {
                foreach (var source in _invulnerabilitySources)
                {
                    if (source.IsInvulnerable) return true;
                }
                return false;
            }
        }

        private void Awake()
        {
            CurrentHealth = MaxHealth;
            _hasAnimator = TryGetComponent(out _animator);
            _hitTriggerHash = Animator.StringToHash(HitAnimationTrigger);
            _dieTriggerHash = Animator.StringToHash(DieAnimationTrigger);
            _meleeCombat = GetComponent<MeleeCombatController>(); // optional — null means nothing to cancel
            _roll = GetComponent<RollController>();                 // optional — null means nothing to cancel
            _block = GetComponent<BlockController>();             // optional — null means hits are never blocked/parried
            _controller = GetComponent<CharacterController>();    // optional — null means no knockback movement
            _invulnerabilitySources = GetComponents<MonoBehaviour>().OfType<IInvulnerabilitySource>().ToArray();

            // Gather whichever of these exist on this GameObject (player or AI, hence the
            // GetComponent-and-filter-nulls approach rather than assuming a fixed set) so death
            // can cleanly stop all of them without caring which ones are actually present.
            _behavioursToDisableOnDeath = new Behaviour[]
            {
                GetComponent<MeleeCombatController>(),
                GetComponent<RollController>(),
                GetComponent<BlockController>(),
                GetComponent<PlayerMeleeCombatInput>(),
                GetComponent<AIMeleeCombatInput>(),
                // ThirdPersonController lives in the StarterAssets namespace (not .Combat) —
                // uncomment once you confirm the exact class name in your project:
                // GetComponent<StarterAssets.ThirdPersonController>(),
            }.Where(b => b != null).ToArray();
        }

        private void Update()
        {
            if (_isBeingKnockedBack) TickKnockback();

            if (!_isHitStunned) return;

            _hitStunTimer -= Time.deltaTime;
            if (_hitStunTimer <= 0f)
            {
                Debug.LogWarning($"{nameof(Health)} on '{name}': stagger exceeded its safety duration " +
                                  "without AE_HitReactionEnd()/AE_StaggerEnd() firing. Check the playing reaction clip has the event wired up. Force-ending.", this);
                AE_HitReactionEnd();
            }
        }

        private void TickKnockback()
        {
            _knockbackTimer -= Time.deltaTime;
            if (_knockbackTimer <= 0f)
            {
                _isBeingKnockedBack = false;
                return;
            }

            _controller.Move(_knockbackDirection * _knockbackSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Resolves a hit in order of precedence: dead -> invulnerable -> block/parry -> plain hit.
        /// Returns the outcome so the attacker can react (MeleeCombatController staggers itself on Parried).
        /// </summary>
        public HitResult TakeDamage(HitInfo hit)
        {
            if (IsDead) return HitResult.Ignored;

            if (IsInvulnerable)
            {
                Debug.Log($"{gameObject.name} negated {hit.Damage} damage (invulnerable).");
                return HitResult.Invulnerable;
            }

            float damageThrough = hit.Damage;
            BlockResolution block = _block != null
                ? _block.ResolveIncomingHit(hit, out damageThrough)
                : BlockResolution.NotBlocked;

            switch (block)
            {
                case BlockResolution.Parried:
                    // No damage, no knockback for the parrier — BlockController already applied the
                    // parry's own effects (post-parry window, push). The attacker staggers itself.
                    return HitResult.Parried;

                case BlockResolution.Blocked:
                    ApplyKnockback(hit.SourcePosition,
                                   hit.KnockbackDistanceMultiplier * _block.BlockedKnockbackMultiplier,
                                   hit.KnockbackDurationMultiplier);
                    return HitResult.Blocked;

                case BlockResolution.GuardBroken:
                    return ApplyDamageAndStagger(hit, damageThrough,
                                                 _block.GuardBreakAnimationTrigger,
                                                 _block.GuardBreakStaggerSafetyDuration,
                                                 HitResult.GuardBroken);

                default:
                    return ApplyDamageAndStagger(hit, hit.Damage,
                                                 HitAnimationTrigger,
                                                 HitStunSafetyDuration,
                                                 HitResult.Hit);
            }
        }

        /// <summary>Shared tail of a plain hit and a guard break: lose health, get pushed, then either
        /// die or stagger with the given reaction.</summary>
        private HitResult ApplyDamageAndStagger(in HitInfo hit, float damage, string staggerTrigger,
                                                float staggerSafetyDuration, HitResult resultIfSurvived)
        {
            CurrentHealth -= damage;
            Debug.Log($"{gameObject.name} took {damage} damage ({resultIfSurvived}). Health remaining: {CurrentHealth}");

            ApplyKnockback(hit.SourcePosition, hit.KnockbackDistanceMultiplier, hit.KnockbackDurationMultiplier);

            if (CurrentHealth <= 0f)
            {
                Die();
                return HitResult.Killed;
            }

            Stagger(staggerSafetyDuration, staggerTrigger);
            return resultIfSurvived;
        }

        /// <summary>
        /// Pushes this character horizontally away from sourcePosition over time. Public on its own (not
        /// only via TakeDamage) so non-damaging effects, like the post-parry shockwave, can push too.
        /// </summary>
        public void ApplyKnockback(Vector3 sourcePosition, float distanceMultiplier = 1f, float durationMultiplier = 1f)
        {
            if (_controller == null || IsDead) return;

            Vector3 pushDirection = transform.position - sourcePosition;
            pushDirection.y = 0f; // horizontal only — don't launch the character into the air

            if (pushDirection.sqrMagnitude < 0.0001f)
            {
                // Source is directly on top of us (or wasn't provided meaningfully) —
                // nothing sensible to push away from, so skip rather than picking an arbitrary direction.
                return;
            }

            // KnockbackDistance/KnockbackDuration stay at their configured defaults — the two
            // multipliers are independent, so an attack can push far without holding it long
            // (or vice versa), rather than distance and duration always scaling together.
            float distance = KnockbackDistance * distanceMultiplier;
            float duration = KnockbackDuration * durationMultiplier;
            if (duration <= 0f || distance <= 0f) return;

            _knockbackDirection = pushDirection.normalized;
            _knockbackSpeed = distance / duration;
            _knockbackTimer = duration;
            _isBeingKnockedBack = true;
        }

        /// <summary>
        /// Generalized hitstun: locks the character out (IsHitStunned), cancels any attack, and plays a
        /// reaction. Used for regular hits, being parried, and guard breaks. Ends via AE_HitReactionEnd /
        /// AE_StaggerEnd on the reaction clip; safetyDuration is only the dev fallback if that event never
        /// fires. If animationTrigger doesn't exist on this Animator, the Hit reaction plays instead so a
        /// new stagger type works before its own clip is authored.
        /// </summary>
        public void Stagger(float safetyDuration, string animationTrigger)
        {
            if (IsDead) return;

            _isHitStunned = true;
            _hitStunTimer = safetyDuration;

            // A stagger interrupts whatever action was playing; each action is cancelled explicitly
            // because its own end event (AE_AttackEnd, AE_IsRolling) won't fire once the clip is cut.
            _meleeCombat?.ForceCancelAttack();
            _roll?.ForceCancelRoll();

            if (!_hasAnimator) return;

            int triggerHash = Animator.StringToHash(animationTrigger);
            if (!_animator.HasParameter(triggerHash)) triggerHash = _hitTriggerHash;
            if (_animator.HasParameter(triggerHash)) _animator.SetTrigger(triggerHash);
        }

        /// <summary>Animation Event — place at the end of the Hit reaction clip.</summary>
        public void AE_HitReactionEnd()
        {
            _isHitStunned = false;
        }

        /// <summary>Animation Event — same as AE_HitReactionEnd, named for the Parried / GuardBreak clips.</summary>
        public void AE_StaggerEnd() => AE_HitReactionEnd();

        public void Heal(float heal)
        {
            if (IsDead) return;
            CurrentHealth = Mathf.Min(CurrentHealth + heal, MaxHealth);
        }

        private void Die()
        {
            IsDead = true;
            _isHitStunned = false;
            Debug.Log($"{gameObject.name} died!");

            _meleeCombat?.ForceCancelAttack();
            _roll?.ForceCancelRoll();

            if (_hasAnimator) _animator.SetTrigger(_dieTriggerHash);

            // Stop the scripts that DRIVE movement/combat, rather than disabling the
            // CharacterController component that they call into.
            foreach (var behaviour in _behavioursToDisableOnDeath)
            {
                behaviour.enabled = false;
            }

            if (TryGetComponent<UnityEngine.AI.NavMeshAgent>(out var agent)) agent.enabled = false;

            // Deliberately NOT calling GetComponent<Collider>() here — CharacterController IS a
            // Collider, so col.enabled = false disables movement AND collision together, and
            // anything still calling _controller.Move() (there shouldn't be, now that the
            // scripts above are disabled) would throw. If you want the corpse to stop blocking
            // navigation/physics, turn off collision WITHOUT disabling the whole component:
            if (TryGetComponent<CharacterController>(out var characterController))
            {
                characterController.detectCollisions = false;
            }

            Destroy(gameObject, DestroyDelayAfterDeath);
        }
    }
}
