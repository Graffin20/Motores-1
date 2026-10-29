using UnityEngine;

public class TipsPanelToggle : MonoBehaviour
{
    private bool tipsDeployed = false;
    public string tipMessage = "Combat controls\r\nLMB: LIght Attack\r\nRMB: Heavy Attack\r\nSpacebar: Roll\r\nE: Block";
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player") && !tipsDeployed)
        {
            UIManager.Instance.UpdatePanelText(tipMessage, UIManager.Instance.tipsPanel);
            UIManager.Instance.ToggleTipsPanel(true);
            tipsDeployed = true;
        }
    }

}
