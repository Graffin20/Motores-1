using System.Collections.Generic;
using UnityEngine;

public class InteractComponent : MonoBehaviour
{
    [Header("Raycast Detection")]
    [Tooltip("Max distance for the camera-forward raycast used to detect interactable objects.")]
    public float InteractRange = 3f;
    [Tooltip("Layers the raycast checks against.")]
    public LayerMask InteractableLayerMask = ~0;
    [Tooltip("Whether the raycast should hit trigger colliders.")]
    public QueryTriggerInteraction RaycastTriggerInteraction = QueryTriggerInteraction.Collide;
    [Tooltip("The gameplay camera to raycast from.")]
    [SerializeField] private Camera interactionCameraOverride;
    [Tooltip("Where the raycast starts from — recommended to be near eye height.")]
    [SerializeField] private Transform rayOrigin;

    private Camera interactionCamera;
    private IInteractable currentInteractable;
    private IInteractable interactingInteractable;
    private readonly List<IInteractable> proximityInteractables = new();

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

        // Try raycast first for precise detection
        IInteractable rayHit = RaycastForInteractable();
        if (rayHit != null)
        {
            SetCurrentInteractable(rayHit);
        }
        else
        {
            // Fallback to proximity detection
            SetCurrentInteractable(GetBestProximityInteractable());
        }
    }

    private IInteractable RaycastForInteractable()
    {
        if (interactionCamera == null) return null;

        Vector3 origin = RayOrigin.position;
        Vector3 direction = interactionCamera.transform.forward;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, InteractRange, InteractableLayerMask, RaycastTriggerInteraction))
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
            IInteractable rayHit = RaycastForInteractable();
            if (rayHit != null)
            {
                SetCurrentInteractable(rayHit);
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
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            proximityInteractables.Remove(interactable);

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

        Gizmos.color = Color.green;
        Gizmos.DrawLine(startPos, endPos);
        Gizmos.DrawWireSphere(endPos, 0.1f);
    }
}