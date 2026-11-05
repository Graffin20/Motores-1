using UnityEngine;

public class SpawnAudioTrigger : MonoBehaviour
{
        [Tooltip("Only play the sound the first time the player enters.")]
        [SerializeField] private bool playOnce = true;

        [Header("Audio")]
        [SerializeField] private AudioSource audioSource;
        private bool hasPlayed = false;

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            audioSource.playOnAwake = false;

            // Make sure the collider acts as a trigger
            GetComponent<BoxCollider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag("Player")) return;
            if (playOnce && hasPlayed) return;

            audioSource.Play();
            hasPlayed = true;
        }
 }

