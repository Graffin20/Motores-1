using StarterAssets.Combat;
using UnityEngine;
using UnityEngine.UI;

public class UIBarsManager : MonoBehaviour
{
    public Slider staminaBar;
    public StaminaSystem playerStamina;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        staminaBar.value = playerStamina.CurrentStamina / playerStamina.MaxStamina;
    }
}
