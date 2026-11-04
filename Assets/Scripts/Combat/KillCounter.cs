using System;
using UnityEngine;

public class KillCounter : MonoBehaviour
{
    [Serializable]
    public class KillVoice
    {
        [Tooltip("Número de kill en el que suena este audio (1 = primera kill).")]
        public int killNumber = 1;

        [Tooltip("Línea de voz. Si la dejás vacía, usa el clip que ya tenga el AudioSource.")]
        public AudioClip voiceLine;
    }

    public static KillCounter Instance { get; private set; }

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;

    [Header("Líneas de voz por kill")]
    [SerializeField] private KillVoice[] killVoices = new KillVoice[] { new KillVoice { killNumber = 1 } };

    [Header("Estado (solo lectura)")]
    [SerializeField] private int kills = 0;

    public int Kills => kills;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public void AddKill()
    {
        kills++;

        foreach (KillVoice kv in killVoices)
        {
            if (kv.killNumber == kills)
            {
                PlayVoice(kv.voiceLine);
                break;
            }
        }
    }

    private void PlayVoice(AudioClip clip)
    {
        if (audioSource == null)
        {
            Debug.LogWarning($"[{name}] No hay AudioSource asignado en KillCounter.");
            return;
        }

        if (clip != null)
            audioSource.clip = clip;

        if (audioSource.clip == null)
        {
            Debug.LogWarning($"[{name}] No hay AudioClip asignado para la kill {kills}.");
            return;
        }

        audioSource.Play();
    }
}

