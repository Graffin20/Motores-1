using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Makes a Humanoid model's Avatar use a pose taken from one of its own clips (e.g. a "Rest_Pose" T-pose)
/// instead of the pose the file's skeleton happens to be in.
///
/// Why: "Create From This Model" builds the Avatar from the skeleton pose stored in the import settings
/// (ModelImporter.humanDescription.skeleton, the same data Configure > Enforce T-Pose edits). Animation
/// files often carry no clean bind pose, so that stored pose ends up slightly off, and every retargeted
/// clip inherits the error (wide arms, exaggerated stride).
///
/// How: the clip is Humanoid (muscle values relative to the CURRENT, wrong Avatar), so sampling it would
/// reproduce the error. The tool imports a temporary GENERIC copy of the file, samples the clip there to
/// get raw bone transforms, writes those into humanDescription.skeleton by bone name and reimports.
/// The bone mapping (humanDescription.human) is left untouched. A copy of the .meta is saved to
/// AvatarPoseBackups/ (outside Assets) first.
/// </summary>
public class AvatarPoseFromClip : EditorWindow
{
    private const string BackupFolder = "AvatarPoseBackups";

    [SerializeField] private GameObject _model;
    [SerializeField] private string _clipName = "Rest_Pose";
    [SerializeField] private float _sampleTime;

    [MenuItem("Tools/Animation/Avatar Pose From Clip")]
    public static void Open() => GetWindow<AvatarPoseFromClip>("Avatar Pose From Clip");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Writes the pose of a clip inside the model file into the Avatar's stored " +
                                "skeleton pose, then reimports. The model must be imported as Humanoid and " +
                                "its Avatar configured at least once (bone mapping present).", MessageType.Info);

        _model = (GameObject)EditorGUILayout.ObjectField("Model (FBX)", _model, typeof(GameObject), false);
        _clipName = EditorGUILayout.TextField("Clip Name", _clipName);
        _sampleTime = EditorGUILayout.FloatField("Sample Time (s)", _sampleTime);

        using (new EditorGUI.DisabledScope(_model == null || string.IsNullOrEmpty(_clipName)))
        {
            if (GUILayout.Button("Apply Pose To Avatar")) Apply(_model, _clipName, _sampleTime);
        }
    }

    private static void Apply(GameObject model, string clipName, float sampleTime)
    {
        string path = AssetDatabase.GetAssetPath(model);
        if (!(AssetImporter.GetAtPath(path) is ModelImporter importer))
        {
            Fail($"{path} is not a model file.");
            return;
        }
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            Fail("Set the model's Rig to Humanoid first.");
            return;
        }

        HumanDescription description = importer.humanDescription;
        if (description.skeleton == null || description.skeleton.Length == 0)
        {
            Fail("The import settings hold no skeleton pose yet. Open Rig > Configure once, click Done, then retry.");
            return;
        }

        if (!TrySampleGenericPose(path, clipName, sampleTime, out var pose, out string error))
        {
            Fail(error);
            return;
        }

        BackupMeta(path);

        var skeleton = description.skeleton;
        var missing = new List<string>();
        int updated = 0;

        // Entry 0 is the model's root object ("<file>(Clone)"), not a bone: keep it as stored.
        for (int i = 1; i < skeleton.Length; i++)
        {
            if (!pose.TryGetValue(skeleton[i].name, out var t))
            {
                missing.Add(skeleton[i].name);
                continue;
            }
            skeleton[i].position = t.position;
            skeleton[i].rotation = t.rotation;
            skeleton[i].scale = t.scale;
            updated++;
        }

        description.skeleton = skeleton;
        importer.humanDescription = description;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.SaveAndReimport();

        string report = $"Updated {updated} bone(s) of '{Path.GetFileName(path)}' from '{clipName}' at {sampleTime}s.";
        if (missing.Count > 0) report += $"\nNot found in the sampled pose (kept as they were): {string.Join(", ", missing)}";
        Debug.Log("[AvatarPoseFromClip] " + report);
        EditorUtility.DisplayDialog("Avatar Pose From Clip", report + "\n\nCheck the result in Rig > Configure.", "OK");
    }

    private struct LocalPose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    /// <summary>Samples the clip on a temporary Generic import of the file and returns each bone's local transform by name.</summary>
    private static bool TrySampleGenericPose(string path, string clipName, float time,
                                             out Dictionary<string, LocalPose> pose, out string error)
    {
        pose = new Dictionary<string, LocalPose>();
        error = null;

        string tempPath = Path.GetDirectoryName(path).Replace('\\', '/') + "/__AvatarPoseTemp" + Path.GetExtension(path);
        GameObject instance = null;
        try
        {
            if (!AssetDatabase.CopyAsset(path, tempPath))
            {
                error = "Couldn't create the temporary copy of the model.";
                return false;
            }

            var tempImporter = (ModelImporter)AssetImporter.GetAtPath(tempPath);
            tempImporter.animationType = ModelImporterAnimationType.Generic;
            tempImporter.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            tempImporter.SaveAndReimport();

            var clip = AssetDatabase.LoadAllAssetsAtPath(tempPath).OfType<AnimationClip>()
                                    .FirstOrDefault(c => c.name == clipName && !c.name.StartsWith("__preview__"));
            if (clip == null)
            {
                var names = AssetDatabase.LoadAllAssetsAtPath(tempPath).OfType<AnimationClip>()
                                         .Where(c => !c.name.StartsWith("__preview__")).Select(c => c.name);
                error = $"No clip named '{clipName}' in the file. Clips: {string.Join(", ", names)}";
                return false;
            }

            instance = (GameObject)Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(tempPath));
            instance.hideFlags = HideFlags.HideAndDontSave;
            clip.SampleAnimation(instance, Mathf.Clamp(time, 0f, clip.length));

            var duplicates = new List<string>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                if (t == instance.transform) continue;
                if (pose.ContainsKey(t.name)) { duplicates.Add(t.name); continue; }
                pose[t.name] = new LocalPose { position = t.localPosition, rotation = t.localRotation, scale = t.localScale };
            }
            if (duplicates.Count > 0)
                Debug.LogWarning("[AvatarPoseFromClip] Duplicate transform names, first one used: " + string.Join(", ", duplicates.Distinct()));

            return true;
        }
        finally
        {
            if (instance != null) DestroyImmediate(instance);
            AssetDatabase.DeleteAsset(tempPath);
        }
    }

    private static void BackupMeta(string assetPath)
    {
        string meta = assetPath + ".meta";
        Directory.CreateDirectory(BackupFolder);
        string backup = Path.Combine(BackupFolder,
            $"{Path.GetFileName(assetPath)}.{System.DateTime.Now:yyyyMMdd_HHmmss}.meta");
        File.Copy(meta, backup, true);
        Debug.Log($"[AvatarPoseFromClip] Backed up import settings to {backup}");
    }

    private static void Fail(string message)
    {
        Debug.LogError("[AvatarPoseFromClip] " + message);
        EditorUtility.DisplayDialog("Avatar Pose From Clip", message, "OK");
    }
}
