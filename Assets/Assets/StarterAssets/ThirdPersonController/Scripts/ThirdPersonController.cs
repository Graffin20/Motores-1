using StarterAssets.Combat;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM 
using UnityEngine.InputSystem;
#endif

/* Note: animations are called via the controller for both the character and capsule using animator null checks
 */

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM 
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour
    {
        /// <summary>
        /// How locomotion maps input to rotation/translation. Mirrors MeleeCombatController's AttackPhase:
        /// one explicit mode instead of lock-on checks scattered through Move().
        /// - Free: input is camera-relative, the character turns to face where it moves.
        /// - LockedStrafe: input is relative to the locked target (up = toward it, left/right = circle it),
        ///   the character always faces the target.
        /// </summary>
        public enum MovementMode
        {
            Free,
            LockedStrafe
        }

        [Header("Player")]
        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;

        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;

        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;

        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        public AudioSource AudioFootsteps;
        public AudioSource LandingAudio;
        public AudioSource AudioFoley;
        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;

        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Lock-On Strafe")]
        [Tooltip("Move speed (m/s) while locked on.")]
        public float StrafeSpeed = 2.0f;

        [Tooltip("Multiplies StrafeSpeed while sprinting locked on. Separate from SprintSpeed so strafing can stay slower than a free sprint.")]
        public float StrafeSprintMultiplier = 1.5f;

        [Tooltip("How fast the character turns to keep facing the locked target")]
        [Range(0.0f, 0.3f)]
        public float StrafeRotationSmoothTime = 0.05f;

        [Tooltip("Damping time for the StrafeX/StrafeY Animator parameters, so a 2D strafe blend tree doesn't jitter on direction changes")]
        public float StrafeAnimationDampTime = 0.1f;

        [Tooltip("Damping time for the StrafeBlend Animator parameter (0 = free locomotion tree, 1 = strafe tree), i.e. how fast the locomotion blend crossfades when locking on/off")]
        public float StrafeBlendDampTime = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;

        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;

        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;

        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("The gameplay camera (the one with the CinemachineBrain). Assign explicitly: with several MainCamera-tagged cameras the tag lookup can resolve to the wrong one. Movement, rolls and lock-on all read the camera from here.")]
        public Camera GameplayCamera;

        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;

        [Tooltip("How smoothly the camera pans to face the locked target")]
        public float LockOnCameraSmoothSpeed = 10.0f;

        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;

        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;

        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        private Health _health;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;
        private int _animIDStrafeX;
        private int _animIDStrafeY;
        private int _animIDIsStrafing;
        private int _animIDStrafeBlend;

        // The strafe parameters are optional until the strafe animations exist: while they're missing,
        // the existing Speed-driven Idle/Walk/Run blend plays in every mode.
        private bool _hasStrafeXParam;
        private bool _hasStrafeYParam;
        private bool _hasIsStrafingParam;
        private bool _hasStrafeBlendParam;

        // combat
        private MeleeCombatController _combat;
        private RollController _roll;
        private BlockController _block;
        private LockOnController _lockOnController;

        // True if movement was locked last frame. See Move(): the first free frame must not read
        // _controller.velocity, which then reflects whatever ELSE moved us (knockback, roll, lunge).
        private bool _wasMovementLocked;

#if ENABLE_INPUT_SYSTEM 
        private PlayerInput _playerInput;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private StarterAssetsInputs _input;
        private GameObject _mainCamera;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;

        /// <summary>Single source for "which camera is the player looking through" — RollController and
        /// LockOnController read this instead of each doing their own lookup.</summary>
        public Transform CameraTransform => _mainCamera != null ? _mainCamera.transform : null;

        /// <summary>Current locomotion mode, resolved once per frame at the start of Move().</summary>
        public MovementMode CurrentMovementMode { get; private set; } = MovementMode.Free;

        /// <summary>
        /// World yaw that "forward" input maps to: toward the locked target in LockedStrafe, the camera's
        /// yaw in Free. Single source for input-relative directions, so RollController rolls in the same
        /// frame the character moves in (target-relative while locked on).
        /// </summary>
        public float MovementReferenceYaw
        {
            get
            {
                if (ResolveMovementMode() == MovementMode.LockedStrafe)
                {
                    Vector3 toTarget = _lockOnController.CurrentTargetTransform.position - transform.position;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude > 0.0001f)
                    {
                        return Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
                    }
                    return transform.eulerAngles.y; // standing on the target's pivot — no meaningful direction
                }

                return _mainCamera != null ? _mainCamera.transform.eulerAngles.y : transform.eulerAngles.y;
            }
        }

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput.currentControlScheme == "KeyboardMouse";
#else
             return false;
