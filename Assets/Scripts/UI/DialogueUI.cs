using UnityEngine;

using TMPro;
using Unity.VisualScripting;
public class DialogueUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI speakerText;

    [SerializeField] private TextMeshProUGUI contentText;

    public void SetDialogueText(string speaker,string content) 
    { 
        speakerText.text = speaker;
        contentText.text = content;

    }

}

