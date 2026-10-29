using UnityEngine;

public class InventoryManager : MonoBehaviour
{
    public static InventoryManager Instance { get; private set; }

    [SerializeField] private bool hasPickedUpObject = false;

    public bool HasPickedUpObject => hasPickedUpObject;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void SetPickedUpObject(bool value)
    {
        hasPickedUpObject = value;
    }
}