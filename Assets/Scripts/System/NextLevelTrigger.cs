using UnityEngine;

public class NextLevelTrigger : MonoBehaviour
{
    public GameObject victoryScreen;
    private void Start()
    {
        if (InventoryManager.Instance == null)
        {
            Debug.LogError("InventoryManager instance is not found in the scene.");
        }
    }
    private void OnTriggerEnter(Collider other)
    {
        if (InventoryManager.Instance.HasPickedUpObject && other.CompareTag("Player"))
        {
            NextLevel();
        }
    }

    private void NextLevel()
    {
        victoryScreen.SetActive(true);
    }
}