using StarterAssets.Combat;
using UnityEngine;
using UnityEngine.UI;

public class UIBarsManager : MonoBehaviour
{
    [Header("UI Bars")]
    public Slider staminaBar;
    public Slider healthBar;
    [Header("System References")]
    public StaminaSystem playerStamina;
    public Health playerHealth;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        staminaBar.value = playerStamina.CurrentStamina / playerStamina.MaxStamina;
        healthBar.value = playerHealth.CurrentHealth / playerHealth.MaxHealth;
    }
}
