using UnityEngine;

// Parpadeo para luces de lámparas.
// Flame: llama de vela/brasas (variación suave y orgánica).
// FailingBulb: foco eléctrico que falla (cortes bruscos, estilo Silent Hill).
// Si se asigna un AudioSource, su volumen acompaña la intensidad de la luz.
[RequireComponent(typeof(Light))]
public class LampFlicker : MonoBehaviour
{
    public enum Mode { Flame, FailingBulb }
    public Mode mode = Mode.Flame;

    [Header("Intensidad")]
    public float baseIntensity = 1.2f;
    public float variation = 0.35f;

    [Header("Llama")]
    public float flameSpeed = 3f;

    [Header("Foco que falla")]
    public float minTimeBetweenFlickers = 2f;
    public float maxTimeBetweenFlickers = 6f;

    [Header("Audio (opcional)")]
    public AudioSource audioSource;
    public float baseVolume = 0.4f;

    Light lamp;
    float seed;
    float nextFlicker;
    float offUntil;

    void Awake()
    {
        lamp = GetComponent<Light>();
        seed = Random.value * 100f; // cada lámpara parpadea distinto
        ScheduleNextFlicker();
    }

    void Update()
    {
        if (mode == Mode.Flame)
        {
            float noise = Mathf.PerlinNoise(seed, Time.time * flameSpeed); // 0..1
            lamp.intensity = baseIntensity + (noise - 0.5f) * 2f * variation;
        }
        else
        {
            if (Time.time >= nextFlicker)
            {
                offUntil = Time.time + Random.Range(0.05f, 0.25f);
                ScheduleNextFlicker();
            }

            bool off = Time.time < offUntil;
            lamp.intensity = off
                ? baseIntensity * 0.1f
                : baseIntensity + Random.Range(-0.05f, 0.05f);
        }

        if (audioSource != null && baseIntensity > 0f)
        {
            float ratio = Mathf.Clamp01(lamp.intensity / baseIntensity);
            audioSource.volume = baseVolume * ratio;
        }
    }

    void ScheduleNextFlicker()
    {
        nextFlicker = Time.time + Random.Range(minTimeBetweenFlickers, maxTimeBetweenFlickers);
    }
}