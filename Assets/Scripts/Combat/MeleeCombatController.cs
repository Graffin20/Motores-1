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
        [Tooltip("Distance to push the character forward during this attack stage.")]
        public float ForwardPushDistance = 1f;
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
    /// Optionally, wherever a defender's block PRESS should count as a parry (see BlockController):
    ///   - AE_ParryWindowOpen()  opens the parry window (usually shortly before the hit frame)
    ///   - AE_ParryWindowClose() closes it (if missing, it's treated as closing at the hit)
    ///   An attack whose clip never opens the window can be blocked but not parried.
    ///
    /// Combo rules:
    /// - Light attacks chain through ComboStages in order (any length). After the last stage, the
    ///   combo ends and ComboCooldownDuration (a design value, not an animation timing) applies
    ///   before another combo can start.
    /// - Heavy attack can be used standalone from Idle, OR as a finisher queued after any
    ///   non-final light stage — never after the last one.
    /// - Any heavy attack (standalone or finisher) ends the chain and triggers the cooldown.
    /// - Being parried staggers this character (Health.Stagger), which cancels the attack.
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
        [Tooltip("Distance to push the character forward during the heavy attack.")]
        public float HeavyForwardPushDistance = 2f;

        [Header("Hit Detection")]
        [Tooltip("Local offset from this transform for the hitbox sphere, e.g. out in front of the character.")]
        public Vector3 HitboxOffset = new Vector3(0f, 1f, 1f);
        public float HitboxRadius = 0.75f;
        public LayerMask HittableLayers;

        [Header("Being Parried")]
        [Tooltip("Animator trigger for the stagger played when one of this character's attacks is parried. Falls back to Health's Hit trigger if this parameter doesn't exist.")]
        public string ParriedAnimationTrigger = "Parried";
        [Tooltip("Dev safety fallback only: the stagger normally ends via AE_StaggerEnd on the Parried clip.")]
        public float ParriedStaggerSafetyDuration = 1.5f;

        [Header("Attack Movement")]
        [Tooltip("Duration (in seconds) over which the forward push is applied.")]
        public float AttackPushDuration = 0.3f;

        [Header("Target Facing")]
        [Tooltip("Fallback for FaceTarget: if the input source gives no aim direction (TryGetAttackDirection), the character snaps to face this at the start of each attack. EnemyAI sets it to the player. LockBridge also sets it while locked on, but the player's camera direction takes priority.")]
        public Transform lookAtReference;

        [Tooltip("Keep turning toward lookAtReference during Windup, until AE_AttackActive commits the swing. Makes attacks harder to dodge early, but the swing itself never follows the target. Meant for enemies; leave off for the player (its aim comes from the camera at attack start).")]
        public bool TrackTargetDuringWindup = false;
        [Tooltip("Max turn rate (degrees/second) while tracking during Windup. Lower = a fast sidestep during the windup can still outpace the turn.")]
        public float WindupTrackingTurnSpeed = 540f;

        // -- runtime state --
        private AttackPhase _phase = AttackPhase.Idle;
        private float _phaseElapsed;
        private float _comboLockoutTimer;
        private float _currentForwardPush;
        private float _currentPushDuration;
        private float _forwardPushElapsed;
        private float _pendingPushDistance; // this attack's data-driven push distance, used when the animation event doesn't pass one

        private CharacterController _characterController;

        [Header("Dev Safety Net")]
        [Tooltip("If a clip is missing an animation event, the state machine would otherwise get stuck forever in that phase. This force-advances (and logs a warning) if a phase runs too long without its event firing. Not a real timing source — just prevents soft-locks while you're wiring up events.")]
        public bool EnablePhaseSafetyTimeout = true;
        public float PhaseSafetyTimeoutSeconds = 2f;

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

        // Parry window of the CURRENT attack, opened/closed by AE_ParryWindowOpen/Close. Times are
        // sent with each hit so the defender can check whether its block press fell inside.
        private bool _parryWindowOpen;
        private bool _parryWindowOpenedThisAttack;
        private float _parryWindowOpenTime;
        private float _parryWindowCloseTime;

        // Reused per swing so one target with several colliders in the hitbox is only hit once.
        private readonly System.Collections.Generic.HashSet<IDamageable> _damagedThisSwing = new();

        private IMeleeCombatInputSource _inputSource;
        private StaminaSystem _stamina;
        private BlockController _block;
        private Health _health;
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

        /// <summary>True while the current attack's parry window is open. Not used by the parry check
        /// itself (that uses the times sent in HitInfo); exposed so an AI can decide when to parry.</summary>
        public bool IsParryWindowOpen => _parryWindowOpen;

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
            _health = GetComponent<Health>();          // optional — null means no hitstun/death gating
            _characterController = GetComponent<CharacterController>();

            _comboTriggerHashes = ComboStages.Select(s => Animator.StringToHash(s.AnimationTrigger)).ToArray();
            _heavyTriggerHash = Animator.StringToHash(HeavyAnimationTrigger);
        }

        private void Update()
        {
            if (_inputSource == null) return;
            if (GameManager.Instance != null && GameManager.Instance.levelComplete) return;

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
                    TrackTargetDuringWindupPhase();
                    ApplyForwardPush();
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

        /// <summary>Hitstun can outlast ComboCooldownDuration (which ForceCancelAttack applies), so the
        /// cooldown alone doesn't stop a fresh press from starting a swing mid-stagger.</summary>
        private bool IsIncapacitated => _health != null && (_health.IsHitStunned || _health.IsDead);

        private void TryStartAttackFromIdle()
        {
            if (_block != null && (_block.IsBlocking || _block.IsParrying)) return; // parrying: the Parry clip owns the Animator until AE_ParryEnd
            if (IsIncapacitated) return;

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
            _bufferedAttack = false;
            _canCancelIntoRoll = false;
            ResetParryWindow();
            ClearForwardPush();
            _pendingPushDistance = ComboStages[stageIndex].ForwardPushDistance;
            FaceTarget();
            EnterPhase(AttackPhase.Windup);

            if (_hasAnimator) _animator.SetTrigger(_comboTriggerHashes[stageIndex]);
        }

        private void BeginHeavyAttack()
        {
            _isHeavyAttack = true;
            _hasDamagedThisSwing = false;
            _bufferedAttack = false;
            _canCancelIntoRoll = false;
            ResetParryWindow();
            ClearForwardPush();
            _pendingPushDistance = HeavyForwardPushDistance;
            FaceTarget();
            EnterPhase(AttackPhase.Windup);

            if (_hasAnimator) _animator.SetTrigger(_heavyTriggerHash);
        }

        /// <summary>Snaps facing at the start of each attack/combo stage. The input source's aim direction
        /// wins (player: camera look, also while locked on); lookAtReference is the fallback (AI). Neither
        /// available = keep the current facing.</summary>
        private void FaceTarget()
        {
            Vector3 direction;
            if (_inputSource != null && _inputSource.TryGetAttackDirection(out direction))
            {
                // use the requested aim direction
            }
            else if (lookAtReference != null)
            {
                direction = lookAtReference.position - transform.position;
            }
            else
            {
                return;
            }

            direction.y = 0f; // Only rotate around Y axis
            if (direction.sqrMagnitude < 0.001f) return; // Avoid degenerate case (e.g. camera looking straight down)

            transform.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        /// <summary>Turns toward lookAtReference only while in Windup. Animation events run after Update,
        /// so tracking stops on exactly the frame AE_AttackActive moves the phase to Active.</summary>
        private void TrackTargetDuringWindupPhase()
        {
            if (!TrackTargetDuringWindup || _phase != AttackPhase.Windup || lookAtReference == null) return;

            Vector3 direction = lookAtReference.position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.001f) return;

            Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation,
                                                          WindupTrackingTurnSpeed * Time.deltaTime);
        }

        private void ApplyForwardPush()
        {
            if (_characterController == null || _currentForwardPush <= 0f || _currentPushDuration <= 0f) return;

            // Clamp the final step so the total distance covered is exactly the requested distance.
            float step = Mathf.Min(Time.deltaTime, _currentPushDuration - _forwardPushElapsed);
            if (step <= 0f)
            {
                ClearForwardPush();
                return;
            }

            _forwardPushElapsed += step;

            // Move in the direction the character is facing
            _characterController.Move(transform.forward * (_currentForwardPush / _currentPushDuration * step));

            if (_forwardPushElapsed >= _currentPushDuration) ClearForwardPush();
        }

        /// <summary>Stops any in-progress lunge. Called whenever an attack starts, ends, or is cancelled so a
        /// half-finished push can never leak into the next attack as a phantom lunge.</summary>
        private void ClearForwardPush()
        {
            _currentForwardPush = 0f;
            _forwardPushElapsed = 0f;
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

        /// <summary>Place on the clip at the moment the character should begin lunging forward. The float
        /// parameter is an optional distance override: leave it at 0 to use this attack's ForwardPushDistance
        /// (per-stage / heavy data), or pass a value to override it for this clip. The push always lasts
        /// AttackPushDuration.</summary>
        public void AE_BeginForwardPush(float distance)
        {
            if (_phase == AttackPhase.Idle) return; // stray event after a cancel — nothing to lunge for

            _currentForwardPush = distance > 0f ? distance : _pendingPushDistance;
            _currentPushDuration = AttackPushDuration;
            _forwardPushElapsed = 0f;
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

        /// <summary>Place on the clip where a defender's block press starts counting as a parry —
        /// usually a little before the hit frame, so the tell is readable.</summary>
        public void AE_ParryWindowOpen()
        {
            if (_phase == AttackPhase.Idle) return; // stray event after a cancel
            _parryWindowOpen = true;
            _parryWindowOpenedThisAttack = true;
            _parryWindowOpenTime = Time.time;
            _parryWindowCloseTime = float.PositiveInfinity; // still open — clamped to the hit time in ApplyHitboxDamage
        }

        /// <summary>Place where the parry window ends. Optional: if the hit lands while the window is
        /// still open, presses up to the hit itself count.</summary>
        public void AE_ParryWindowClose()
        {
            if (!_parryWindowOpen) return;
            _parryWindowOpen = false;
            _parryWindowCloseTime = Time.time;
        }

        private void ResetParryWindow()
        {
            _parryWindowOpen = false;
            _parryWindowOpenedThisAttack = false;
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
            ResetParryWindow();
            ClearForwardPush();
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
            ResetParryWindow();
            ClearForwardPush();

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

            var hitInfo = new HitInfo(damage, transform.position, knockbackDistanceMultiplier, knockbackDurationMultiplier)
            {
                Attacker = transform.root.gameObject,
                IsParryable = _parryWindowOpenedThisAttack,
                ParryWindowOpenTime = _parryWindowOpenTime,
                // A window still open at the hit counts presses up to this moment.
                ParryWindowCloseTime = Mathf.Min(_parryWindowCloseTime, Time.time),
            };

            bool wasParried = false;
            _damagedThisSwing.Clear();

            foreach (var hit in hits)
            {
                if (hit.transform.root == transform.root) continue; // don't hit self

                if (hit.TryGetComponent<IDamageable>(out var damageable))
                {
                    if (!_damagedThisSwing.Add(damageable)) continue; // same target, another collider

                    if (damageable.TakeDamage(hitInfo) == HitResult.Parried) wasParried = true;
                }
                else
                {
                    Debug.Log($"{name} hit {hit.name} for {damage} damage, but it has no IDamageable component.");
                }
            }

            _hasDamagedThisSwing = true;

            // After the loop, not inside it: the stagger cancels this attack (ForceCancelAttack), and
            // every target caught by this same swing should still be resolved with its data.
            if (wasParried) OnParried();
        }

        /// <summary>One of this character's attacks was parried — stagger (which also cancels the attack
        /// and applies the combo cooldown). Without a Health there's no stagger state, so just cancel.</summary>
        private void OnParried()
        {
            if (_health != null) _health.Stagger(ParriedStaggerSafetyDuration, ParriedAnimationTrigger);
            else ForceCancelAttack();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = _phase == AttackPhase.Active ? Color.red : new Color(1f, 0.5f, 0f, 0.4f);
            Gizmos.DrawWireSphere(transform.TransformPoint(HitboxOffset), HitboxRadius);
        }
    }
}