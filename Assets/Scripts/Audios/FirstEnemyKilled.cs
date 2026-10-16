using UnityEngine;

public class FirstEnemyKilled : MonoBehaviour
{
    [SerializeField] private GameObject enemy;
    [SerializeField] private string enemyName = "Enemy";
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip voiceLine;

    [SerializeField] private float delay = 0f;

    private bool hasPlayed = false;
    private bool enemyFound = false;

    private void Start()
    {
        if (enemy == null)
            enemy = GameObject.Find(enemyName);

        enemyFound = enemy != null;

        if (!enemyFound)
            Debug.LogWarning($"[{name}] No se encontró ningún objeto llamado '{enemyName}'. Asignalo en el Inspector.");

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
    }

    private void Update()
    {
        if (hasPlayed || !enemyFound) return;

        
        bool enemyDead = enemy == null || !enemy.activeInHierarchy;

        if (enemyDead)
        {
            hasPlayed = true;
            Invoke(nameof(PlayVoice), delay);
        }
    }

    private void PlayVoice()
    {
        if (audioSource == null)
        {
            Debug.LogWarning($"[{name}] No hay AudioSource asignado.");
            return;
        }

        if (voiceLine != null)
            audioSource.clip = voiceLine;

        if (audioSource.clip == null)
        {
            Debug.LogWarning($"[{name}] No hay AudioClip asignado.");
            return;
        }

        audioSource.Play();
    }
}
