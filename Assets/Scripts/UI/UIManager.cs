using UnityEngine;

public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }

    public GameObject focusTextPanel;
    public GameObject tipsPanel;
    [SerializeField]
    private float tipsPanelDuration = 3f; // Duration in seconds

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
        }

        Instance = this;
        DontDestroyOnLoad(this.gameObject);
    }

    public void ToggleFocusTextPanel(bool active)
    {
        if (focusTextPanel != null)
        {
            focusTextPanel.SetActive(active);
        }
    }

    public void UpdatePanelText(string newText, GameObject panel)
    {
        if (panel != null)
        {   
            panel.GetComponentInChildren<TMPro.TextMeshProUGUI>().text = newText;
        }
    }

    public void ToggleTipsPanel(bool active)
    {
        if (tipsPanel != null)
        {
            if (active)
            {
                StopCoroutine(DeactivateTipsPanelAfterDelay());
                tipsPanel.SetActive(true);
                StartCoroutine(DeactivateTipsPanelAfterDelay());
            }
            else
            {
                tipsPanel.SetActive(false);
            }
        }
    }

    private System.Collections.IEnumerator DeactivateTipsPanelAfterDelay()
    {
        yield return new WaitForSeconds(tipsPanelDuration);
        if (tipsPanel != null)
        {
            tipsPanel.SetActive(false);
        }
    }
}
