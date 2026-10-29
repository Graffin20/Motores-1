using UnityEngine;

/// <summary>
/// Generic pickupable item — a key, a consumable, etc. Implements IInteractable so it works with
/// InteractComponent's raycast-based detection: aim the camera at it, press Interact, it's picked
/// up. Doesn't require a trigger collider or being added to InteractComponent's proximity list at
/// all — the raycast finds it directly via its own collider.
///
/// Picking up is a one-shot action: OnStartInteract() does the pickup and the object is gone
/// immediately, unlike a lever or door that stays "interacting" until you interact again.
/// </summary>
[RequireComponent(typeof(Collider))]
public class Pickup : MonoBehaviour, IInteractable
{
    [Tooltip("Display name shown in UI prompts, e.g. \"Rusty Key\" or \"Health Potion\".")]
    public string ItemName = "Item";

    public void OnStartInteract()
    {
        // TODO: hook this up to your actual inventory system, e.g.:
        // InventoryManager.Instance.AddItem(ItemName);
        Debug.Log($"Picked up: {ItemName}");

        InventoryManager.Instance.SetPickedUpObject(true);
        UIManager.Instance.ToggleFocusTextPanel(false);
        Destroy(gameObject);
    }

    public void OnStopInteract()
    {
        // Pickup completes entirely within OnStartInteract() — there's no ongoing "interacting"
        // state to stop, and the object is destroyed immediately afterward, so this can't
        // realistically be called. Implemented as a no-op only to satisfy IInteractable.
    }

    public void OnFocus()
    {
        Debug.Log("Pickup.OnFocus firing"); // temporary
        UIManager.Instance.UpdatePanelText($"F - Pick up {ItemName}", UIManager.Instance.focusTextPanel);
        UIManager.Instance.ToggleFocusTextPanel(true);
    }

    public void OnUnfocus()
    {
        UIManager.Instance.ToggleFocusTextPanel(false);
    }

    public void OnAvailable()
    {
        // Only fires if this Pickup ALSO sits inside a trigger-based interaction volume (see
        // InteractComponent.OnTriggerEnter) — not required for raycast detection to work at all.
    }

    public void OnUnavailable()
    {
    }
}