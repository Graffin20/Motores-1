using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class MainMenu : MonoBehaviour
{
    [SerializeField] private string level1SceneName = "Level1";

    [SerializeField] private GameObject instructionsPanel;

    private void Start()
    {
        if (instructionsPanel != null)
            instructionsPanel.SetActive(false);
    }

    public void StartGame()
    {
        SceneManager.LoadScene(level1SceneName);
    }

    public void OpenInstructions()
    {
        if (instructionsPanel != null)
            instructionsPanel.SetActive(true);
    }

    public void CloseInstructions()
    {
        if (instructionsPanel != null)
            instructionsPanel.SetActive(false);
    }

    public void ExitGame()
    {
#if UNITY_EDITOR
        // Stops Play Mode when testing in the editor
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
