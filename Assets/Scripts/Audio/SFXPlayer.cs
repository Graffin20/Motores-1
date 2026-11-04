using UnityEngine;

// Componente genérico para SFX.
//
// Formas de reproducir:
//   - PlayAt(i): reproduce el clip número i. Lo usa el swing: golpe 1 -> clip 0,
//                golpe 2 -> clip 1, golpe 3 -> clip 2, pesado -> clip 3.
//   - Play():    reproduce según el modo elegido en el Inspector:
//                  Secuencia -> clip 0, 1, 2, 3, 0, 1... (en orden)
//                  Aleatorio -> clip al azar, sin repetir el anterior
//
// En todos los casos aplica pitch y volumen aleatorios (la variación que pide la consigna).
// Con un solo clip y los rangos en 1 - 1, funciona como un SFX simple sin variación.
// Si es 2D o 3D y por qué grupo del Mixer sale se configura en el AudioSource asignado.
public class SFXPlayer : MonoBehaviour
{
    public enum PlayMode { Secuencia, Aleatorio }

    [Header("Audio")]
    [SerializeField] private AudioSource source;
    [SerializeField] private AudioClip[] clips;

    [Header("Orden de los clips (solo para Play)")]
    [SerializeField] private PlayMode mode = PlayMode.Secuencia;

    [Header("Variación")]
    [SerializeField] private Vector2 pitchRange = new Vector2(0.9f, 1.1f);
    [SerializeField] private Vector2 volumeRange = new Vector2(0.8f, 1f);

    private int lastIndex = -1;

    // Reproduce el siguiente clip según el modo
    public void Play()
    {
        if (clips == null || clips.Length == 0) return;

        int index;
        if (mode == PlayMode.Secuencia)
        {
            index = (lastIndex + 1) % clips.Length;          // 0, 1, 2, 3, 0...
        }
        else
        {
            index = Random.Range(0, clips.Length);
            if (clips.Length > 1 && index == lastIndex)
                index = (index + 1) % clips.Length;          // no repetir el anterior
        }

        PlayAt(index);
    }

    // Reproduce un clip concreto
    public void PlayAt(int index)
    {
        if (source == null || clips == null || clips.Length == 0) return;

        index = Mathf.Abs(index) % clips.Length;             // si el índice se pasa, vuelve a empezar
        lastIndex = index;

        source.pitch = Random.Range(pitchRange.x, pitchRange.y);
        source.PlayOneShot(clips[index], Random.Range(volumeRange.x, volumeRange.y));
    }

    // Hace que el próximo Play() en modo Secuencia arranque desde el primer clip
    public void ResetSequence()
    {
        lastIndex = -1;
    }
}
