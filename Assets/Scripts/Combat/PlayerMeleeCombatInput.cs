using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-driven combat input. Attack/HeavyAttack/Roll are single-frame pulses: true only
    /// during the frame a press is detected, then unconditionally cleared in LateUpdate regardless
    /// of whether anything consumed them (see MeleeCombatController for why this matters — it
    /// prevents a leftover press from lingering across a cooldown window). Block is a genuine held
    /// state, read by directly polling the action every frame rather than trusting the Send
    /// Messages callback alone — some Interaction configurations (e.g. a "Press" interaction set
    /// to "Press Only") only invoke OnBlock() on press and never send a release message, which
    /// would otherwise leave BlockHeld stuck true forever.
    ///
    /// Setup: add "Attack", "HeavyAttack", "Roll", and "Block" actions to your Input Actions asset
    /// and wire their callbacks (Send Messages behavior -> OnAttack / OnHeavyAttack / OnRoll /
    /// OnBlock) to this component, the same way OnJump/OnSprint are wired to StarterAssetsInputs.
    /// Also double check the Block action itself has no Interaction added beyond a plain button —
    /// if OnBlock never fires on release, that's usually why.
    /// </summary>
    public class PlayerMeleeCombatInput : MonoBehaviour, IMeleeCombatInputSource
    {
        [Header("Block (polled directly, see class summary)")]
        [Tooltip("Name of the Block action in your Input Actions asset, used to read its held state directly every frame.")]
        public string BlockActionName = "Block";

        public bool AttackRequested { get; private set; }
        public bool HeavyAttackRequested { get; private set; }
        public bool RollRequested { get; private set; }
        public bool BlockHeld { get; private set; }

        public void ConsumeAttackRequest() => AttackRequested = false;
        public void ConsumeHeavyAttackRequest() => HeavyAttackRequested = false;
        public void ConsumeRollRequest() => RollRequested = false;

#if ENABLE_INPUT_SYSTEM
        private PlayerInput _playerInput;
        private InputAction _blockAction;

        private void Awake()
        {
            _playerInput = GetComponent<PlayerInput>();

            if (_playerInput != null && _playerInput.actions != null)
            {
                _blockAction = _playerInput.actions.FindAction(BlockActionName);
            }

            if (_blockAction == null)
            {
                Debug.LogWarning($"{nameof(PlayerMeleeCombatInput)} on '{name}' couldn't find a '{BlockActionName}' " +
                                  "action to poll directly — Block will fall back to the OnBlock() message callback only, " +
                                  "which can get stuck true if that action has a Press-Only interaction with no release message.", this);
            }
        }

        // Called via PlayerInput "Send Messages" / "Broadcast Messages" behavior.
        // Only the press edge sets the pulse flags — release events are ignored, since
        // LateUpdate is what clears them, not the release callback.
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

        // Kept as an immediate first response, but Update() below re-polls the actual action
        // state every frame and is the real source of truth — this can't get permanently stuck
        // even if the release message never arrives.
        public void OnBlock(InputValue value)
        {
            BlockHeld = value.isPressed;
        }
#endif

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (_blockAction != null) BlockHeld = _blockAction.IsPressed();
#else
            // Fallback for old input manager / manual polling, if you're not using the new Input System.
            if (Input.GetButtonDown("Fire1")) AttackRequested = true;
            if (Input.GetButtonDown("Fire2")) HeavyAttackRequested = true;
            if (Input.GetKeyDown(KeyCode.LeftShift)) RollRequested = true;
            BlockHeld = Input.GetMouseButton(1);
#endif
        }

        // Runs after every script's Update this frame. Whatever pulse is left unconsumed by then
        // is dropped rather than carried into the next frame. BlockHeld is intentionally excluded.
        private void LateUpdate()
        {
            AttackRequested = false;
            HeavyAttackRequested = false;
            RollRequested = false;
        }
    }
}