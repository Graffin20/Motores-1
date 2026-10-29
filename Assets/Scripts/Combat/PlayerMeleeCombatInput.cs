using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-driven combat input. Attack/HeavyAttack/Roll/SwitchTarget are single-frame pulses: 
    /// true only during the frame a press or flick is detected, then unconditionally cleared in LateUpdate.
    /// </summary>
    public class PlayerMeleeCombatInput : MonoBehaviour, IMeleeCombatInputSource
    {
        [Header("Block (polled directly)")]
        [Tooltip("Name of the Block action in your Input Actions asset, used to read its held state directly every frame.")]
        public string blockActionName = "Block";

        [Header("Target Switching Sensitivity")]
        [Tooltip("Threshold for stick flick or mouse delta to trigger a target switch.")]
        public float targetSwitchThreshold = 0.5f;

        public bool AttackRequested { get; private set; }
        public bool HeavyAttackRequested { get; private set; }
        public bool RollRequested { get; private set; }
        public bool BlockHeld { get; private set; }
        public bool LockOnRequested { get; private set; }
        public Vector2 TargetSwitchDirection { get; private set; }

        // Camera source for attack aiming. ThirdPersonController owns the camera reference; read it
        // lazily (not cached in Awake) since ThirdPersonController resolves it in its own Awake.
        private ThirdPersonController _thirdPersonController;

        /// <summary>Player attacks go where the camera looks, in free cam and lock-on alike.</summary>
        public bool TryGetAttackDirection(out Vector3 direction)
        {
            Transform cameraTransform = _thirdPersonController != null ? _thirdPersonController.CameraTransform : null;
            if (cameraTransform == null)
            {
                direction = default;
                return false;
            }

            direction = cameraTransform.forward;
            return true;
        }

        public void ConsumeAttackRequest() => AttackRequested = false;
        public void ConsumeHeavyAttackRequest() => HeavyAttackRequested = false;
        public void ConsumeRollRequest() => RollRequested = false;
        public void ConsumeLockOnRequest() => LockOnRequested = false;

#if ENABLE_INPUT_SYSTEM
        private PlayerInput _playerInput;
        private InputAction _blockAction;

        private void Awake()
        {
            _playerInput = GetComponent<PlayerInput>();
            _thirdPersonController = GetComponent<ThirdPersonController>(); // optional — null means no camera aiming

            if (_playerInput != null && _playerInput.actions != null)
            {
                _blockAction = _playerInput.actions.FindAction(blockActionName);
            }

            if (_blockAction == null)
            {
                Debug.LogWarning($"{nameof(PlayerMeleeCombatInput)} on '{name}' couldn't find a '{blockActionName}' " +
                                  "action to poll directly — Block will fall back to the OnBlock() message callback only.", this);
            }
        }

        // --- Standard Combat Callbacks ---
        public void OnAttack(InputValue value)
        {
            if (value.isPressed) AttackRequested = true;
        }

        public void OnHeavyAttack(InputValue value)
        {
            if (value.isPressed) HeavyAttackRequested = true;
        }

        public void OnRoll(InputValue value)
        {
            if (value.isPressed) RollRequested = true;
        }

        public void OnBlock(InputValue value)
        {
            BlockHeld = value.isPressed;
        }

        public void OnLockOn(InputValue value)
        {
            if (value.isPressed) LockOnRequested = true;
        }

        public void OnSwitchTarget(InputValue value)
        {
            var input = value.Get<Vector2>();
            if (Mathf.Abs(input.x) > targetSwitchThreshold)
            {
                TargetSwitchDirection = new Vector2(Mathf.Sign(input.x), 0f);
            }
        }
#endif

        private void Update()
        {
            if (GameManager.Instance && GameManager.Instance.levelComplete)
            {
                // Disable input when the level is complete
                AttackRequested = false;
                HeavyAttackRequested = false;
                RollRequested = false;
                BlockHeld = false;
                LockOnRequested = false;
                TargetSwitchDirection = Vector2.zero;
                return;
            }

#if ENABLE_INPUT_SYSTEM
            if (_blockAction != null) BlockHeld = _blockAction.IsPressed();
#endif
        }

        private void LateUpdate()
        {
            // Single-frame pulses cleared at the end of the frame
            AttackRequested = false;
            HeavyAttackRequested = false;
            RollRequested = false;
            LockOnRequested = false;
            TargetSwitchDirection = Vector2.zero;
        }
    }
}