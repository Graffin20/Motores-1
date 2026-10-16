using UnityEngine;

public class VoiceOverTrigger : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip voiceLine;
    [SerializeField] private string playerTag = "Player";
    [SerializeField] private bool playOnlyOnce = true;
    [SerializeField] private bool avoidOverlap = true;

    private bool hasPlayed = false;

    private void OnCollisionEnter(Collision collision)
    {
        if (!collision.gameObject.CompareTag(playerTag)) return;
        if (playOnlyOnce && hasPlayed) return;
      
        if (audioSource == null)
            audioSource = collision.gameObject.GetComponentInChildren<AudioSource>();

        if (audioSource == null)
        {
            Debug.LogWarning($"[{name}] No hay AudioSource asignado ni encontrado en el Player.");
            return;
        }

        if (avoidOverlap && audioSource.isPlaying) return;

        if (voiceLine != null)
            audioSource.clip = voiceLine;

        if (audioSource.clip == null)
        {
            Debug.LogWarning($"[{name}] No hay AudioClip asignado.");
            return;
        }

        audioSource.Play();
        hasPlayed = true;
    }
}
