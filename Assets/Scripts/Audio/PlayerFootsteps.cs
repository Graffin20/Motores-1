using UnityEngine;

// Reproduce los pasos del jugador según la superficie que pisa.
// Lo llama ThirdPersonController.OnFootstep (Animation Event de caminar/correr).
public class PlayerFootsteps : MonoBehaviour
{
    [System.Serializable]
    public class SurfaceClips
    {
        public SurfaceType type;
        public AudioClip[] clips;
        [Range(0f, 2f)] public float volume = 1f;          // ajuste fino por superficie
        [Tooltip("Texturas del Terrain que cuentan como esta superficie")]
        public TerrainLayer[] terrainLayers;
    }

    [Header("Audio")]
    [SerializeField] private AudioSource source;            // 2D, Output = SFX/Player
    [SerializeField] private SurfaceClips[] surfaces;
    [SerializeField] private Vector2 pitchRange = new Vector2(0.9f, 1.1f);
    [SerializeField] private Vector2 volumeRange = new Vector2(0.8f, 1f);

    [Header("Correr")]
    [SerializeField] private float runVolumeMultiplier = 1.3f;
    [SerializeField] private float runPitchOffset = 0.05f;

    [Header("Detección de superficie")]
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float rayLength = 2f;
    [SerializeField] private SurfaceType defaultSurface = SurfaceType.Tierra;

    [Header("Otros")]
    [SerializeField] private float minInterval = 0.2f;      // evita pasos duplicados
    [SerializeField] private bool debugSurface = false;     // muestra la superficie en la consola

    private float lastStepTime;
    private int lastIndex = -1;

    public void Footstep(bool running = false)
    {
        if (Time.time - lastStepTime < minInterval) return;
        lastStepTime = Time.time;

        SurfaceType type = DetectSurface();
        if (debugSurface) Debug.Log("Superficie: " + type);

        SurfaceClips surface = GetSurface(type);
        if (surface == null || surface.clips == null || surface.clips.Length == 0) return;

        // Clip aleatorio, sin repetir el anterior
        int index = Random.Range(0, surface.clips.Length);
        if (surface.clips.Length > 1 && index == lastIndex)
            index = (index + 1) % surface.clips.Length;
        lastIndex = index;

        float pitch = Random.Range(pitchRange.x, pitchRange.y);
        float volume = Random.Range(volumeRange.x, volumeRange.y) * surface.volume;

        if (running)
        {
            pitch += runPitchOffset;
            volume *= runVolumeMultiplier;
        }

        source.pitch = pitch;
        source.PlayOneShot(surface.clips[index], volume);
    }

    private SurfaceType DetectSurface()
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, rayLength,
                             groundMask, QueryTriggerInteraction.Ignore))
            return defaultSurface;

        // 1) Prefab marcado con Surface (piedra, ladrillo)
        Surface surface = hit.collider.GetComponentInParent<Surface>();
        if (surface != null) return surface.type;

        // 2) Terrain: buscar la textura pintada dominante
        Terrain terrain = hit.collider.GetComponent<Terrain>();
        if (terrain != null)
        {
            TerrainLayer layer = GetDominantLayer(terrain, hit.point);
            if (layer != null)
            {
                foreach (SurfaceClips s in surfaces)
                    if (s.terrainLayers != null && System.Array.IndexOf(s.terrainLayers, layer) >= 0)
                        return s.type;
            }
        }

        return defaultSurface;
    }

    private TerrainLayer GetDominantLayer(Terrain terrain, Vector3 worldPos)
    {
        TerrainData data = terrain.terrainData;
        if (data.alphamapLayers == 0) return null;

        Vector3 local = worldPos - terrain.transform.position;
        int x = Mathf.Clamp(Mathf.FloorToInt(local.x / data.size.x * data.alphamapWidth), 0, data.alphamapWidth - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt(local.z / data.size.z * data.alphamapHeight), 0, data.alphamapHeight - 1);

        float[,,] mix = data.GetAlphamaps(x, z, 1, 1);
        int best = 0;
        float max = 0f;
        for (int i = 0; i < mix.GetLength(2); i++)
        {
            if (mix[0, 0, i] > max)
            {
                max = mix[0, 0, i];
                best = i;
            }
        }

        TerrainLayer[] layers = data.terrainLayers;
        return best < layers.Length ? layers[best] : null;
    }

    private SurfaceClips GetSurface(SurfaceType type)
    {
        foreach (SurfaceClips s in surfaces)
            if (s.type == type) return s;
        return null;
    }
}
