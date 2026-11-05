using UnityEngine;

// Sonidos de los ataques del jefe, disparados por Animation Events.
//
// IMPORTANTE: tiene que estar en el MISMO objeto que el Animator del jefe (la raíz "Boss"),
// porque Unity solo envía los Animation Events a los scripts de ese objeto.
//
// Un Animation Event por sonido. En cada clip, agregar un evento con el nombre de la función:
//   - Combo (golpes normales):  AE_SoundCombo     -> parámetro Int: 0 = golpe 1, 1 = golpe 2, 2 = golpe 3
//   - Lanzar fuego:             AE_SoundFire
//   - Pisotón (al caer):        AE_SoundStomp
//
// Cada sonido usa un SFXPlayer con su propio AudioSource (3D, salida al grupo Enemies del Mixer).
// Si un campo queda vacío, ese evento no hace nada.
public class BossSoundEvents : MonoBehaviour
{
    [Header("Combo de ataques normales")]
    [Tooltip("SFXPlayer con un clip por golpe del combo, en orden (golpe 1, 2, 3).")]
    [SerializeField] private SFXPlayer comboSfx;

    [Header("Lanzar fuego")]
    [SerializeField] private SFXPlayer fireSfx;

    [Header("Pisotón")]
    [SerializeField] private SFXPlayer stompSfx;

    // Animation Event del combo. El parámetro Int indica qué golpe es (0, 1 o 2).
    // Si el evento se deja con -1, usa el modo del SFXPlayer (Secuencia o Aleatorio).
    public void AE_SoundCombo(int hitIndex)
    {
        if (comboSfx == null) return;

        if (hitIndex >= 0) comboSfx.PlayAt(hitIndex);
        else comboSfx.Play();
    }

    // Animation Event del ataque de fuego (al empezar a lanzar el fuego).
    public void AE_SoundFire()
    {
        if (fireSfx != null) fireSfx.Play();
    }

    // Animation Event del pisotón (en el frame en que el jefe toca el suelo).
    public void AE_SoundStomp()
    {
        if (stompSfx != null) stompSfx.Play();
    }
}
