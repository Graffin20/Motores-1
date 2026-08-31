using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Data for one stage of the light attack combo. Timing lives in the animation clip itself
    /// (via animation events), not here — this only holds what the state machine can't get from
    /// the clip: which trigger to fire, and how much damage it deals.
    /// </summary>
    [System.Serializable]
    public class LightAttackStage
    {
        [Tooltip("Animator trigger parameter name fired for this stage, e.g. \"Attack1\".")]
        public string AnimationTrigger = "Attack1";
        public float Damage = 10f;
        public float StaminaCost = 8f;
        [Tooltip("Multiplies the defender's own KnockbackDistance (see Health) — 1 = defender's default.")]
        public float KnockbackDistanceMultiplier = 1f;
        [Tooltip("Multiplies the defender's own KnockbackDuration (see Health) — 1 = defender's default. Independent from the distance multiplier.")]
        public float KnockbackDurationMultiplier = 1f;
    }

    /// <summary>
    /// Entity-agnostic melee combat state machine. Works identically for a player or an enemy —
    /// it doesn't read input or AI state directly, it only asks whichever component on this
    /// GameObject implements IMeleeCombatInputSource.
    ///
    /// Phase timing (Windup -> Active -> Recovery -> done) is driven entirely by animation events
    /// rather than hardcoded durations, so it always matches the actual clip. Each attack clip
    /// needs THREE animation events calling back into this component:
    ///   - AE_AttackActive()   at the moment the hitbox should turn on
    ///   - AE_AttackRecovery() at the moment the hitbox should turn off / recovery begins
    ///   - AE_AttackEnd()      at the very end of the clip, when the next action is allowed
    /// Plus, wherever the actual hit lands within the Active window:
    ///   - ApplyHitboxDamage() on the hit frame(s)
    /// Optionally, wherever the attack should be cancelable into a roll (see RollController):
    ///   - AE_RollCancelOpen()  opens the cancel window
    ///   - AE_RollCancelClose() closes it early (otherwise it auto-closes at AE_AttackEnd)
    ///
    /// Combo rules:
    /// - Light attacks chain 1 -> 2 -> 3. After 3, the combo ends and ComboCooldownDuration
    ///   (a design value, not an animation timing) applies before another combo can start.
    /// - Heavy attack can be used standalone from Idle, OR as a finisher queued during the
    ///   Recovery of combo stage 1 or 2 (0-indexed: stage 0 or 1) — never after stage 3.
    /// - Any heavy attack (standalone or finisher) ends the chain and triggers the cooldown.
    ///
    /// Intended to sit alongside ThirdPersonController: expose IsAttacking so Move() can gate
    /// rotation/locomotion during an attack.
    /// </summary>
    public class MeleeCombatController : MonoBehaviour
    {
        private enum AttackPhase
        {
            Idle,
            Windup,
            Active,
            Recovery
        }

        [Header("Light Attack Combo (in order)")]
        public LightAttackStage[] ComboStages = new LightAttackStage[3]
        {
            new LightAttackStage { AnimationTrigger = "Attack1" },
            new LightAttackStage { AnimationTrigger = "Attack2" },
            new LightAttackStage { AnimationTrigger = "Attack3" },
        };

        [Tooltip("Design cooldown (not animation timing) after the combo ends — 3rd light hit, or any heavy attack — before a new combo can start.")]
        public float ComboCooldownDuration = 0.35f;

        [Header("Heavy Attack (standalone or combo finisher)")]
        public string HeavyAnimationTrigger = "HeavyAttack";
        public float HeavyAttackDamage = 22f;
        public float HeavyAttackStaminaCost = 15f;
        [Tooltip("Multiplies the defender's own KnockbackDistance (see Health) — 1 = defender's default.")]
        public float HeavyKnockbackDistanceMultiplier = 1.5f;
        [Tooltip("Multiplies the defender's own KnockbackDuration (see Health) — 1 = defender's default. Independent from the distance multiplier.")]
        public float HeavyKnockbackDurationMultiplier = 1.5f;

        [Header("Hit Detection")]
        [Tooltip("Local offset from this transform for the hitbox sphere, e.g. out in front of the character.")]
        public Vector3 HitboxOffset = new Vector3(0f, 1f, 1f);
        public float HitboxRadius = 0.75f;
        public LayerMask HittableLayers;

        [Header("Dev Safety Net")]
        [Tooltip("If a clip is missing an animation event, the state machine would otherwise get stuck forever in that phase. This force-advances (and logs a warning) if a phase runs too long without its event firing. Not a real timing source — just prevents soft-locks while you're wiring up events.")]
        public bool EnablePhaseSafetyTimeout = true;
        public float PhaseSafetyTimeoutSeconds = 2f;

        // -- runtime state --
        private AttackPhase _phase = AttackPhase.Idle;
        private float _phaseElapsed;
        private float _comboLockoutTimer;

        // -1 = not currently in a light combo stage (either idle, or currently doing a heavy attack)
        private int _comboIndex = -1;
        private bool _isHeavyAttack;
        private bool _hasDamagedThisSwing;

        // buffered next action, queued during Recovery
        private bool _bufferedAttack;
        private bool _bufferedIsHeavy;
        private int _bufferedComboIndex; // only meaningful when _bufferedIsHeavy == false

        // True during a window opened by AE_RollCancelOpen() on the current clip — lets
        // RequestCancelForRoll() know the current attack may be aborted into a roll.
        private bool _canCancelIntoRoll;

        private IMeleeCombatInputSource _inputSource;
        private StaminaSystem _stamina;
        private BlockController _block;
        private Animator _animator;
        private bool _hasAnimator;

        private int[] _comboTriggerHashes;
        private int _heavyTriggerHash;

        /// <summary>True whenever mid-swing (Windup/Active/Recovery) — read this from
        /// ThirdPersonController.Move() to suppress rotation/locomotion while attacking.
        /// Note: does NOT cover the post-combo cooldown window, since the character has
        /// regained control by then — only the next *attack* is locked out.</summary>
        public bool IsAttacking => _phase != AttackPhase.Idle;

        /// <summary>True while the current attack is inside a roll-cancel window
        /// (opened via AE_RollCancelOpen on the clip).</summary>
        public bool CanCancelIntoRoll => _canCancelIntoRoll;

        private void Awake()
        {
            _inputSource = GetComponents<MonoBehaviour>().OfType<IMeleeCombatInputSource>().FirstOrDefault();

            if (_inputSource == null)
            {
                Debug.LogWarning($"{nameof(MeleeCombatController)} on '{name}' found no " +
                                  $"{nameof(IMeleeCombatInputSource)} implementation. Add " +
                                  $"{nameof(PlayerMeleeCombatInput)} or {nameof(AIMeleeCombatInput)}.", this);
            }

            _hasAnimator = TryGetComponent(out _animator);
            _stamina = GetComponent<StaminaSystem>(); // optional — null means attacks are unrestricted by stamina
            _block = GetComponent<BlockController>(); // optional — null means blocking never prevents attacks

            _comboTriggerHashes = ComboStages.Select(s => Animator.StringToHash(s.AnimationTrigger)).ToArray();
            _heavyTriggerHash = Animator.StringToHash(HeavyAnimationTrigger);
        }

        private void Update()
        {
            if (_inputSource == null) return;

            switch (_phase)
            {
                case AttackPhase.Idle:
                    TickIdle();
                    break;

                default: // Windup, Active, Recovery
                    // Capture the buffer as soon as it's requested, not only once Recovery
                    // starts — otherwise an anticipatory press during Windup/Active would be
                    // wiped by the input source's single-frame pulse before Recovery ever
                    // gets a chance to read it.
                    if (!_bufferedAttack) TryBufferNextAttack();
                    TickSafetyTimeout();
                    break;
            }
        }

        private void TickIdle()
        {
            if (_comboLockoutTimer > 0f)
            {
                _comboLockoutTimer -= Time.deltaTime;
                return;
            }

            // Cooldown has elapsed — a new combo only starts on a deliberate fresh press,
            // never on a leftover buffered input from before the cooldown began.
            TryStartAttackFromIdle();
        }

        private void TickSafetyTimeout()
        {
            if (!EnablePhaseSafetyTimeout) return;

            _phaseElapsed += Time.deltaTime;
            if (_phaseElapsed < PhaseSafetyTimeoutSeconds) return;

            Debug.LogWarning($"{nameof(MeleeCombatController)} on '{name}': phase {_phase} exceeded " +
                              $"{PhaseSafetyTimeoutSeconds}s without its animation event firing. " +
                              "Check that the clip has the matching AE_ method wired up. Force-advancing.", this);

            switch (_phase)
            {
                case AttackPhase.Windup: AE_AttackActive(); break;
                case AttackPhase.Active: AE_AttackRecovery(); break;
                case AttackPhase.Recovery: AE_AttackEnd(); break;
            }
        }

        private void TryStartAttackFromIdle()
        {
            if (_block != null && _block.IsBlocking) return;

            if (_inputSource.HeavyAttackRequested)
            {
                _inputSource.ConsumeHeavyAttackRequest();
                if (HasEnoughStamina(HeavyAttackStaminaCost)) BeginHeavyAttack();
            }
            else if (_inputSource.AttackRequested)
            {
                _inputSource.ConsumeAttackRequest();
                if (HasEnoughStamina(ComboStages[0].StaminaCost)) BeginLightAttack(stageIndex: 0);
            }
        }

        /// <summary>Checks and spends in one step — false means nothing was spent.</summary>
        private bool HasEnoughStamina(float cost) => _stamina == null || _stamina.TrySpend(cost);

        private void TryBufferNextAttack()
        {
            if (_block != null && _block.IsBlocking) return;

            bool canFinisher = CanQueueHeavyFinisher();
            bool canContinueCombo = CanContinueLightCombo();

            if (canFinisher && _inputSource.HeavyAttackRequested)
            {
                _inputSource.ConsumeHeavyAttackRequest();
                _bufferedAttack = true;
                _bufferedIsHeavy = true;
            }
            else if (canContinueCombo && _inputSource.AttackRequested)
            {
                _inputSource.ConsumeAttackRequest();
                _bufferedAttack = true;
                _bufferedIsHeavy = false;
                _bufferedComboIndex = _comboIndex + 1;
            }
            // If the combo is maxed out (just finished stage 3), a press here is intentionally
            // left unconsumed — no restart buffering. The player has to press again after the
            // cooldown to start a new combo.
        }

        /// <summary>Heavy finisher is only offered mid-combo, after stage 1 or 2 (index 0 or 1) — never after stage 3.</summary>
        private bool CanQueueHeavyFinisher()
        {
            return !_isHeavyAttack && _comboIndex >= 0 && _comboIndex < ComboStages.Length - 1;
        }

        private bool CanContinueLightCombo()
        {
            return !_isHeavyAttack && _comboIndex >= 0 && _comboIndex < ComboStages.Length - 1;
        }

        private void BeginLightAttack(int stageIndex)
        {
            _isHeavyAttack = false;
            _comboIndex = stageIndex;
            _hasDamagedThisSwing = false;
            _bufferedAttack = false; // never start an attack carrying leftover buffer state
            _canCancelIntoRoll = false; // each attack opens its own window fresh, via its own AE_RollCancelOpen
            EnterPhase(AttackPhase.Windup);

            if (_hasAnimator) _animator.SetTrigger(_comboTriggerHashes[stageIndex]);
        }

        private void BeginHeavyAttack()
        {
            _isHeavyAttack = true;
            _hasDamagedThisSwing = false;
            _bufferedAttack = false; // never start an attack carrying leftover buffer state
            _canCancelIntoRoll = false;
            EnterPhase(AttackPhase.Windup);

            if (_hasAnimator) _animator.SetTrigger(_heavyTriggerHash);
        }

        private void EnterPhase(AttackPhase phase)
        {
            _phase = phase;
            _phaseElapsed = 0f;
        }

        // ---- Animation Event callbacks: wire these onto each attack clip ----

        /// <summary>Place on the clip at the moment the hitbox should turn on (end of windup).</summary>
        public void AE_AttackActive()
        {
            if (_phase != AttackPhase.Windup) return;
            EnterPhase(AttackPhase.Active);
        }

        /// <summary>Place on the clip at the moment the hitbox should turn off / recovery begins.</summary>
        public void AE_AttackRecovery()
        {
            if (_phase != AttackPhase.Active) return;
            EnterPhase(AttackPhase.Recovery);
        }

        /// <summary>
        /// Place on a clip wherever it's OK to abort the attack into a roll — typically somewhere
        /// in Recovery, once the hit has already landed. Stays open until AE_RollCancelClose(),
        /// or automatically closes when the attack ends normally via AE_AttackEnd().
        /// </summary>
        public void AE_RollCancelOpen()
        {
            if (_phase == AttackPhase.Idle) return;
            _canCancelIntoRoll = true;
        }

        /// <summary>Place later on the same clip if the cancel window should close before the clip ends
        /// (e.g. to stop a cancel right before a combo-finisher moment). Optional — AE_AttackEnd()
        /// already closes the window automatically at the end of the clip.</summary>
        public void AE_RollCancelClose()
        {
            _canCancelIntoRoll = false;
        }

        /// <summary>
        /// Called by RollController before starting a roll. Returns true if it's fine to roll right
        /// now — either because nothing is happening, or because the current attack is inside its
        /// designated cancel window — and in the latter case aborts the attack immediately.
        /// </summary>
        public bool RequestCancelForRoll()
        {
            if (_phase == AttackPhase.Idle) return true;
            if (!_canCancelIntoRoll) return false;

            // Deliberately no ComboCooldownDuration here — bailing into a roll is a defensive
            // choice mid-attack, not a completed combo. Revisit if this turns out to be
            // exploitable as a way to skip the cooldown by always canceling the 3rd hit.
            ResetAttackState();
            return true;
        }

        /// <summary>
        /// Immediately aborts whatever attack is in progress — called by Health when the character
        /// takes damage, so a swing doesn't visually continue while staggering (or dying). Unlike
        /// RequestCancelForRoll(), this doesn't check CanCancelIntoRoll: getting hit isn't a choice
        /// the player is making, so it interrupts regardless of any designer-placed cancel window.
        /// </summary>
        public void ForceCancelAttack()
        {
            if (_phase == AttackPhase.Idle) return;

            ResetAttackState();

            // Unlike the roll-cancel, a forced interruption DOES apply the combo cooldown — being
            // hit breaks the combo rhythm regardless of which hit you were on, so the moment
            // hitstun ends shouldn't let you resume mid-combo instantly.
            _comboLockoutTimer = ComboCooldownDuration;

            // Defensively clear any combo/heavy trigger that may have been set but not yet
            // consumed by its transition — otherwise it can sit armed on the Animator and fire
            // as a phantom attack later, once the Hit/Die state exits back to combat idle. Same
            // class of bug as the stray-buffered-state issue we fixed on the combo state machine.
            if (_hasAnimator)
            {
                foreach (var hash in _comboTriggerHashes) _animator.ResetTrigger(hash);
                _animator.ResetTrigger(_heavyTriggerHash);
            }
        }

        /// <summary>Shared reset used by both RequestCancelForRoll() and ForceCancelAttack() —
        /// wipes all in-progress-attack state back to Idle. Callers are responsible for deciding
        /// whether ComboCooldownDuration applies afterward.</summary>
        private void ResetAttackState()
        {
            EnterPhase(AttackPhase.Idle);
            _isHeavyAttack = false;
            _comboIndex = -1;
            _bufferedAttack = false;
            _canCancelIntoRoll = false;
        }

        /// <summary>Place at the very end of the clip — the attack is fully finished and the next action is allowed.</summary>
        public void AE_AttackEnd()
        {
            if (_phase != AttackPhase.Recovery) return;

            bool comboEnded = _isHeavyAttack || _comboIndex == ComboStages.Length - 1;
            EnterPhase(AttackPhase.Idle);
            _isHeavyAttack = false;
            _comboIndex = -1;
            _canCancelIntoRoll = false;

            if (comboEnded)
            {
                // Combo is over — explicitly discard any buffered attack rather than assuming
                // it's already false. TickIdle() will only accept a fresh press once
                // ComboCooldownDuration elapses; nothing should carry through into it.
                _bufferedAttack = false;
                _comboLockoutTimer = ComboCooldownDuration;
            }
            else if (_bufferedAttack)
            {
                _bufferedAttack = false;
                if (_bufferedIsHeavy)
                {
                    if (HasEnoughStamina(HeavyAttackStaminaCost)) BeginHeavyAttack();
                }
                else if (HasEnoughStamina(ComboStages[_bufferedComboIndex].StaminaCost))
                {
                    BeginLightAttack(_bufferedComboIndex);
                }
            }
        }

        /// <summary>
        /// Place on the clip's actual hit frame(s), within the Active window.
        /// </summary>
        public void ApplyHitboxDamage()
        {
            if (_phase != AttackPhase.Active || _hasDamagedThisSwing) return;

            Vector3 origin = transform.TransformPoint(HitboxOffset);
            Collider[] hits = Physics.OverlapSphere(origin, HitboxRadius, HittableLayers);

            float damage = _isHeavyAttack ? HeavyAttackDamage : ComboStages[_comboIndex].Damage;
            float knockbackDistanceMultiplier = _isHeavyAttack ? HeavyKnockbackDistanceMultiplier : ComboStages[_comboIndex].KnockbackDistanceMultiplier;
            float knockbackDurationMultiplier = _isHeavyAttack ? HeavyKnockbackDurationMultiplier : ComboStages[_comboIndex].KnockbackDurationMultiplier;

            foreach (var hit in hits)
            {
                if (hit.transform.root == transform.root) continue; // don't hit self

                if (hit.TryGetComponent<IDamageable>(out var damageable))
                {
                    damageable.TakeDamage(damage, transform.position, knockbackDistanceMultiplier, knockbackDurationMultiplier);
                }
                else
                {
                    Debug.Log($"{name} hit {hit.name} for {damage} damage, but it has no IDamageable component.");
                }
            }

            _hasDamagedThisSwing = true;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = _phase == AttackPhase.Active ? Color.red : new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.TransformPoint(HitboxOffset), HitboxRadius);
        }
    }
}