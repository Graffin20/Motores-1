using System.Collections.Generic;
using UnityEngine;

public class InteractComponent : MonoBehaviour

{
    [SerializeField] private Camera interactionCamera;

    private IInteractable currentInteractable;

    private readonly List<IInteractable> interactables = new();

    public void Interact()
    {
        currentInteractable?.OnStartInteract();
    }
    private void Update()
    {
        FindBestInteractable();
    }
    private void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            if (!interactables.Contains(interactable))
                interactables.Add(interactable);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            interactables.Remove(interactable);

            if (currentInteractable == interactable)
                currentInteractable = null;
        }
    }

    private void FindBestInteractable()
    {
        currentInteractable = null;

        float bestDot = -1f;

        Vector3 cameraForward = interactionCamera.transform.forward;

        foreach (var interactable in interactables)
        {
            if (interactable is not Component component)
                continue;

            Vector3 direction = (component.transform.position -
                                 interactionCamera.transform.position).normalized;

            float dot = Vector3.Dot(cameraForward, direction);

            if (dot >= bestDot)
            {
                bestDot = dot;
                currentInteractable = interactable;
            }
        }
    }


}
