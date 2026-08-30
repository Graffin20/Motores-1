using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Entity-agnostic roll/dodge. Works for player or AI via IMeleeCombatInputSource, same
    /// pattern as MeleeCombatController. Moves the character using CharacterController.Move
    /// (this project uses CharacterController, not Rigidbody, so no physics forces are involved)
    /// covering RollDistance over RollDuration in the direction the character is currently facing.
    ///
    /// Costs stamina via StaminaSystem if one is present on this GameObject (optional — rolling
    /// is unrestricted if no StaminaSystem is attached).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class RollController : MonoBehaviour
    {
        [Header("Roll Movement")]
        public float RollDistance = 3f;
        public float RollDuration = 0.4f;

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
        private IMeleeCombatInputSource _inputSource;
        private Animator _animator;
        private bool _hasAnimator;
        private int _rollTriggerHash;

        private bool _isRolling;
        private float _rollTimer;
        private Vector3 _rollDirection;

        /// <summary>True while the roll is in progress — read this from ThirdPersonController.Move()
        /// (alongside MeleeCombatController.IsAttacking) to suppress normal locomotion during the roll,
        /// and from MeleeCombatController to prevent attacking mid-roll if that's the feel you want.</summary>
        public bool IsRolling => _isRolling;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _stamina = GetComponent<StaminaSystem>();       // optional — null is fine, roll just goes unrestricted
            _combat = GetComponent<MeleeCombatController>(); // optional — null means nothing blocks rolling on attack state
            _block = GetComponent<BlockController>();         // optional — null means blocking never prevents rolling
            _inputSource = GetComponent<IMeleeCombatInputSource>();
            _hasAnimator = TryGetComponent(out _animator);
            _rollTriggerHash = Animator.StringToHash(RollAnimationTrigger);

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
            if (_block != null && _block.IsBlocking) return;

            // Check stamina BEFORE asking to cancel the attack — if there isn't enough stamina to
            // actually roll, we must not abort a perfectly good attack for nothing. HasEnoughStamina
            // has no side effect, so it's safe to check ahead of committing to anything.
            if (_stamina != null && !_stamina.HasEnoughStamina(RollStaminaCost)) return;

            // Ask the combat state machine directly rather than inspecting the Animator's current
            // state by name — MeleeCombatController already knows whether it's mid-attack (and
            // whether the clip has opened a cancel window via AE_RollCancelOpen), and that stays
            // correct even if the Animator's state layout changes later. If an attack is in
            // progress and NOT cancelable right now, this returns false and the roll is refused.
            if (_combat != null && !_combat.RequestCancelForRoll()) return;

            // Now actually spend it — guaranteed to succeed given the check above (nothing else
            // runs between these two calls on Unity's single-threaded update).
            _stamina?.TrySpend(RollStaminaCost);

            _isRolling = true;
            _rollTimer = MaxRollSafetyDuration;
            _rollDirection = transform.forward;

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
                return;
            }

            float speed = RollDistance / RollDuration;

            // NOTE: this does not apply gravity/vertical velocity — ThirdPersonController owns
            // that value privately. If rolls need to respect falling (e.g. rolling off a ledge),
            // expose ThirdPersonController's vertical velocity via a public getter and blend it
            // in here, e.g.:
            //   Vector3 verticalMotion = Vector3.up * thirdPersonController.VerticalVelocity;
            //   _controller.Move((_rollDirection * speed + verticalMotion) * Time.deltaTime);
            _controller.Move(_rollDirection * speed * Time.deltaTime);
        }

        /// <summary>
        /// Animation Event — place at the frame where the roll is visually complete.
        /// </summary>
        private void AE_IsRolling()
        {
            _isRolling = false;
        }
    }
}