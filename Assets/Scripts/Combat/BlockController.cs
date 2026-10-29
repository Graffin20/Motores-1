using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>How the block side resolved one incoming hit (see BlockController.ResolveIncomingHit).</summary>
    public enum BlockResolution
    {
        NotBlocked,
        Parried,
        Blocked,
        GuardBroken
    }

    /// <summary>
    /// Entity-agnostic block and parry. Works for player or AI via IMeleeCombatInputSource.
    ///
    /// - Block: while held, hits from the front (BlockArcDegrees) are absorbed, costing stamina
    ///   proportional to the hit's damage (StaminaCostPerDamage).
    /// - Guard break: if stamina can't cover the full cost, all remaining stamina is spent, the part of
    ///   the damage it couldn't cover goes through, and the defender is staggered.
    /// - Parry: PRESSING block (the press itself, not holding it) while the attacker's parry window is
    ///   open (AE_ParryWindowOpen/Close on the attacker's clip). Holding block from before the window
    ///   opened just blocks. A parry negates the hit, staggers the attacker (the attacker does that to
    ///   itself from the returned HitResult), grants a short invulnerability window and pushes
    ///   nearby characters away.
    /// - Parry recovery: if the Animator has the Parry trigger, the parrier is locked out (IsParrying:
    ///   no movement, attack, roll or block) until AE_ParryEnd fires on the Parry clip. Where that
    ///   event sits decides how fast the parrier can punish the staggered attacker.
    ///
    /// Health owns the order of resolution and calls ResolveIncomingHit(); this component only decides
    /// the block side and applies its own costs/effects.
    /// </summary>
    public class BlockController : MonoBehaviour, IInvulnerabilitySource
    {
        [Header("Animation")]
        [Tooltip("Animator bool parameter that stays true for as long as blocking is held. Optional now that the masked layer's weight is what actually controls visibility, but harmless to keep if other systems read it.")]
        public string BlockAnimatorBool = "IsBlocking";

        [Header("Masked Block Layer")]
        [Tooltip("Name of the Animator layer (with the block Avatar Mask assigned) that should only be active while blocking. That layer should contain a single BlockLoop state — no internal transitions needed, since weight alone controls whether it has any effect.")]
        public string BlockLayerName = "Block";
        [Tooltip("How fast the layer's weight blends between 0 and 1. Set very high (e.g. 100) for an instant on/off snap instead of a blend.")]
        public float LayerWeightBlendSpeed = 12f;

        [Header("Blocking")]
        [Tooltip("Total frontal arc (degrees) that block and parry cover, centered on the character's forward. 180 = everything in front; hits from outside the arc ignore the guard.")]
        [Range(0f, 360f)]
        public float BlockArcDegrees = 120f;
        [Tooltip("Stamina spent per point of damage absorbed. 1 = a 20-damage hit costs 20 stamina. 0 = blocking is free.")]
        public float StaminaCostPerDamage = 1f;
        [Tooltip("Multiplies the attack's knockback distance on a successful block (0 = the guard holds its ground).")]
        public float BlockedKnockbackMultiplier = 0.5f;

        [Header("Guard Break")]
        [Tooltip("Animator trigger for the guard-break stagger. Falls back to Health's Hit trigger if this parameter doesn't exist.")]
        public string GuardBreakAnimationTrigger = "GuardBreak";
        [Tooltip("Dev safety fallback only: the stagger normally ends via AE_StaggerEnd on the GuardBreak clip.")]
        public float GuardBreakStaggerSafetyDuration = 1.5f;

        [Header("Parry")]
        [Tooltip("Optional Animator trigger played on a successful parry. If this parameter doesn't exist, the parry has no animation and no recovery lockout.")]
        public string ParryAnimationTrigger = "Parry";
        [Tooltip("Dev safety fallback only: the parry recovery normally ends via AE_ParryEnd on the Parry clip.")]
        public float ParrySafetyDuration = 1.5f;
        [Tooltip("Design value (not an animation timing): how long after a successful parry all incoming damage is negated.")]
        public float PostParryInvulnerabilityDuration = 0.5f;
        [Tooltip("Radius of the omnidirectional push applied to nearby characters on a successful parry.")]
        public float ParryPushRadius = 3f;
        [Tooltip("Layers the parry push affects — usually the same layers your attacks' HittableLayers use.")]
        public LayerMask ParryPushLayers;
        [Tooltip("Multiplies each pushed character's own KnockbackDistance (see Health).")]
        public float ParryPushDistanceMultiplier = 1.5f;
        [Tooltip("Multiplies each pushed character's own KnockbackDuration (see Health).")]
        public float ParryPushDurationMultiplier = 1f;

        private IMeleeCombatInputSource _inputSource;
        private StaminaSystem _stamina;
        private Health _health;
        private MeleeCombatController _combat;
        private RollController _roll;
        private Animator _animator;
        private bool _hasAnimator;
        private int _blockBoolHash;
        private int _parryTriggerHash;
        private int _blockLayerIndex = -1;

        private bool _wasBlockHeld;
        // Time.time of the most recent block PRESS made while blocking was allowed. Only a press can
        // parry — this is never updated by a held block, so holding early can't auto-parry.
        private float _lastBlockPressTime = float.NegativeInfinity;
        private float _postParryTimer;

        private bool _isParrying;
        private float _parrySafetyTimer;

        private readonly HashSet<Health> _pushedThisParry = new();

        public bool IsBlocking { get; private set; }

        /// <summary>True from a successful parry until AE_ParryEnd: the parry animation is playing and the
        /// character is locked out (ThirdPersonController.ShouldLockMovement, attack and roll all check it).</summary>
        public bool IsParrying => _isParrying;

        /// <summary>True during the post-parry window. Health picks this up via IInvulnerabilitySource.</summary>
        public bool IsInvulnerable => _postParryTimer > 0f;

        private void Awake()
        {
            _inputSource = GetComponents<MonoBehaviour>().OfType<IMeleeCombatInputSource>().FirstOrDefault();
            _stamina = GetComponent<StaminaSystem>();         // optional — null means blocking is free and never breaks
            _health = GetComponent<Health>();                  // optional — null means no hitstun/death gating
            _combat = GetComponent<MeleeCombatController>();   // optional — null means attacking never prevents blocking
            _roll = GetComponent<RollController>();            // optional — null means rolling never prevents blocking
            _hasAnimator = TryGetComponent(out _animator);
            _blockBoolHash = Animator.StringToHash(BlockAnimatorBool);
            _parryTriggerHash = Animator.StringToHash(ParryAnimationTrigger);

            if (_hasAnimator)
            {
                _blockLayerIndex = _animator.GetLayerIndex(BlockLayerName);
                if (_blockLayerIndex < 0)
                {
                    Debug.LogWarning($"{nameof(BlockController)} on '{name}' couldn't find an Animator layer " +
                                      $"named '{BlockLayerName}'. The masked block layer's weight won't be controlled.", this);
                }
                else
                {
                    _animator.SetLayerWeight(_blockLayerIndex, 0f); // start fully off
                }
            }

            if (_inputSource == null)
            {
                Debug.LogWarning($"{nameof(BlockController)} on '{name}' found no " +
                                  $"{nameof(IMeleeCombatInputSource)} implementation. Add " +
                                  $"{nameof(PlayerMeleeCombatInput)} or {nameof(AIMeleeCombatInput)}.", this);
            }
        }

        private void Update()
        {
            if (_postParryTimer > 0f) _postParryTimer -= Time.deltaTime;
            TickParryRecovery();

            if (_inputSource == null) return;

            bool held = _inputSource.BlockHeld;
            bool canBlock = CanBlock();

            // Rising edge of the INPUT, not of IsBlocking: a block held through an attack becomes
            // active when the attack ends, and that must not count as a fresh (parrying) press.
            if (held && !_wasBlockHeld && canBlock) _lastBlockPressTime = Time.time;
            _wasBlockHeld = held;

            SetBlocking(held && canBlock);
            TickLayerWeight();
        }

        /// <summary>Blocking is refused (and an active block dropped) while staggered, dead, mid-swing or
        /// mid-roll. Attack/roll already refuse to start while blocking, so this makes the states
        /// mutually exclusive in both directions.</summary>
        private bool CanBlock()
        {
            if (_health != null && (_health.IsHitStunned || _health.IsDead)) return false;
            if (_isParrying) return false; // also keeps the Block layer blended out so the Parry clip shows
            if (_combat != null && _combat.IsAttacking) return false;
            if (_roll != null && _roll.IsRolling) return false;
            return true;
        }

        /// <summary>
        /// Called by Health for every hit that isn't negated by invulnerability. Decides parry / block /
        /// guard break, spends stamina and applies parry effects. damageThrough is how much of the hit
        /// still reaches health (0 unless guard broken or not blocked).
        /// </summary>
        public BlockResolution ResolveIncomingHit(in HitInfo hit, out float damageThrough)
        {
            damageThrough = hit.Damage;

            if (!CanBlock() || !IsFacing(hit.SourcePosition)) return BlockResolution.NotBlocked;

            if (IsTimedParry(hit))
            {
                damageThrough = 0f;
                OnParrySuccess();
                return BlockResolution.Parried;
            }

            if (!IsBlocking) return BlockResolution.NotBlocked;

            if (_stamina == null || StaminaCostPerDamage <= 0f)
            {
                damageThrough = 0f;
                return BlockResolution.Blocked;
            }

            float cost = hit.Damage * StaminaCostPerDamage;
            if (_stamina.TrySpend(cost))
            {
                damageThrough = 0f;
                return BlockResolution.Blocked;
            }

            // Not enough stamina: whatever is left absorbs its share, the rest goes through.
            // e.g. 20 damage vs 10 stamina at 1 stamina/damage = 10 absorbed, 10 taken + stagger.
            float spent = _stamina.SpendUpTo(cost);
            damageThrough = Mathf.Max(0f, hit.Damage - spent / StaminaCostPerDamage);
            return BlockResolution.GuardBroken;
        }

        /// <summary>The last block press fell inside the attacker's parry window. The press must be inside
        /// [open, close]: pressing before the window (or holding from earlier) doesn't count, and neither
        /// does a press left over from a previous attack.</summary>
        private bool IsTimedParry(in HitInfo hit)
        {
            if (!hit.IsParryable) return false;
            return _lastBlockPressTime >= hit.ParryWindowOpenTime && _lastBlockPressTime <= hit.ParryWindowCloseTime;
        }

        /// <summary>Block direction from the hit source position the attacker already sends — no second
        /// detection pass needed.</summary>
        private bool IsFacing(Vector3 sourcePosition)
        {
            if (BlockArcDegrees >= 360f) return true;

            Vector3 toSource = sourcePosition - transform.position;
            toSource.y = 0f;
            if (toSource.sqrMagnitude < 0.0001f) return true; // source on top of us — direction is meaningless

            return Vector3.Angle(transform.forward, toSource) <= BlockArcDegrees * 0.5f;
        }

        private void OnParrySuccess()
        {
            // One press = one parry: further hits in the same window are covered by the
            // post-parry invulnerability instead of each counting as another parry.
            _lastBlockPressTime = float.NegativeInfinity;
            _postParryTimer = PostParryInvulnerabilityDuration;

            // The recovery lockout only exists if there's a Parry clip to end it (AE_ParryEnd);
            // without one, the parry is instant and the character stays free to act.
            if (_hasAnimator && _animator.HasParameter(_parryTriggerHash))
            {
                _animator.SetTrigger(_parryTriggerHash);
                _isParrying = true;
                _parrySafetyTimer = ParrySafetyDuration;
            }

            PushNearbyCharacters();
        }

        private void TickParryRecovery()
        {
            if (!_isParrying) return;

            // A stagger or death (e.g. hit after the post-parry window ran out) replaces the Parry clip,
            // so its AE_ParryEnd will never fire.
            if (_health != null && (_health.IsHitStunned || _health.IsDead))
            {
                _isParrying = false;
                return;
            }

            _parrySafetyTimer -= Time.deltaTime;
            if (_parrySafetyTimer <= 0f)
            {
                Debug.LogWarning($"{nameof(BlockController)} on '{name}': parry recovery exceeded {ParrySafetyDuration}s " +
                                  "without AE_ParryEnd() firing. Check the Parry clip has the event wired up. Force-ending.", this);
                _isParrying = false;
            }
        }

        /// <summary>Animation Event — place on the Parry clip where the character may move/act again.
        /// Can be before the clip's end: the Animator's own exit transition handles the visual return to locomotion.</summary>
        public void AE_ParryEnd()
        {
            _isParrying = false;
        }

        private void PushNearbyCharacters()
        {
            if (ParryPushRadius <= 0f) return;

            _pushedThisParry.Clear();
            Collider[] hits = Physics.OverlapSphere(transform.position, ParryPushRadius, ParryPushLayers);

            foreach (var hit in hits)
            {
                if (hit.transform.root == transform.root) continue; // don't push self

                // A character can have several colliders in range; push each one once.
                if (hit.TryGetComponent<Health>(out var health) && _pushedThisParry.Add(health))
                {
                    health.ApplyKnockback(transform.position, ParryPushDistanceMultiplier, ParryPushDurationMultiplier);
                }
            }
        }

        private void TickLayerWeight()
        {
            if (!_hasAnimator || _blockLayerIndex < 0) return;

            float target = IsBlocking ? 1f : 0f;
            float current = _animator.GetLayerWeight(_blockLayerIndex);

            if (Mathf.Approximately(current, target)) return;

            float next = Mathf.MoveTowards(current, target, LayerWeightBlendSpeed * Time.deltaTime);
            _animator.SetLayerWeight(_blockLayerIndex, next);
        }

        private void SetBlocking(bool blocking)
        {
            if (blocking == IsBlocking) return;

            IsBlocking = blocking;
            if (_hasAnimator) _animator.SetBool(_blockBoolHash, IsBlocking);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, ParryPushRadius);

            // Block arc edges
            Vector3 left = Quaternion.Euler(0f, -BlockArcDegrees * 0.5f, 0f) * transform.forward;
            Vector3 right = Quaternion.Euler(0f, BlockArcDegrees * 0.5f, 0f) * transform.forward;
            Vector3 origin = transform.position + Vector3.up;
            Gizmos.DrawLine(origin, origin + left * 1.5f);
            Gizmos.DrawLine(origin, origin + right * 1.5f);
        }
    }
}
