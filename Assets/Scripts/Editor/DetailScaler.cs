using UnityEditor;
using UnityEngine;

public static class DetailScaler
{
    const float Factor = 0.8f; // 0.8 = 20% más chico. 

    [MenuItem("Tools/Terrain/Achicar todos los details")]
    static void Shrink() => Scale(Factor);

    [MenuItem("Tools/Terrain/Agrandar todos los details")]
    static void Grow() => Scale(1f / Factor);

    static void Scale(float f)
    {
        foreach (Terrain t in Terrain.activeTerrains)
        {
            TerrainData d = t.terrainData;
            Undo.RecordObject(d, "Scale Details");

            DetailPrototype[] protos = d.detailPrototypes;
            foreach (DetailPrototype p in protos)
            {
                p.minWidth *= f;
                p.maxWidth *= f;
                p.minHeight *= f;
                p.maxHeight *= f;
            }
            d.detailPrototypes = protos;
            EditorUtility.SetDirty(d);
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"Details escalados x{f}");
    }
}