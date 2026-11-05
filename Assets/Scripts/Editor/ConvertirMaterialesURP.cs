// Delicarium - Herramienta de editor
// Convierte materiales rosas (shaders Built-in / custom no compatibles) a URP,
// conservando Albedo, Normal, Emision, Color, Tiling y Alpha Clipping.
// Detecta automaticamente que shaders YA son compatibles con URP y no los toca.
//
// USO:
// 1. Guardar este archivo dentro de Assets/Editor/
// 2. Hacer commit en Git ANTES de usarlo.
// 3. Menu superior > Tools > Delicarium:
//    - "Convertir TODO el proyecto"   -> revisa todos los materiales de Assets/
//    - "Convertir seleccion"          -> solo materiales o carpetas seleccionadas en Project
//    - "Listar materiales rosas"      -> no cambia nada, solo informa en la Console

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ConvertirMaterialesURP
{
    private const string MENU = "Tools/Delicarium/";

    [MenuItem(MENU + "Convertir TODO el proyecto a URP")]
    private static void ConvertirTodo()
    {
        if (!EditorUtility.DisplayDialog("Convertir todo el proyecto",
            "Se van a convertir a URP todos los materiales incompatibles de Assets/.\n\n¿Hiciste commit en Git antes?",
            "Si, convertir", "Cancelar"))
            return;

        Procesar(BuscarTodosLosMateriales(), true);
    }

    [MenuItem(MENU + "Convertir seleccion a URP")]
    private static void ConvertirSeleccion()
    {
        Material[] seleccion = Selection.GetFiltered<Material>(SelectionMode.DeepAssets);
        if (seleccion.Length == 0)
        {
            EditorUtility.DisplayDialog("Sin seleccion", "Selecciona materiales o carpetas en la ventana Project.", "OK");
            return;
        }
        Procesar(seleccion, true);
    }

    [MenuItem(MENU + "Listar materiales rosas (sin cambiar nada)")]
    private static void Listar()
    {
        Procesar(BuscarTodosLosMateriales(), false);
    }

    // ------------------------------------------------------------------

    private static Material[] BuscarTodosLosMateriales()
    {
        var lista = new List<Material>();
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            Material m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m != null) lista.Add(m);
        }
        return lista.ToArray();
    }

    private static void Procesar(Material[] materiales, bool aplicarCambios)
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        Shader urpTerrain = Shader.Find("Universal Render Pipeline/Terrain/Lit");
        if (urpLit == null)
        {
            EditorUtility.DisplayDialog("Error", "No se encontro URP/Lit. Verifica que el proyecto use URP.", "OK");
            return;
        }

        int convertidos = 0, compatibles = 0, salteados = 0;

        try
        {
            for (int i = 0; i < materiales.Length; i++)
            {
                Material mat = materiales[i];
                string path = AssetDatabase.GetAssetPath(mat);
                EditorUtility.DisplayProgressBar("Delicarium - Materiales URP", path, (float)i / materiales.Length);

                // Materiales embebidos en FBX no se pueden editar directamente
                if (!path.EndsWith(".mat")) { salteados++; continue; }

                Shader shader = mat.shader;
                string shaderName = shader != null ? shader.name : "";

                if (EsCompatibleConURP(shader)) { compatibles++; continue; }
                if (DebeSaltearse(shaderName))
                {
                    salteados++;
                    Debug.Log($"[Delicarium] Salteado (revisar a mano): {path}  | shader: {shaderName}", mat);
                    continue;
                }

                if (!aplicarCambios)
                {
                    Debug.Log($"[Delicarium] Rosa: {path}  | shader: {shaderName}", mat);
                    convertidos++;
                    continue;
                }

                Undo.RecordObject(mat, "Convertir a URP");

                if (shaderName.Contains("Terrain") && urpTerrain != null)
                    mat.shader = urpTerrain;
                else
                    ConvertirALit(mat, urpLit);

                EditorUtility.SetDirty(mat);
                convertidos++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        string accion = aplicarCambios ? "Convertidos" : "Rosas encontrados";
        Debug.Log($"[Delicarium] {accion}: {convertidos} | Ya compatibles: {compatibles} | Salteados: {salteados}");
    }

    // Revisa los tags internos del shader para saber si ya funciona en URP
    private static bool EsCompatibleConURP(Shader shader)
    {
        if (shader == null || shader.name == "Hidden/InternalErrorShader") return false;

        var tagPipeline = new ShaderTagId("RenderPipeline");
        var tagLightMode = new ShaderTagId("LightMode");

        for (int s = 0; s < shader.subshaderCount; s++)
        {
            if (shader.FindSubshaderTagValue(s, tagPipeline).name == "UniversalPipeline") return true;

            for (int p = 0; p < shader.GetPassCountInSubshader(s); p++)
            {
                if (shader.FindPassTagValue(s, p, tagLightMode).name.StartsWith("Universal")) return true;
            }
        }
        return false;
    }

    // Shaders que funcionan en URP sin tags propios, o que no conviene pasar a Lit
    private static bool DebeSaltearse(string shaderName)
    {
        return shaderName.StartsWith("UI/")
            || shaderName.StartsWith("Sprites/")
            || shaderName.StartsWith("TextMeshPro")
            || shaderName.StartsWith("Skybox/")
            || shaderName.StartsWith("Unlit/")
            || shaderName.Contains("Particles")   // usar el Render Pipeline Converter para estos
            || shaderName.StartsWith("Hidden/") && shaderName != "Hidden/InternalErrorShader";
    }

    private static void ConvertirALit(Material mat, Shader urpLit)
    {
        // ---- 1. Leer datos del material original ----
        Texture albedo = null, normal = null, emission = null;
        Vector2 tiling = Vector2.one, offset = Vector2.zero;

        foreach (string prop in mat.GetTexturePropertyNames())
        {
            Texture tex = mat.GetTexture(prop);
            if (tex == null) continue;

            string p = prop.ToLower();
            if (p.Contains("detail")) continue;

            if (normal == null && (p.Contains("bump") || p.Contains("normal")))
                normal = tex;
            else if (emission == null && p.Contains("emission"))
                emission = tex;
            else if (albedo == null && (p.Contains("main") || p.Contains("albedo") || p.Contains("diffuse") || p.Contains("base") || p.Contains("color")))
            {
                albedo = tex;
                tiling = mat.GetTextureScale(prop);
                offset = mat.GetTextureOffset(prop);
            }
        }

        Color color = Color.white;
        if (mat.HasProperty("_Color")) color = mat.GetColor("_Color");
        else if (mat.HasProperty("_BaseColor")) color = mat.GetColor("_BaseColor");

        float normalScale = LeerPrimerFloat(mat, 1f, "_BumpScale", "_NormalPower", "_NormalStrength", "_NormalScale");

        bool usaCutout = mat.HasProperty("_Cutoff") || mat.renderQueue == 2450 || mat.IsKeywordEnabled("_ALPHATEST_ON");
        float cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;

        // ---- 2. Cambiar shader y reasignar ----
        mat.shader = urpLit;
        mat.SetColor("_BaseColor", color);

        if (albedo != null)
        {
            mat.SetTexture("_BaseMap", albedo);
            mat.SetTextureScale("_BaseMap", tiling);
            mat.SetTextureOffset("_BaseMap", offset);
        }

        if (normal != null)
        {
            MarcarComoNormalMap(normal);
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", normalScale);
            mat.EnableKeyword("_NORMALMAP");
        }

        if (emission != null)
        {
            mat.SetTexture("_EmissionMap", emission);
            mat.SetColor("_EmissionColor", Color.white);
            mat.EnableKeyword("_EMISSION");
        }

        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Smoothness", 0.15f);

        if (usaCutout) // Pasto, hojas, plantas
        {
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", cutoff);
            mat.SetFloat("_Cull", 0f); // Doble cara
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = 2450;
        }
    }

    private static float LeerPrimerFloat(Material mat, float valorPorDefecto, params string[] nombres)
    {
        foreach (string n in nombres)
            if (mat.HasProperty(n)) return mat.GetFloat(n);
        return valorPorDefecto;
    }

    private static void MarcarComoNormalMap(Texture tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.NormalMap)
        {
            importer.textureType = TextureImporterType.NormalMap;
            importer.SaveAndReimport();
        }
    }
}