#endif
            }
        }


        private void Awake()
        {
            // get a reference to our main camera
            if (GameplayCamera != null)
            {
                _mainCamera = GameplayCamera.gameObject;
            }
            else
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
                Debug.LogWarning($"{nameof(ThirdPersonController)} on '{name}' has no GameplayCamera assigned — " +
                                 $"falling back to the MainCamera tag ('{(_mainCamera != null ? _mainCamera.name : "none found")}'), " +
                                 "which can pick the wrong camera.", this);
            }

            _health = GetComponent<Health>();
        }

        private void Start()
        {
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<StarterAssetsInputs>();
#if ENABLE_INPUT_SYSTEM 
            _playerInput = GetComponent<PlayerInput>();
#else
          Debug.LogError( "Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

            _combat = GetComponent<MeleeCombatController>();
            _roll = GetComponent<RollController>();
            _block = GetComponent<BlockController>();
            _lockOnController = GetComponent<LockOnController>();

            AssignAnimationIDs();

            if (_hasAnimator)
            {
                _hasStrafeXParam = _animator.HasParameter(_animIDStrafeX);
                _hasStrafeYParam = _animator.HasParameter(_animIDStrafeY);
                _hasIsStrafingParam = _animator.HasParameter(_animIDIsStrafing);
                _hasStrafeBlendParam = _animator.HasParameter(_animIDStrafeBlend);
            }

            // reset our timeouts on start
            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;
        }

        private void Update()
        {
            _hasAnimator = TryGetComponent(out _animator);

            JumpAndGravity();
            GroundedCheck();
            Move();
        }

        private void LateUpdate()
        {
            CameraRotation();
        }

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
            _animIDStrafeX = Animator.StringToHash("StrafeX");
            _animIDStrafeY = Animator.StringToHash("StrafeY");
            _animIDIsStrafing = Animator.StringToHash("IsStrafing");
            _animIDStrafeBlend = Animator.StringToHash("StrafeBlend");
        }

        private MovementMode ResolveMovementMode()
        {
            bool lockedOn = _lockOnController != null && _lockOnController.IsLockedOn &&
                            _lockOnController.CurrentTargetTransform != null;
            return lockedOn ? MovementMode.LockedStrafe : MovementMode.Free;
        }

        private bool ShouldLockMovement()
        {
            if (_health != null && (_health.IsHitStunned || _health.IsDead)) return true;
            // A knockback without stagger (e.g. a blocked hit) must still lock locomotion: otherwise
            // Move() keeps walking toward the facing direction (= toward the attacker) and cancels the push.
            if (_health != null && _health.IsBeingKnockedBack) return true;
            if (_combat != null && _combat.IsAttacking) return true;
            if (_roll != null && _roll.IsRolling) return true;
            if (_block != null && _block.IsParrying) return true;
            if (GameManager.Instance != null && GameManager.Instance.levelComplete) return true;
            return false;
        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers, QueryTriggerInteraction.Ignore);

            if (_hasAnimator)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        private void CameraRotation()
        {
            // If we are locked on, smoothly interpolate yaw/pitch toward the target position
            if (_lockOnController != null && _lockOnController.IsLockedOn)
            {
                Transform targetTransform = _lockOnController.CurrentTargetTransform;
                if (targetTransform != null)
                {
                    Vector3 targetDirection = (targetTransform.position + Vector3.up * 1f) - CinemachineCameraTarget.transform.position;
                    Quaternion lookRotation = Quaternion.LookRotation(targetDirection);
                    Vector3 euler = lookRotation.eulerAngles;

                    float targetYaw = euler.y;
                    float targetPitch = euler.x;
                    if (targetPitch > 180f) targetPitch -= 360f;

                    // Smoothly glide towards the target angles instead of snapping instantly
                    _cinemachineTargetYaw = Mathf.LerpAngle(_cinemachineTargetYaw, targetYaw, Time.deltaTime * LockOnCameraSmoothSpeed);
                    _cinemachineTargetPitch = Mathf.Lerp(_cinemachineTargetPitch, targetPitch, Time.deltaTime * LockOnCameraSmoothSpeed);
                }
            }
            else
            {
                // Normal free-look mouse rotation when NOT locked on
                if (_input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
                {
                    float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;

                    _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
                    _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
                }
            }

            // clamp our rotations so our values are limited 360 degrees
            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            // Cinemachine will follow this target
            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride,
                _cinemachineTargetYaw, 0.0f);
        }

        private void Move()
        {
            CurrentMovementMode = ResolveMovementMode();

            if (ShouldLockMovement())
            {
                _controller.Move(new Vector3(0f, _verticalVelocity, 0f) * Time.deltaTime);
                _wasMovementLocked = true;
                return;
            }

            float targetSpeed = CurrentMovementMode == MovementMode.LockedStrafe
                ? (_input.sprint ? StrafeSpeed * StrafeSprintMultiplier : StrafeSpeed)
                : (_input.sprint ? SprintSpeed : MoveSpeed);

            if (_input.move == Vector2.zero) targetSpeed = 0.0f;

            // CharacterController.velocity only reflects the LAST Move() call of the frame, so right after a
            // lockout it can be the knockback/roll/lunge speed (or our gravity-only move), depending on script
            // order. Feeding that into the acceleration below made the character lurch along its facing on the
            // first free frame. Start from rest instead, so it's deterministic.
            float currentHorizontalSpeed = _wasMovementLocked
                ? 0f
                : new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;
            _wasMovementLocked = false;

            float speedOffset = 0.1f;
            float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;

            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude, Time.deltaTime * SpeedChangeRate);
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;
            float referenceYaw = MovementReferenceYaw;

            // _targetRotation is the MOVE direction in both modes. With no input it keeps the last one, so
            // deceleration continues along the direction we were actually moving.
            if (_input.move != Vector2.zero)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg + referenceYaw;
            }

            switch (CurrentMovementMode)
            {
                case MovementMode.Free:
                    // Face where we move (only while there is input, as in the original Starter Assets).
                    if (_input.move != Vector2.zero) RotateTowards(_targetRotation, RotationSmoothTime);
                    break;

                case MovementMode.LockedStrafe:
                    // Always face the target, moving or not. Recomputing the target-relative direction every
                    // frame is what turns sideways input into circling around the target.
                    RotateTowards(referenceYaw, StrafeRotationSmoothTime);
                    break;
            }

            Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

            _controller.Move(targetDirection.normalized * (_speed * Time.deltaTime) +
                             new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            UpdateLocomotionAnimator(targetDirection, inputMagnitude);
        }

        private void RotateTowards(float targetYaw, float smoothTime)
        {
            float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref _rotationVelocity, smoothTime);
            transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
        }

        /// <summary>
        /// Speed/MotionSpeed keep driving the existing Idle/Walk/Run blend, which is the fallback for every
        /// mode until strafe animations exist. If the Animator defines them, also feeds:
        /// - IsStrafing (bool): true while locked on, to switch to a separate strafe state.
        /// - StrafeBlend (float 0..1, damped): 1 while locked on — weight of the strafe tree when free and
        ///   strafe trees are nested in ONE locomotion state (see Tools > Animator > Merge Strafe State Into
        ///   Locomotion), which keeps every transition wired to that single state.
        /// - StrafeX / StrafeY (float, m/s, same scale as Speed): the move velocity in the character's local
        ///   space. +X = right, -X = left, +Y = forward, -Y = backward — the axes of a 2D blend tree.
        /// </summary>
        private void UpdateLocomotionAnimator(Vector3 moveDirection, float inputMagnitude)
        {
            if (!_hasAnimator) return;

            _animator.SetFloat(_animIDSpeed, _animationBlend);
            _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);

            bool strafing = CurrentMovementMode == MovementMode.LockedStrafe;

            if (_hasIsStrafingParam)
            {
                _animator.SetBool(_animIDIsStrafing, strafing);
            }

            if (_hasStrafeBlendParam)
            {
                _animator.SetFloat(_animIDStrafeBlend, strafing ? 1f : 0f, StrafeBlendDampTime, Time.deltaTime);
            }

            if (_hasStrafeXParam || _hasStrafeYParam)
            {
                Vector3 localVelocity = transform.InverseTransformDirection(moveDirection) * _animationBlend;
                if (_hasStrafeXParam) _animator.SetFloat(_animIDStrafeX, localVelocity.x, StrafeAnimationDampTime, Time.deltaTime);
                if (_hasStrafeYParam) _animator.SetFloat(_animIDStrafeY, localVelocity.z, StrafeAnimationDampTime, Time.deltaTime);
            }
        }

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                _fallTimeoutDelta = FallTimeout;

                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                if (_verticalVelocity < 0.0f)
                {
                    _verticalVelocity = -2f;
                }

                if (_input.jump && _jumpTimeoutDelta <= 0.0f && !ShouldLockMovement())
                {
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);

                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDJump, true);
                    }
                }

                if (_jumpTimeoutDelta >= 0.0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                _jumpTimeoutDelta = JumpTimeout;

                if (_fallTimeoutDelta >= 0.0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDFreeFall, true);
                    }
                }

                _input.jump = false;
            }

            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += Gravity * Time.deltaTime;
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            if (Grounded) Gizmos.color = transparentGreen;
            else Gizmos.color = transparentRed;

            Gizmos.DrawSphere(
                new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z),
                GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                if (AudioFootsteps != null)
                    AudioFootsteps.Play();
                if (AudioFoley != null)
                    AudioFoley.Play();
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                if (LandingAudio != null)
                    LandingAudio.Play();
            }
        }
    }
}