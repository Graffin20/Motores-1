using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-driven attack input. AttackRequested/HeavyAttackRequested are single-frame pulses:
    /// true only during the frame a press is detected, then unconditionally cleared in LateUpdate
    /// regardless of whether anything consumed them.
    ///
    /// This matters for MeleeCombatController's "no input buffering during cooldown" behavior —
    /// if the flag persisted until consumed (as a plain latch would), a press that happens to land
    /// during the cooldown window and never gets an explicit release callback could sit "true"
    /// indefinitely and fire the instant the cooldown ends, even if the player released the button
    /// long before. Clearing every frame in LateUpdate guarantees a press only ever counts on the
    /// exact frame it occurs.
    ///
    /// Setup: add "Attack" and "HeavyAttack" actions to your Input Actions asset and wire their
    /// callbacks (Send Messages behavior -> OnAttack / OnHeavyAttack) to this component, the same
    /// way OnJump/OnSprint are wired to StarterAssetsInputs.
    /// </summary>
    public class PlayerMeleeCombatInput : MonoBehaviour, IMeleeCombatInputSource
    {
        public bool AttackRequested { get; private set; }
        public bool HeavyAttackRequested { get; private set; }

        public void ConsumeAttackRequest() => AttackRequested = false;
        public void ConsumeHeavyAttackRequest() => HeavyAttackRequested = false;

#if ENABLE_INPUT_SYSTEM
        // Called via PlayerInput "Send Messages" / "Broadcast Messages" behavior.
        // Only the press edge sets the pulse — release events (isPressed == false) are ignored,
        // since LateUpdate is what clears it, not the release callback.
        public void OnAttack(InputValue value)
        {
            if (value.isPressed) AttackRequested = true;
        }

        public void OnHeavyAttack(InputValue value)
        {
            if (value.isPressed) HeavyAttackRequested = true;
        }
#endif

        // Fallback for old input manager / manual polling, if you're not using the new Input System.
        private void Update()
        {
#if !ENABLE_INPUT_SYSTEM
            if (Input.GetButtonDown("Fire1")) AttackRequested = true;
            if (Input.GetButtonDown("Fire2")) HeavyAttackRequested = true;
#endif
        }

        // Runs after every script's Update this frame, so anything that wanted to react to the
        // pulse (MeleeCombatController's Update) has already had its chance. Whatever's left
        // unconsumed is dropped rather than carried into the next frame.
        private void LateUpdate()
        {
            AttackRequested = false;
            HeavyAttackRequested = false;
        }
    }
}