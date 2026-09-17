using System.Collections.Generic;
using UnityEngine;

public class InteractComponent : MonoBehaviour
{
    private Camera interactionCamera;

    private IInteractable currentInteractable;
    private IInteractable interactingInteractable;

    private readonly List<IInteractable> interactables = new();

    private void Awake()
    {
        interactionCamera = Camera.main;
    }

    private void Update()
    {
        FindBestInteractable();
    }

    public void Interact()
    {
        if (currentInteractable == null)
            return;

     
        if (interactingInteractable == currentInteractable)
        {
            StopInteract();
            return;
        }

        
        if (interactingInteractable != null)
        {
            StopInteract();
        }

        interactingInteractable = currentInteractable;
        interactingInteractable.OnStartInteract();
    }

    private void StopInteract()
    {
        if (interactingInteractable == null)
            return;

        interactingInteractable.OnStopInteract();
        interactingInteractable = null;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            if (!interactables.Contains(interactable))
            {
                interactables.Add(interactable);
                interactable.OnAvailable();
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.TryGetComponent<IInteractable>(out var interactable))
        {
            if (!interactables.Remove(interactable))
                return;

            interactable.OnUnavailable();

            if (currentInteractable == interactable)
            {
                Unfocus(interactable);
                currentInteractable = null;
            }

            if (interactingInteractable == interactable)
            {
                StopInteract();
            }
        }
    }

    private void FindBestInteractable()
    {
        IInteractable bestInteractable = null;
        float bestDot = -1f;

        Vector3 cameraForward = interactionCamera.transform.forward;

        foreach (var interactable in interactables)
        {
            if (interactable is not Component component)
                continue;

            Vector3 direction =
                (component.transform.position -
                 interactionCamera.transform.position).normalized;

            float dot = Vector3.Dot(cameraForward, direction);

            if (dot >= bestDot)
            {
                bestDot = dot;
                bestInteractable = interactable;
            }
        }

        if (bestInteractable == currentInteractable)
            return;

        
        if (currentInteractable != null)
        {
            Unfocus(currentInteractable);
        }

        currentInteractable = bestInteractable;

        
        if (currentInteractable != null)
        {
            Focus(currentInteractable);
        }
    }

    private void Focus(IInteractable interactable)
    {
        interactable.OnFocus();
    }

    private void Unfocus(IInteractable interactable)
    {
        interactable.OnUnfocus();
    }
}

