using UnityEditor;
using UnityEngine;

public static class TerrainSync
{
    [MenuItem("Tools/Terrain/Copiar capas, árboles y detalles al resto")]
    static void Sync()
    {
        Terrain source = Selection.activeGameObject
            ? Selection.activeGameObject.GetComponent<Terrain>()
            : null;

        if (source == null)
        {
            Debug.LogWarning("Seleccioná primero el terrain de origen.");
            return;
        }

        TerrainData src = source.terrainData;

        foreach (Terrain t in Terrain.activeTerrains)
        {
            if (t == source) continue;

            TerrainData d = t.terrainData;
            Undo.RecordObject(d, "Sync Terrain");
            d.terrainLayers = src.terrainLayers;
            d.treePrototypes = src.treePrototypes;
            d.detailPrototypes = src.detailPrototypes;
            EditorUtility.SetDirty(d);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("Terrains sincronizados.");
    }
}