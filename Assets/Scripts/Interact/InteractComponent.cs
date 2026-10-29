using System.Collections.Generic;
using UnityEngine;

public class InteractComponent : MonoBehaviour
{
    [Header("Capsule Detection")]
    [Tooltip("Max distance for the capsule cast used to detect interactable objects.")]
    public float InteractRange = 3f;
    [Tooltip("Radius of the capsule cast.")]
    public float CapsuleRadius = 0.5f;
    [Tooltip("Height of the capsule cast.")]
    public float CapsuleHeight = 1f;
    [Tooltip("Layers the capsule cast checks against.")]
    public LayerMask InteractableLayerMask = ~0;
    [Tooltip("Whether the cast should hit trigger colliders.")]
    public QueryTriggerInteraction CastTriggerInteraction = QueryTriggerInteraction.Collide;
    [Tooltip("The gameplay camera to cast from.")]
    [SerializeField] private Camera interactionCameraOverride;
    [Tooltip("Where the cast starts from — recommended to be near eye height.")]
    [SerializeField] private Transform rayOrigin;

    private Camera interactionCamera;
    private IInteractable currentInteractable;
    private IInteractable interactingInteractable;
    private readonly List<IInteractable> proximityInteractables = new();
    private IInteractable previousCastInteractable;

    private void Awake()
    {
        interactionCamera = ResolveCamera();
    }

    private Camera ResolveCamera()
    {
        return interactionCameraOverride != null ? interactionCameraOverride : Camera.main;
    }

    private Transform RayOrigin => rayOrigin != null ? rayOrigin : transform;

    private void Update()
    {
        if (interactionCamera == null)
        {
            interactionCamera = ResolveCamera();
            if (interactionCamera == null) return;
        }

        // Check if currentInteractable was destroyed
        if (currentInteractable != null && (currentInteractable as Component) == null)
        {
            currentInteractable = null;
        }

        // Check if interactingInteractable was destroyed
        if (interactingInteractable != null && (interactingInteractable as Component) == null)
        {
            interactingInteractable = null;
        }

        // Check if previousCastInteractable was destroyed
        if (previousCastInteractable != null && (previousCastInteractable as Component) == null)
        {
            previousCastInteractable = null;
        }

        // Try capsule cast first for broader detection
        IInteractable castHit = CapsuleCastForInteractable();
        
        // Handle focus/unfocus for raycast detection
        if (castHit != previousCastInteractable)
        {
            if (previousCastInteractable != null && previousCastInteractable is Pickup previousPickup)
            {
                previousPickup.OnUnfocus();
            }

            if (castHit != null && castHit is Pickup newPickup)
            {
                newPickup.OnFocus();
            }

            previousCastInteractable = castHit;
        }

        if (castHit != null)
        {
            SetCurrentInteractable(castHit);
        }
        else
        {
            // Fallback to proximity detection
            SetCurrentInteractable(GetBestProximityInteractable());
        }
    }

    private IInteractable CapsuleCastForInteractable()
    {
        if (interactionCamera == null) return null;

        Vector3 origin = RayOrigin.position;
        Vector3 direction = interactionCamera.transform.forward;
        
        // Calculate capsule endpoints
        Vector3 point1 = origin;
        Vector3 point2 = origin + interactionCamera.transform.up * (CapsuleHeight - CapsuleRadius * 2f);

        if (Physics.CapsuleCast(point1, point2, CapsuleRadius, direction, out RaycastHit hit, InteractRange, InteractableLayerMask, CastTriggerInteraction))
        {
            if (hit.collider.TryGetComponent<IInteractable>(out var interactable))
            {
                return interactable;
            }
        }

        return null;
    }

    private IInteractable GetBestProximityInteractable()
    {
        if (proximityInteractables.Count == 0) return null;

        return proximityInteractables[0];
    }

    private void SetCurrentInteractable(IInteractable interactable)
    {
        if (currentInteractable == interactable)
            return;

        currentInteractable = interactable;
    }

    public void Interact()
    {
        // Check if currentInteractable was destroyed
        if (currentInteractable != null && (currentInteractable as Component) == null)
        {
            currentInteractable = null;
        }

        // Refresh detection in case we're being called before Update() has run this frame
        if (currentInteractable == null)
        {
            IInteractable castHit = CapsuleCastForInteractable();
            if (castHit != null)
            {
                SetCurrentInteractable(castHit);
            }
            else
            {
                SetCurrentInteractable(GetBestProximityInteractable());
            }
        }

        if (currentInteractable == null)
        {
            return;
        }

        // Stop previous interaction if pressing interact again
        if (interactingInteractable != null)
        {
            interactingInteractable.OnStopInteract();
        }

        // Start new interaction
        interactingInteractable = currentInteractable;
        interactingInteractable.OnStartInteract();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            if (!proximityInteractables.Contains(interactable))
            {
                proximityInteractables.Add(interactable);
                
                // Call OnAvailable for trigger interactables
                interactable.OnAvailable();
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            proximityInteractables.Remove(interactable);
            
            // Call OnUnavailable for trigger interactables
            interactable.OnUnavailable();

            // If the interactable we lost is the current one, clear it
            if (currentInteractable == interactable)
            {
                currentInteractable = null;
            }

            // If we were interacting with this, stop the interaction
            if (interactingInteractable == interactable)
            {
                interactingInteractable.OnStopInteract();
                interactingInteractable = null;
            }
        }
    }

    private void OnDrawGizmos()
    {
        Camera cam = interactionCameraOverride != null ? interactionCameraOverride : Camera.main;
        Transform origin = rayOrigin != null ? rayOrigin : transform;

        if (cam == null || origin == null) return;

        Vector3 startPos = origin.position;
        Vector3 direction = cam.transform.forward;
        Vector3 endPos = startPos + direction * InteractRange;

        // Draw capsule cast visualization
        Vector3 point1 = startPos;
        Vector3 point2 = startPos + cam.transform.up * (CapsuleHeight - CapsuleRadius * 2f);

        Gizmos.color = Color.green;
        // Draw capsule at start
        DrawCapsuleGizmo(point1, point2, CapsuleRadius);
        // Draw capsule at end
        DrawCapsuleGizmo(point1 + direction * InteractRange, point2 + direction * InteractRange, CapsuleRadius);
        // Draw direction line
        Gizmos.DrawLine(startPos, endPos);
    }

    private void DrawCapsuleGizmo(Vector3 point1, Vector3 point2, float radius)
    {
        Gizmos.DrawWireSphere(point1, radius);
        Gizmos.DrawWireSphere(point2, radius);
    }
}