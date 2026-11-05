using System.Linq;
using UnityEngine;
using UnityEngine.Audio;

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
        [Tooltip("Off for bosses (poise): plain hits deal damage and knockback but never interrupt. Being parried still staggers (that calls Stagger directly).")]
        public bool StaggerOnPlainHits = true;

        [Header("Audio")]
        [SerializeField] private AudioSource audioSource;

        [Tooltip("Sonido al recibir daño (golpe normal o guardia rota). Opcional.")]
        public SFXPlayer HurtSfx;
        [Tooltip("Sonido al morir. Opcional.")]
        public SFXPlayer DeathSfx;
        public bool playerVoiceLine;

        [Header("Death")]

        public string DieAnimationTrigger = "Die";
        public float DestroyDelayAfterDeath = 5f;
        [Tooltip("Activalo solo en enemigos: al morir suma una kill al KillCounter.")]
        public bool CountsAsKill = false;
        [Tooltip("Game object to instantiate at the character's position when they die. Optional.")]
        public GameObject DeathEffectPrefab;
        public Transform dropSpawnPosition; // Optional: if set, the death effect spawns at this position instead of the character's position

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

        // Event-driven actions a stagger/death must cancel (boss special attacks, ...).
        private IInterruptibleAction[] _interruptibleActions;

        private bool _isHitStunned;
        private float _hitStunTimer;

        // Held by another character (boss grab). A bool, not just the socket reference, so a destroyed
        // holder (fake-null socket) is noticed and released instead of silently reading as "not held".
        private bool _isHeld;
        private Transform _holdSocket;

        // Scripts to disable on death, gathered once at Awake. Deliberately NOT the
        // CharacterController itself — see the note in Die() below for why.
        private Behaviour[] _behavioursToDisableOnDeath;

        public bool IsDead { get; private set; }

        /// <summary>Raised once on death with the killing hit's SourcePosition (RagdollController pushes the
        /// body away from it).</summary>
        public event System.Action<Vector3> Died;

        /// <summary>Raised after every resolved hit (not on Ignored, i.e. already dead) with the hit and its
        /// outcome. HitFeedback spawns FX / hit-stop / camera shake from it.</summary>
        public event System.Action<HitInfo, HitResult> HitReceived;

        /// <summary>True during any stagger — a regular hit reaction, being parried, or a guard break
        /// (see Stagger()). Everything that refuses to act while stunned reads this, so new stagger
        /// sources are gated automatically.</summary>
        public bool IsHitStunned => _isHitStunned;

        /// <summary>True while a knockback push is moving this character. AI steering reads this so it
        /// doesn't fight the push with its own movement.</summary>
        public bool IsBeingKnockedBack => _isBeingKnockedBack;

        /// <summary>Goes up on every Stagger/BeginHold. Lets an attacker tell whether the stagger it caused is
        /// still the current one (the boss grab is only allowed during its own stomp's stun).</summary>
        public int StaggerId { get; private set; }

        /// <summary>True while another character holds this one (boss grab): position follows the holder's
        /// socket, IsHitStunned stays true (so every action and locomotion is refused), and hits only deal damage.</summary>
        public bool IsHeld => _isHeld;

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
            _interruptibleActions = GetComponents<MonoBehaviour>().OfType<IInterruptibleAction>().ToArray();

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
            if (_hitStunTimer <= 0f && _isHeld)
            {
                Debug.LogWarning($"{nameof(Health)} on '{name}': held longer than its safety duration without the holder " +
                                  "releasing it (AE_GrabRelease). Force-releasing.", this);
                EndHold(transform.position, 0f, 0f);
            }
            else if (_hitStunTimer <= 0f)
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
            HitResult result = ResolveHit(hit);
            if (result != HitResult.Ignored) HitReceived?.Invoke(hit, result);

            // Audio: solo con daño real (no suena al bloquear, hacer parry ni esquivar con roll)
            if (HurtSfx != null && (result == HitResult.Hit || result == HitResult.GuardBroken))
                HurtSfx.Play();

            return result;
        }

        private HitResult ResolveHit(HitInfo hit)
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
                    // The attack may ask for its own reaction (boss stomp = "Stunned").
                    return ApplyDamageAndStagger(hit, hit.Damage,
                                                 string.IsNullOrEmpty(hit.StaggerAnimationTrigger) ? HitAnimationTrigger : hit.StaggerAnimationTrigger,
                                                 hit.StaggerSafetyDuration > 0f ? hit.StaggerSafetyDuration : HitStunSafetyDuration,
                                                 HitResult.Hit,
                                                 allowStagger: StaggerOnPlainHits);
            }
        }

        /// <summary>Shared tail of a plain hit and a guard break: lose health, get pushed, then either
        /// die or stagger with the given reaction. Damage-only hits (DoT ticks) and hits taken while held
        /// skip the push and the stagger: a reaction would break the grab pose / stun-lock the defender.</summary>
        private HitResult ApplyDamageAndStagger(in HitInfo hit, float damage, string staggerTrigger,
                                                float staggerSafetyDuration, HitResult resultIfSurvived,
                                                bool allowStagger = true)
        {
            CurrentHealth -= damage;
            Debug.Log($"{gameObject.name} took {damage} damage ({resultIfSurvived}). Health remaining: {CurrentHealth}");

            bool reacts = !hit.DamageOnly && !_isHeld;
            if (reacts) ApplyKnockback(hit.SourcePosition, hit.KnockbackDistanceMultiplier, hit.KnockbackDurationMultiplier);

            if (CurrentHealth <= 0f)
            {
                Die(hit.SourcePosition);
                return HitResult.Killed;
            }

            if (reacts && allowStagger) Stagger(staggerSafetyDuration, staggerTrigger);
            return resultIfSurvived;
        }

        /// <summary>
        /// Pushes this character horizontally away from sourcePosition over time. Public on its own (not
        /// only via TakeDamage) so non-damaging effects, like the post-parry shockwave, can push too.
        /// </summary>
        public void ApplyKnockback(Vector3 sourcePosition, float distanceMultiplier = 1f, float durationMultiplier = 1f)
        {
            if (_controller == null || IsDead || _isHeld) return; // held: the holder's socket owns the position

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
            if (IsDead || _isHeld) return; // held: the holder ends it with EndHold (which then staggers)

            _isHitStunned = true;
            _hitStunTimer = safetyDuration;
            StaggerId++;

            CancelActions();
            PlayReaction(animationTrigger);
        }

        /// <summary>A stagger interrupts whatever action was playing; each action is cancelled explicitly
        /// because its own end event (AE_AttackEnd, AE_IsRolling, AE_SpecialAttackEnd) won't fire once the clip is cut.</summary>
        private void CancelActions()
        {
            _meleeCombat?.ForceCancelAttack();
            _roll?.ForceCancelRoll();
            foreach (var action in _interruptibleActions) action.ForceCancel();
        }

        /// <summary>Sets the trigger, falling back to Hit if this Animator doesn't have it.</summary>
        private void PlayReaction(string animationTrigger)
        {
            if (!_hasAnimator) return;

            int triggerHash = Animator.StringToHash(animationTrigger);
            if (!_animator.HasParameter(triggerHash)) triggerHash = _hitTriggerHash;
            if (_animator.HasParameter(triggerHash)) _animator.SetTrigger(triggerHash);
        }

        /// <summary>
        /// Another character grabs this one (boss grab). Replaces any current stagger: the Stunned clip is cut
        /// by animationTrigger, so its AE_StaggerEnd won't fire; the hold keeps IsHitStunned true until
        /// EndHold. While held, LateUpdate pins the position to socket (never parented: the 100x bone scale
        /// would rescale the character, and its CharacterController would collide with the holder's).
        /// safetyDuration is only the dev fallback if the holder never releases. Returns false if refused.
        /// </summary>
        public bool BeginHold(Transform socket, string animationTrigger, float safetyDuration)
        {
            if (IsDead || _isHeld || socket == null) return false;

            _isHeld = true;
            _holdSocket = socket;
            _isHitStunned = true;
            _hitStunTimer = safetyDuration;
            _isBeingKnockedBack = false; // the socket owns the position from now on
            StaggerId++;

            CancelActions();
            PlayReaction(animationTrigger);
            SnapToHoldSocket();
            return true;
        }

        /// <summary>The holder lets go: pushed away from pushSource, then a normal Hit reaction.</summary>
        public void EndHold(Vector3 pushSource, float knockbackDistanceMultiplier, float knockbackDurationMultiplier)
        {
            if (!_isHeld) return;
            ClearHold();
            if (IsDead) return;

            _isHitStunned = false; // so Stagger below starts a fresh reaction
            ApplyKnockback(pushSource, knockbackDistanceMultiplier, knockbackDurationMultiplier);
            Stagger(HitStunSafetyDuration, HitAnimationTrigger);
        }

        private void ClearHold()
        {
            _isHeld = false;
            _holdSocket = null;
            // The transform was moved directly; without this the CharacterController's next Move() can start
            // from its stale collider position and snap the character back into the holder's hands.
            Physics.SyncTransforms();
        }

        // LateUpdate: after the holder's Animator has posed its hand this frame.
        private void LateUpdate()
        {
            if (!_isHeld) return;
            if (_holdSocket == null)
            {
                EndHold(transform.position, 0f, 0f); // holder destroyed mid-grab
                return;
            }
            SnapToHoldSocket();
        }

        /// <summary>Position = socket; facing = the socket's forward, yaw only (the CharacterController stays upright).</summary>
        private void SnapToHoldSocket()
        {
            transform.position = _holdSocket.position;
            Vector3 forward = _holdSocket.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        /// <summary>Animation Event — place at the end of the Hit reaction clip.</summary>
        public void AE_HitReactionEnd()
        {
            if (_isHeld) return; // a clip cut by the grab can still fire its event during the transition
            _isHitStunned = false;
        }

        /// <summary>Animation Event — same as AE_HitReactionEnd, named for the Parried / GuardBreak clips.</summary>
        public void AE_StaggerEnd() => AE_HitReactionEnd();

        public void Heal(float heal)
        {
            if (IsDead) return;
            CurrentHealth = Mathf.Min(CurrentHealth + heal, MaxHealth);
        }

        private void Die(Vector3 killSourcePosition)
        {
            IsDead = true;
            _isHitStunned = false;
            Debug.Log($"{gameObject.name} died!");

            if (CountsAsKill && KillCounter.Instance != null)
                KillCounter.Instance.AddKill();

            if (_isHeld) ClearHold(); // died in the holder's hands: drop where it is
            CancelActions();

            if (_hasAnimator) _animator.SetTrigger(_dieTriggerHash);

            // Instantiate death effect at character position
            if (DeathEffectPrefab != null)
            {
                if (dropSpawnPosition != null)
                {
                    Instantiate(DeathEffectPrefab, dropSpawnPosition.position, Quaternion.identity);
                } else
                {
                    Instantiate(DeathEffectPrefab, transform.position, Quaternion.identity);
                }
            }

            // Stop the scripts that DRIVE movement/combat, rather than disabling the
            // CharacterController component that they call into.
            foreach (var behaviour in _behavioursToDisableOnDeath)
            {
                behaviour.enabled = false;
            }

            if (TryGetComponent<UnityEngine.AI.NavMeshAgent>(out var agent)) agent.enabled = false;

            // Disable the CharacterController so the body stops blocking characters, casts and the
            // ragdoll. Safe only because nothing calls Move() on it any more: the scripts above are off,
            // the death knockback is stopped (TickKnockback would Move() for a few more frames), and
            // ThirdPersonController (kept running for the camera) skips movement while it's disabled.
            _isBeingKnockedBack = false;
            if (_controller != null) _controller.enabled = false;

            if (DeathSfx != null) DeathSfx.Play();
            Died?.Invoke(killSourcePosition);
            if (playerVoiceLine == true)
            {
                if (audioSource != null)
                {
                    audioSource.Play();
                }
            }
            Destroy(gameObject, DestroyDelayAfterDeath);
        }
    }
}
