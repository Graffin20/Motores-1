using UnityEngine;
using UnityEditor;

public class GLBMaskGenerator : EditorWindow
{
    private GameObject glbPrefab;
    private string maskName = "GLB_UpperBodyMask";

    [MenuItem("Tools/GLB Mask Generator")]
    public static void ShowWindow()
    {
        GetWindow<GLBMaskGenerator>("GLB Mask Generator");
    }

    private void OnGUI()
    {
        GUILayout.Label("Generate Avatar Mask from GLB Hierarchy", EditorStyles.boldLabel);

        glbPrefab = (GameObject)EditorGUILayout.ObjectField("GLB Prefab/Object", glbPrefab, typeof(GameObject), true);
        maskName = EditorGUILayout.TextField("Mask Name", maskName);

        if (GUILayout.Button("Generate Mask") && glbPrefab != null)
        {
            CreateMask();
        }
    }

    private void CreateMask()
    {
        AvatarMask mask = new AvatarMask();
        Transform[] allTransforms = glbPrefab.GetComponentsInChildren<Transform>();

        foreach (Transform t in allTransforms)
        {
            // Skip the top-most root object itself
            if (t == glbPrefab.transform) continue;

            // Generate the relative path Unity animations use
            string path = AnimationUtility.CalculateTransformPath(t, glbPrefab.transform);

            // By default, let's enable the bone. You can change this logic.
            bool isUpperBody = IsUpperBodyBone(t.name.ToLower());

            mask.AddTransformPath(t, false); // Initialize in mask
            int index = mask.transformCount - 1;

            // Set path name and toggle status
            mask.SetTransformPath(index, path);
            mask.SetTransformActive(index, isUpperBody);
        }

        string savePath = $"Assets/{maskName}.mask";
        AssetDatabase.CreateAsset(mask, savePath);
        AssetDatabase.SaveAssets();

        Debug.Log($"Successfully created Avatar Mask at: {savePath}");
    }

    private bool IsUpperBodyBone(string boneName)
    {
        // Simple name matching. Customize these strings based on your GLB's naming convention!
        if (boneName.Contains("leg") || boneName.Contains("foot") || boneName.Contains("toe") || boneName.Contains("thigh") || boneName.Contains("calf"))
        {
            return false;
        }
        // Keep spine, chest, arms, hands, head active
        return true;
    }
}
