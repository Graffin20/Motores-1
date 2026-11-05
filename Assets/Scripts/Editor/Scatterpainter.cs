// Delicarium - Scatter Painter (reemplazo simple de Polybrush)
// Pinta prefabs (musgo, enredaderas, hojas) sobre cualquier superficie con collider:
// paredes de ProBuilder, setos, rocas, escaleras, terreno.
//
// USO:
// 1. Guardar en Assets/Editor/
// 2. Menu: Tools > Delicarium > Scatter Painter
// 3. Cargar los prefabs en la lista, activar "Pintar" y:
//    - Clic izquierdo / arrastrar en la vista Scene  -> coloca objetos
//    - Shift + clic / arrastrar                      -> borra objetos dentro del pincel
//    - Ctrl + rueda del mouse                        -> cambia el tamano del pincel
//    - Esc                                           -> deja de pintar
// Todo se puede deshacer con Ctrl+Z.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class ScatterPainter : EditorWindow
{
    [SerializeField] private List<GameObject> prefabs = new List<GameObject>();
    [SerializeField] private Transform contenedor;

    [SerializeField] private float radioPincel = 1f;
    [SerializeField] private int cantidadPorClic = 5;
    [SerializeField] private float separacionMinima = 0.25f;

    [SerializeField] private float escalaMin = 0.7f;
    [SerializeField] private float escalaMax = 1.3f;
    [SerializeField] private bool alinearANormal = true;
    [SerializeField] private bool rotacionAleatoria = true;
    [SerializeField] private float hundimiento = 0.02f;   // mete el objeto un poco en la superficie
    [SerializeField] private LayerMask capas = ~0;

    private bool pintando;
    private SerializedObject so;
    private Vector2 scroll;
    private readonly List<Vector3> puntosRecientes = new List<Vector3>();

    [MenuItem("Tools/Delicarium/Scatter Painter")]
    private static void Abrir() => GetWindow<ScatterPainter>("Scatter Painter");

    private void OnEnable()
    {
        so = new SerializedObject(this);
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    private void OnGUI()
    {
        so.Update();
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Objetos a pintar", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("prefabs"), true);
        EditorGUILayout.PropertyField(so.FindProperty("contenedor"),
            new GUIContent("Contenedor (opcional)", "Objeto padre donde se guardan las copias. Si esta vacio se crea uno."));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pincel", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("radioPincel"), new GUIContent("Radio (m)"));
        EditorGUILayout.PropertyField(so.FindProperty("cantidadPorClic"), new GUIContent("Cantidad por pincelada"));
        EditorGUILayout.PropertyField(so.FindProperty("separacionMinima"), new GUIContent("Separacion minima (m)"));
        EditorGUILayout.PropertyField(so.FindProperty("capas"), new GUIContent("Pintar sobre capas"));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Variacion", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("escalaMin"), new GUIContent("Escala minima"));
        EditorGUILayout.PropertyField(so.FindProperty("escalaMax"), new GUIContent("Escala maxima"));
        EditorGUILayout.PropertyField(so.FindProperty("alinearANormal"), new GUIContent("Alinear a la superficie"));
        EditorGUILayout.PropertyField(so.FindProperty("rotacionAleatoria"), new GUIContent("Rotacion aleatoria"));
        EditorGUILayout.PropertyField(so.FindProperty("hundimiento"), new GUIContent("Hundir en superficie (m)"));

        so.ApplyModifiedProperties();
        radioPincel = Mathf.Max(0.05f, radioPincel);
        cantidadPorClic = Mathf.Max(1, cantidadPorClic);
        escalaMax = Mathf.Max(escalaMin, escalaMax);

        EditorGUILayout.Space();
        GUI.backgroundColor = pintando ? new Color(1f, 0.5f, 0.5f) : new Color(0.5f, 1f, 0.5f);
        if (GUILayout.Button(pintando ? "DETENER (Esc)" : "PINTAR", GUILayout.Height(36)))
        {
            pintando = !pintando;
            SceneView.RepaintAll();
        }
        GUI.backgroundColor = Color.white;

        EditorGUILayout.HelpBox(
            "Clic / arrastrar: colocar\nShift + clic / arrastrar: borrar\nCtrl + rueda: tamano del pincel\n" +
            "La superficie necesita un Collider (ProBuilder lo agrega por defecto).",
            MessageType.Info);

        EditorGUILayout.EndScrollView();
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!pintando) return;

        Event e = Event.current;
        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(controlId); // evita que se seleccionen objetos al hacer clic

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            pintando = false;
            Repaint();
            e.Use();
            return;
        }

        if (e.type == EventType.ScrollWheel && e.control)
        {
            radioPincel = Mathf.Max(0.05f, radioPincel * (e.delta.y > 0 ? 0.9f : 1.1f));
            Repaint();
            e.Use();
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f, capas, QueryTriggerInteraction.Ignore))
            return;

        // Dibujar el pincel
        Handles.color = e.shift ? new Color(1f, 0.3f, 0.3f, 0.9f) : new Color(0.3f, 1f, 0.4f, 0.9f);
        Handles.DrawWireDisc(hit.point, hit.normal, radioPincel);
        Handles.DrawLine(hit.point, hit.point + hit.normal * radioPincel * 0.5f);
        sceneView.Repaint();

        bool clic = (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && !e.alt;
        if (!clic) return;

        if (e.type == EventType.MouseDown) puntosRecientes.Clear();

        if (e.shift) Borrar(hit.point);
        else Colocar(hit);

        e.Use();
    }

    private void Colocar(RaycastHit centro)
    {
        if (prefabs.Count == 0 || prefabs.TrueForAll(p => p == null))
        {
            Debug.LogWarning("[Scatter Painter] Carga al menos un prefab en la lista.");
            return;
        }

        Transform padre = ObtenerContenedor();

        // Base ortonormal sobre la superficie para repartir puntos dentro del disco
        Vector3 n = centro.normal;
        Vector3 t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
        Vector3 b = Vector3.Cross(n, t);

        for (int i = 0; i < cantidadPorClic; i++)
        {
            Vector2 r = Random.insideUnitCircle * radioPincel;
            Vector3 origen = centro.point + t * r.x + b * r.y + n * radioPincel;

            if (!Physics.Raycast(origen, -n, out RaycastHit h, radioPincel * 2f, capas, QueryTriggerInteraction.Ignore))
                continue;

            // No pintar sobre las propias copias
            if (h.collider.transform.IsChildOf(padre)) continue;

            if (DemasiadoCerca(h.point)) continue;

            GameObject prefab = prefabs[Random.Range(0, prefabs.Count)];
            if (prefab == null) continue;

            GameObject go = PrefabUtility.IsPartOfPrefabAsset(prefab)
                ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, padre)
                : Instantiate(prefab, padre);

            Quaternion rot = alinearANormal ? Quaternion.FromToRotation(Vector3.up, h.normal) : Quaternion.identity;
            if (rotacionAleatoria) rot *= Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            go.transform.SetPositionAndRotation(h.point - h.normal * hundimiento, rot);
            go.transform.localScale = prefab.transform.localScale * Random.Range(escalaMin, escalaMax);

            Undo.RegisterCreatedObjectUndo(go, "Scatter Painter - colocar");
            puntosRecientes.Add(h.point);
        }
    }

    private bool DemasiadoCerca(Vector3 punto)
    {
        float s2 = separacionMinima * separacionMinima;
        foreach (Vector3 p in puntosRecientes)
            if ((p - punto).sqrMagnitude < s2) return true;

        Transform padre = contenedor;
        if (padre == null) return false;
        foreach (Transform hijo in padre)
            if ((hijo.position - punto).sqrMagnitude < s2) return true;

        return false;
    }

    private void Borrar(Vector3 centro)
    {
        if (contenedor == null) return;
        float r2 = radioPincel * radioPincel;

        var aBorrar = new List<GameObject>();
        foreach (Transform hijo in contenedor)
            if ((hijo.position - centro).sqrMagnitude <= r2) aBorrar.Add(hijo.gameObject);

        foreach (GameObject go in aBorrar)
            Undo.DestroyObjectImmediate(go);
    }

    private Transform ObtenerContenedor()
    {
        if (contenedor == null)
        {
            GameObject go = new GameObject("Scatter_Musgo");
            Undo.RegisterCreatedObjectUndo(go, "Scatter Painter - contenedor");
            contenedor = go.transform;
            Repaint();
        }
        return contenedor;
    }
}