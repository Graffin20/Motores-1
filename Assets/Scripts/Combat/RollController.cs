using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Entity-agnostic roll/dodge. Works for player or AI via IMeleeCombatInputSource, same
    /// pattern as MeleeCombatController. Moves the character using CharacterController.Move
    /// (this project uses CharacterController, not Rigidbody, so no physics forces are involved)
    /// covering RollDistance over RollDuration in the direction the player's input indicates
    /// (camera-relative, or target-relative while locked on — same convention as ThirdPersonController.Move(), see TryStartRoll()),
    /// with the character smoothly rotating to face that direction as the roll plays out.
    ///
    /// Costs stamina via StaminaSystem if one is present on this GameObject (optional — rolling
    /// is unrestricted if no StaminaSystem is attached).
    ///
    /// Optionally invulnerable during part of the roll: place AE_IFrameStart() and AE_IFrameEnd()
    /// on the clip wherever the dodge should actually avoid damage. Health checks IsInvulnerable
    /// (via IInvulnerabilitySource) before applying any hit, so no changes are needed on the attacker's side.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class RollController : MonoBehaviour, IInvulnerabilitySource
    {
        [Header("Roll Movement")]
        public float RollDistance = 3f;
        public float RollDuration = 0.4f;
        public float RotationSpeed = 10f; // Smooth rotation speed multiplier

        [Header("Stamina")]
        public float RollStaminaCost = 20f;

        [Header("Animation")]
        [Tooltip("Animator trigger fired when the roll starts.")]
        public string RollAnimationTrigger = "Roll";

        [Header("Safety Net")]
        [Tooltip("If the roll animation's end event never fires, force-end the roll after this long so the character doesn't get stuck rolling forever.")]
        public float MaxRollSafetyDuration = 1.5f;

        private CharacterController _controller;
        private StaminaSystem _stamina;
        private MeleeCombatController _combat;
        private BlockController _block;
        private Health _health;
        private StarterAssetsInputs _input;
        private IMeleeCombatInputSource _inputSource;
        private Animator _animator;
        private bool _hasAnimator;
        private int _rollTriggerHash;
        private ThirdPersonController _thirdPersonController;

        private bool _isRolling;
        private float _rollTimer;
        private Vector3 _rollDirection;
        private bool _isInvulnerable;
        private Quaternion _targetRotation; // target rotation to smoothly turn towards during the roll

        /// <summary>True while the roll is in progress — read this from ThirdPersonController.Move()
        /// (alongside MeleeCombatController.IsAttacking) to suppress normal locomotion during the roll,
        /// and from MeleeCombatController to prevent attacking mid-roll if that's the feel you want.</summary>
        public bool IsRolling => _isRolling;

        /// <summary>True during the invulnerability window opened by AE_IFrameStart() on the roll clip.
        /// Health checks this before applying any damage.</summary>
        public bool IsInvulnerable => _isInvulnerable;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _stamina = GetComponent<StaminaSystem>();       // optional — null is fine, roll just goes unrestricted
            _combat = GetComponent<MeleeCombatController>(); // optional — null means nothing blocks rolling on attack state
            _block = GetComponent<BlockController>();         // optional — null means blocking never prevents rolling
            _health = GetComponent<Health>();                  // optional — null means no hitstun/death gating
            _inputSource = GetComponent<IMeleeCombatInputSource>();
            _hasAnimator = TryGetComponent(out _animator);
            _rollTriggerHash = Animator.StringToHash(RollAnimationTrigger);
            _input = GetComponent<StarterAssetsInputs>();

            // Source of the reference yaw used to interpret _input.move (camera in free movement, target
            // while locked on) rather than the character's own transform — see TryStartRoll() for why
            // that distinction matters. Optional — null falls back to the character's facing.
            _thirdPersonController = GetComponent<ThirdPersonController>();

            if (_inputSource == null)
            {
                Debug.LogWarning($"{nameof(RollController)} on '{name}' found no " +
                                  $"{nameof(IMeleeCombatInputSource)} implementation. Add " +
                                  $"{nameof(PlayerMeleeCombatInput)} or {nameof(AIMeleeCombatInput)}.", this);
            }
        }

        private void Update()
        {
            if (_isRolling)
            {
                TickRoll();
                return;
            }

            if (_inputSource == null) return;

            if (_inputSource.RollRequested)
            {
                _inputSource.ConsumeRollRequest();
                TryStartRoll();
            }
        }

        private void TryStartRoll()
        {
            if (_block != null && (_block.IsBlocking || _block.IsParrying)) return;
            if (_health != null && (_health.IsHitStunned || _health.IsDead)) return;

            if (_stamina != null && !_stamina.HasEnoughStamina(RollStaminaCost)) return;

            if (_combat != null && !_combat.RequestCancelForRoll()) return;

            _stamina?.TrySpend(RollStaminaCost);

            _isRolling = true;
            _rollTimer = MaxRollSafetyDuration;

            if (_input != null && _input.move != Vector2.zero && _thirdPersonController != null)
            {
                // Interpret input in the same frame ThirdPersonController.Move() uses: relative to the
                // CAMERA in free movement, relative to the TARGET while locked on (left/right = roll
                // around it, back = roll away). Using transform.forward/right (the character's own
                // current facing) here instead would anchor the roll direction to whatever way the
                // character happened to already be facing — which is exactly what caused rolls to
                // feel like they ignored facing and used fixed axes.
                Vector3 inputDirection = new Vector3(_input.move.x, 0f, _input.move.y).normalized;
                float targetAngle = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg
                                     + _thirdPersonController.MovementReferenceYaw;

                _targetRotation = Quaternion.Euler(0f, targetAngle, 0f);
                _rollDirection = _targetRotation * Vector3.forward;
            }
            else
            {
                // No input (or no ThirdPersonController) — roll straight forward relative to the
                // character's current facing (toward the target while locked on).
                _rollDirection = transform.forward;
                _targetRotation = transform.rotation;
            }

            _isInvulnerable = false;

            if (_hasAnimator) _animator.SetTrigger(_rollTriggerHash);
        }

        private void TickRoll()
        {
            // Safety net only — normal termination happens via AE_IsRolling(). If that event is
            // missing or mistimed on a clip, this guarantees the character isn't stuck rolling forever.
            _rollTimer -= Time.deltaTime;
            if (_rollTimer <= 0f)
            {
                _isRolling = false;
                _isInvulnerable = false; // don't let a force-ended roll leave i-frames stuck on
                return;
            }

            // Smoothly rotate towards target
            transform.rotation = Quaternion.Lerp(transform.rotation, _targetRotation, Time.deltaTime * RotationSpeed);

            float speed = RollDistance / RollDuration;
            _controller.Move(_rollDirection * speed * Time.deltaTime);
        }

        /// <summary>
        /// Immediately ends the roll — called by Health when the character is staggered or dies. The
        /// reaction's Any State transition interrupts the Roll clip, so AE_IsRolling would never fire and
        /// the roll would keep moving the character (on top of the knockback) until MaxRollSafetyDuration.
        /// </summary>
        public void ForceCancelRoll()
        {
            if (!_isRolling) return;

            _isRolling = false;
            _isInvulnerable = false;

            // Same defensive reset as ForceCancelAttack: a Roll trigger set this frame but not yet
            // consumed would otherwise fire a phantom roll animation once the reaction ends.
            if (_hasAnimator) _animator.ResetTrigger(_rollTriggerHash);
        }

        /// <summary>
        /// Animation Event — place at the frame where the roll is visually complete.
        /// </summary>
        private void AE_IsRolling()
        {
            _isRolling = false;
            _isInvulnerable = false; // don't let i-frames outlive the roll if AE_IFrameEnd was missed
        }

        /// <summary>Animation Event — place wherever the dodge should actually start avoiding damage.</summary>
        private void AE_IFrameStart()
        {
            if (!_isRolling) return;
            _isInvulnerable = true;
        }

        /// <summary>Animation Event — place wherever the invulnerability window should close.
        /// Optional if you want i-frames to last the entire roll: AE_IsRolling() already clears
        /// this automatically when the roll ends, so an explicit end event is only needed if the
        /// window should close before the roll animation itself finishes.</summary>
        private void AE_IFrameEnd()
        {
            _isInvulnerable = false;
        }
    }
}