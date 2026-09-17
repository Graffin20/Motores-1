using UnityEngine;
using TMPro;

public class FrogDialogue : MonoBehaviour, IInteractable
{
    [Header("UI Reference")]
    public DialogueUI dialoguePrefab;
    private DialogueUI currentDialogue;

    [Header("Frog's message")]
    [TextArea(3, 5)]
    public string message = "¡Greetings, Traveler! Your salvation lies beyond a locked passage. Search [LUGAR], for only its key holds the power to set you free.";

    public void OnStartInteract()
    {
        if (currentDialogue == null)
        { 
        
            DialogueUI dialog = Instantiate(dialoguePrefab);

            currentDialogue = dialog;
            currentDialogue.SetDialogueText("Mr. Frog",message);
        }
        else
        {
            OnStopInteract();
        }
    }

    public void OnStopInteract()
    {
       if (currentDialogue)
       {

            Destroy(currentDialogue.gameObject);
            currentDialogue=null;

       }
    }

    public void OnFocus()
    {

    }

    public void OnUnfocus()
    {
        
    }

    public void OnAvailable()
    {
        
    }

    public void OnUnavailable()
    {
       
    }
}