using UnityEngine;

public interface IInteractable
{
    public void OnStartInteract();

    public void OnStopInteract();

    public void OnFocus();
    public void OnUnfocus();

    void OnAvailable();
    void OnUnavailable();
}
