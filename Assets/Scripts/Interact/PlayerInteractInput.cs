using StarterAssets.Combat;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Replaces the old Player.cs's interaction trigger (its movement half is now redundant —
/// ThirdPersonController already drives the CharacterController, so Player.cs should be removed
/// from the GameObject entirely rather than left running alongside it).
///
/// Wires an "Interact" input action to the teammate's InteractComponent using the same Send
/// Messages pattern as StarterAssetsInputs / PlayerMeleeCombatInput, instead of polling
/// Input.GetKeyDown(KeyCode.E) directly. InteractComponent itself is untouched — this only
/// changes how it gets triggered.
///
/// Setup: add an "Interact" action to your Input Actions asset (bind it to E, same as before,
/// plus a gamepad button if you want one) and it'll reach OnInteract() below automatically via
/// PlayerInput's Send Messages behavior, the same way OnAttack/OnRoll/OnBlock already work.
/// </summary>
[RequireComponent(typeof(InteractComponent))]
public class PlayerInteractInput : MonoBehaviour
{
    private InteractComponent _interactComponent;
    private bool _interactRequested;

    // Optional gating, same null-safe pattern used throughout the combat scripts — interacting
    // mid-swing/mid-roll/while staggered is usually not intended. Remove any of these checks (or
    // the whole ShouldBlockInteraction call) if you'd rather allow interaction regardless of state.
    private MeleeCombatController _combat;
    private RollController _roll;
    private Health _health;

    private void Awake()
    {
        _interactComponent = GetComponent<InteractComponent>();
        _combat = GetComponent<MeleeCombatController>(); // optional
        _roll = GetComponent<RollController>();           // optional
        _health = GetComponent<Health>();                  // optional
    }

#if ENABLE_INPUT_SYSTEM
    // Called via PlayerInput "Send Messages"/"Broadcast Messages" behavior.
    public void OnInteract(InputValue value)
    {
        if (value.isPressed) _interactRequested = true;
        Debug.Log($"OnInteract called, isPressed={value.isPressed}, _interactRequested={_interactRequested}");
    }
#endif

    private void Update()
    {
#if !ENABLE_INPUT_SYSTEM
    if (Input.GetKeyDown(KeyCode.E)) _interactRequested = true;
#endif

        if (!_interactRequested) return;
        _interactRequested = false;

        if (ShouldBlockInteraction())
        {
            Debug.Log("Interact blocked by ShouldBlockInteraction()"); // temporary
            return;
        }

        Debug.Log("Calling InteractComponent.Interact()"); // temporary
        _interactComponent.Interact();
    }

    private bool ShouldBlockInteraction()
    {
        if (_combat != null && _combat.IsAttacking) return true;
        if (_roll != null && _roll.IsRolling) return true;
        if (_health != null && (_health.IsHitStunned || _health.IsDead)) return true;
        return false;
    }
}