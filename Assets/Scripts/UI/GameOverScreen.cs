using System.Collections;
using StarterAssets;
using StarterAssets.Combat;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>
/// Shows the Game Over panel when the player dies and provides the panel's button actions
/// (hook Restart() and GoToMainMenu() to the Buttons' OnClick).
///
/// Put this on an object that is ALWAYS active (the Canvas or UIManager), not on the panel itself:
/// an inactive object never runs Start, so it could never subscribe to the death event that activates it.
///
/// Reacts to Health.Died (an event) instead of polling IsDead every frame, so it fires exactly once and
/// at the moment of death. The panel appears after ShowDelay so the death animation is seen first.
/// </summary>
public class GameOverScreen : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The Game Over panel (graphic + buttons). Leave it inactive in the scene; it is enabled on death.")]
    [SerializeField] private GameObject gameOverPanel;
    [Tooltip("The player's Health. Empty = found on the object tagged Player.")]
    [SerializeField] private Health playerHealth;
    [Tooltip("Optional: button selected when the panel opens, so it can be used with a gamepad/keyboard.")]
    [SerializeField] private GameObject firstSelectedButton;

    [Header("Behaviour")]
    [Tooltip("Seconds between the death and the panel appearing (real time, ignores pause).")]
    [SerializeField] private float showDelay = 2f;
    [Tooltip("Freeze the game (Time.timeScale = 0) while the panel is shown.")]
    [SerializeField] private bool pauseGameOnShow = false;
    [Tooltip("Scene loaded by the Main Menu button. Must be added to File > Build Profiles (Scene List).")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    private bool _shown;

    private void Start()
    {
        // Start, not Awake: the panel must be hidden regardless of how it was saved in the scene.
        if (gameOverPanel != null) gameOverPanel.SetActive(false);

        if (playerHealth == null)
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) playerHealth = player.GetComponentInParent<Health>() ?? player.GetComponentInChildren<Health>();
        }

        if (playerHealth == null)
        {
            Debug.LogWarning($"[{name}] GameOverScreen: no player Health found; the Game Over screen will never show.");
            return;
        }

        playerHealth.Died += OnPlayerDied;
    }

    private void OnDestroy()
    {
        if (playerHealth != null) playerHealth.Died -= OnPlayerDied;
    }

    private void OnPlayerDied(Vector3 killSourcePosition)
    {
        // Dying after reaching the exit shouldn't replace the victory screen.
        if (GameManager.Instance != null && GameManager.Instance.levelComplete) return;
        if (_shown) return;

        // Unlink the camera right away: the mouse shouldn't keep orbiting the camera around the body
        // during the delay. The cursor is only revealed with the panel, so it never floats over gameplay.
        UnlinkCameraFromMouse();
        StartCoroutine(ShowAfterDelay());
    }

    private IEnumerator ShowAfterDelay()
    {
        _shown = true;
        yield return new WaitForSecondsRealtime(showDelay);

        if (gameOverPanel != null) gameOverPanel.SetActive(true);
        if (pauseGameOnShow) Time.timeScale = 0f;

        RevealCursor();

        if (firstSelectedButton != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(firstSelectedButton);
    }

    /// <summary>
    /// ThirdPersonController turns the camera from StarterAssetsInputs.look, which OnLook only writes
    /// while cursorInputForLook is on. Turning that off (and zeroing the last value, which would
    /// otherwise keep being applied) stops any look device from moving the camera.
    /// </summary>
    private void UnlinkCameraFromMouse()
    {
        var input = FindPlayerInputs();
        if (input == null) return;
        input.cursorInputForLook = false;
        input.LookInput(Vector2.zero);
    }

    /// <summary>
    /// StarterAssetsInputs re-applies cursorLocked whenever the window regains focus (OnApplicationFocus),
    /// so setting only Cursor.lockState would re-lock the cursor after the first alt-tab or click on the
    /// Game view. Clearing the flag keeps it free.
    /// </summary>
    private void RevealCursor()
    {
        var input = FindPlayerInputs();
        if (input != null)
        {
            input.cursorLocked = false;
            input.cursorInputForLook = false;
            input.LookInput(Vector2.zero);
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    /// <summary>The player's StarterAssetsInputs, wherever it sits relative to Health (same object,
    /// parent or child), falling back to any in the scene (there is a single player).</summary>
    private StarterAssetsInputs FindPlayerInputs()
    {
        StarterAssetsInputs input = null;
        if (playerHealth != null)
        {
            input = playerHealth.GetComponentInParent<StarterAssetsInputs>();
            if (input == null) input = playerHealth.GetComponentInChildren<StarterAssetsInputs>();
        }
        if (input == null) input = FindAnyObjectByType<StarterAssetsInputs>();
        if (input == null) Debug.LogWarning($"[{name}] GameOverScreen: no StarterAssetsInputs found; the mouse may still move the camera.");
        return input;
    }

    // ------------------------------------------------------------------ Button actions

    /// <summary>Button: reloads the current level from the start.</summary>
    public void Restart()
    {
        PrepareForSceneChange();

        // The cursor state is global and survives the reload, and StarterAssetsInputs only applies its
        // cursorLocked in OnApplicationFocus (which a scene load doesn't trigger), so the level would
        // start with the free cursor from this menu. Gameplay wants it locked and hidden again.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    /// <summary>Button: loads the main menu scene.</summary>
    public void GoToMainMenu()
    {
        if (!Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
        {
            Debug.LogError($"[{name}] Scene '{mainMenuSceneName}' isn't in the build's Scene List (or the name is wrong).");
            return;
        }

        PrepareForSceneChange();
        SceneManager.LoadScene(mainMenuSceneName);
    }

    private void PrepareForSceneChange()
    {
        // timeScale is global and survives scene loads: a paused game over would load a frozen scene.
        Time.timeScale = 1f;

        // InventoryManager survives scene loads (DontDestroyOnLoad), so without this the level's
        // required pickup would still count as collected after a restart.
        if (InventoryManager.Instance != null) InventoryManager.Instance.SetPickedUpObject(false);
    }
}
