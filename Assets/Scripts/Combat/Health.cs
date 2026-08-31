using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    public class Health : MonoBehaviour, IDamageable
    {
        [field: SerializeField] public float MaxHealth { get; set; } = 100f;
        public float CurrentHealth { get; set; }

        [Header("Hit Reaction")]
        [Tooltip("Animator trigger fired when damage is taken (and the hit doesn't kill).")]
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

        private bool _isHitStunned;
        private float _hitStunTimer;

        // Scripts to disable on death, gathered once at Awake. Deliberately NOT the
        // CharacterController itself — see the note in Die() below for why.
        private Behaviour[] _behavioursToDisableOnDeath;

        public bool IsDead { get; private set; }

        /// <summary>True while a hit-reaction animation is playing — read this from
        /// ThirdPersonController.Move() (alongside IsAttacking/IsRolling) to suppress
        /// locomotion during the stagger.</summary>
        public bool IsHitStunned => _isHitStunned;

        private void Awake()
        {
            CurrentHealth = MaxHealth;
            _hasAnimator = TryGetComponent(out _animator);
            _hitTriggerHash = Animator.StringToHash(HitAnimationTrigger);
            _dieTriggerHash = Animator.StringToHash(DieAnimationTrigger);
            _meleeCombat = GetComponent<MeleeCombatController>(); // optional — null means nothing to cancel
            _roll = GetComponent<RollController>();                 // optional — null means no i-frame check
            _controller = GetComponent<CharacterController>();     // optional — null means no knockback movement

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
                Debug.LogWarning($"{nameof(Health)} on '{name}': hit-stun exceeded {HitStunSafetyDuration}s " +
                                  "without AE_HitReactionEnd() firing. Check the Hit clip has the event wired up. Force-ending.", this);
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

            float speed = _knockbackSpeed;
            _controller.Move(_knockbackDirection * speed * Time.deltaTime);
        }

        public void TakeDamage(float damage, Vector3 hitSourcePosition, float knockbackDistanceMultiplier = 1f, float knockbackDurationMultiplier = 1f)
        {
            if (IsDead) return;

            if (_roll != null && _roll.IsInvulnerable)
            {
                Debug.Log($"{gameObject.name} dodged {damage} damage (invulnerable during roll).");
                return;
            }

            CurrentHealth -= damage;
            Debug.Log($"{gameObject.name} took {damage} damage. Health remaining: {CurrentHealth}");

            ApplyKnockback(hitSourcePosition, knockbackDistanceMultiplier, knockbackDurationMultiplier);

            if (CurrentHealth <= 0f)
            {
                Die();
                return;
            }

            BeginHitStun();
        }

        private void ApplyKnockback(Vector3 hitSourcePosition, float distanceMultiplier, float durationMultiplier)
        {
            if (_controller == null) return;

            Vector3 pushDirection = transform.position - hitSourcePosition;
            pushDirection.y = 0f; // horizontal only — don't launch the character into the air

            if (pushDirection.sqrMagnitude < 0.0001f)
            {
                // Hit source is directly on top of us (or wasn't provided meaningfully) —
                // nothing sensible to push away from, so skip rather than picking an arbitrary direction.
                return;
            }

            // KnockbackDistance/KnockbackDuration stay at their configured defaults — the two
            // multipliers are independent, so an attack can push far without holding it long
            // (or vice versa), rather than distance and duration always scaling together.
            float distance = KnockbackDistance * distanceMultiplier;
            float duration = KnockbackDuration * durationMultiplier;

            _knockbackDirection = pushDirection.normalized;
            _knockbackSpeed = duration > 0f ? distance / duration : 0f;
            _knockbackTimer = duration;
            _isBeingKnockedBack = true;
        }

        private void BeginHitStun()
        {
            _isHitStunned = true;
            _hitStunTimer = HitStunSafetyDuration;

            _meleeCombat?.ForceCancelAttack();

            if (_hasAnimator) _animator.SetTrigger(_hitTriggerHash);
        }

        /// <summary>Animation Event — place at the end of the Hit reaction clip.</summary>
        public void AE_HitReactionEnd()
        {
            _isHitStunned = false;
        }

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