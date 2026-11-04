using UnityEngine;

// Tipos de superficie que reconoce el sistema de pasos.
public enum SurfaceType { Pasto, Tierra, Piedra }

// Se agrega a los pisos de piedra (escaleras, pasillos) para indicar qué superficie son.
public class Surface : MonoBehaviour
{
    public SurfaceType type;
}
