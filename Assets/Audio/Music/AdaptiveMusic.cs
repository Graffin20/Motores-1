using UnityEngine;
using UnityEngine.Audio;

public class AdaptiveMusic : MonoBehaviour
{
    public static AdaptiveMusic Instance;

    [Header("Mismo orden")]
    [SerializeField] private AudioClip[] stems;
    [SerializeField] private AudioMixerGroup[] groups;

    [Header("Snapshots")]
    [SerializeField] private AudioMixerSnapshot exploration;
    [SerializeField] private AudioMixerSnapshot combat;
    [SerializeField] private AudioMixerSnapshot win;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        } else Destroy(this.gameObject);
        
        var start = AudioSettings.dspTime + 0.25;

        for (var i = 0; i < stems.Length; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.clip = stems[i];
            src.outputAudioMixerGroup = groups[i];
            src.loop = true;
            src.playOnAwake = false;
            src.PlayScheduled(start);
        }
        exploration.TransitionTo(0f);
    }

    public void Exploration() => exploration.TransitionTo(3f);
    public void Combat()     => combat.TransitionTo(0.5f);
    public void Win()   => win.TransitionTo(4f);
}