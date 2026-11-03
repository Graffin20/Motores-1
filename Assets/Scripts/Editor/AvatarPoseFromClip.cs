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

    private enum PoseSource { ClipInModel, ExternalClip }

    [SerializeField] private GameObject _model;
    [SerializeField] private PoseSource _source = PoseSource.ClipInModel;
    [SerializeField] private string _clipName = "Rest_Pose";
    [SerializeField] private AnimationClip _externalClip;
    [SerializeField] private GameObject _poseRig;
    [SerializeField] private float _sampleTime;

    [MenuItem("Tools/Animation/Avatar Pose From Clip")]
    public static void Open() => GetWindow<AvatarPoseFromClip>("Avatar Pose From Clip");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Writes the pose of a clip into the model's stored Avatar skeleton pose, then " +
                                "reimports. The model must be imported as Humanoid and its Avatar configured at " +
                                "least once (bone mapping present).", MessageType.Info);

        _model = (GameObject)EditorGUILayout.ObjectField("Model (FBX)", _model, typeof(GameObject), false);
        _source = (PoseSource)EditorGUILayout.EnumPopup("Pose Source", _source);

        bool ready = _model != null;
        if (_source == PoseSource.ClipInModel)
        {
            _clipName = EditorGUILayout.TextField("Clip Name", _clipName);
            ready &= !string.IsNullOrEmpty(_clipName);
        }
        else
        {
            _externalClip = (AnimationClip)EditorGUILayout.ObjectField("Clip", _externalClip, typeof(AnimationClip), false);
            ready &= _externalClip != null;

            if (_externalClip != null && _externalClip.humanMotion)
            {
                EditorGUILayout.HelpBox("Humanoid clip: it stores muscle values relative to an Avatar, so it is played " +
                                        "through the Pose Rig, a model with a CORRECT Avatar (e.g. General Animations.fbx). " +
                                        "The rigs may differ: the model's humanoid bones are matched by Avatar role and " +
                                        "turned to point the same way. Its other bones keep their file pose.", MessageType.None);
                _poseRig = (GameObject)EditorGUILayout.ObjectField("Pose Rig", _poseRig, typeof(GameObject), false);
                ready &= _poseRig != null;
            }
            else if (_externalClip != null)
            {
                EditorGUILayout.HelpBox("Generic clip: sampled on a Generic copy of the model, so its curve paths " +
                                        "must match the model's hierarchy.", MessageType.None);
            }
        }
        _sampleTime = EditorGUILayout.FloatField("Sample Time (s)", _sampleTime);

        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("Apply Pose To Avatar")) Apply();
        }
    }

    private void Apply()
    {
        GameObject model = _model;
        float sampleTime = _sampleTime;
        string clipName = _source == PoseSource.ClipInModel ? _clipName : _externalClip.name;
        bool external = _source == PoseSource.ExternalClip;
        bool humanoidExternal = external && _externalClip.humanMotion;

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

        Dictionary<string, LocalPose> pose;
        string error;
        bool sampled = humanoidExternal
            ? TrySampleHumanoidPose(_externalClip, _poseRig, model, description, sampleTime, out pose, out error)
            : TrySampleGenericPose(path, clipName, external ? _externalClip : null, sampleTime, out pose, out error);
        if (!sampled)
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
                if (!humanoidExternal) missing.Add(skeleton[i].name); // humanoid source: unmapped bones are skipped by design
                continue;
            }
            skeleton[i].rotation = t.rotation;
            // An external source is another file: its bone offsets/scale may differ slightly, and the pose is
            // the rotations, so the model keeps its own. A clip from the model itself is the same skeleton.
            if (!external)
            {
                skeleton[i].position = t.position;
                skeleton[i].scale = t.scale;
            }
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

    /// <summary>
    /// Samples a clip on a temporary Generic import of the file and returns each bone's local transform by
    /// name. The clip is <paramref name="externalClip"/> if given (a Generic clip whose paths match the
    /// model), else the clip named <paramref name="clipName"/> inside the file.
    /// </summary>
    private static bool TrySampleGenericPose(string path, string clipName, AnimationClip externalClip, float time,
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

            var clip = externalClip != null ? externalClip
                : AssetDatabase.LoadAllAssetsAtPath(tempPath).OfType<AnimationClip>()
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

    /// <summary>
    /// Poses the Pose Rig with a Humanoid clip, then bends the TARGET model's humanoid bones to match it and
    /// returns their local rotations by (target) bone name.
    ///
    /// The two files may use different skeletons (Mixamo "mixamorig:LeftArm" vs Mesh2Motion "upperarm_l") with
    /// different bone rest orientations, so rotations can't be copied. Instead bones are matched by humanoid
    /// role through each file's Avatar mapping, and each target bone is turned (parents first) so that it
    /// points at its child the same way the posed rig's bone does, in each model's root space (both face +Z,
    /// as Unity's Humanoid requires). Twist about the bone's own axis is kept from the target's file pose,
    /// except the hips and hands, which also match a sideways axis (thigh to thigh, index to little finger).
    /// Unmapped bones (root, twist, cloth) are not returned: the target keeps their file pose, which is what
    /// Unity expects for in-between bones.
    /// </summary>
    private static bool TrySampleHumanoidPose(AnimationClip clip, GameObject poseRig, GameObject target,
                                              HumanDescription targetDescription, float time,
                                              out Dictionary<string, LocalPose> pose, out string error)
    {
        pose = new Dictionary<string, LocalPose>();
        error = null;

        string rigPath = AssetDatabase.GetAssetPath(poseRig);
        Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(rigPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null || !avatar.isHuman || !avatar.isValid)
        {
            error = $"'{poseRig.name}' has no valid Humanoid Avatar to play the clip through.";
            return false;
        }

        time = Mathf.Clamp(time, 0f, clip.length);

        GameObject rigInstance = null, targetInstance = null;
        try
        {
            rigInstance = Instantiate(poseRig);
            rigInstance.hideFlags = HideFlags.HideAndDontSave;
            rigInstance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            using (var handler = new HumanPoseHandler(avatar, rigInstance.transform))
            {
                var humanPose = new HumanPose();
                handler.GetHumanPose(ref humanPose); // allocates the muscle array

                int musclesRead = 0;
                for (int m = 0; m < humanPose.muscles.Length; m++)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip,
                        EditorCurveBinding.FloatCurve("", typeof(Animator), MuscleCurveName(HumanTrait.MuscleName[m])));
                    if (curve == null) continue;
                    humanPose.muscles[m] = curve.Evaluate(time);
                    musclesRead++;
                }
                if (musclesRead == 0)
                {
                    error = $"'{clip.name}' has no muscle curves.";
                    return false;
                }

                // Body upright and facing forward, NOT the clip's RootQ: that carries the clip's root-motion
                // facing (and the source rig's backwards root), which would tilt or turn the pose.
                humanPose.bodyRotation = Quaternion.identity;
                handler.SetHumanPose(ref humanPose);
            }

            targetInstance = Instantiate(target);
            targetInstance.hideFlags = HideFlags.HideAndDontSave;
            targetInstance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var source = MapHumanBones(rigInstance, avatar.humanDescription);
            var dest = MapHumanBones(targetInstance, targetDescription);
            if (!source.ContainsKey(HumanBodyBones.Hips) || !dest.ContainsKey(HumanBodyBones.Hips))
            {
                error = "Hips aren't mapped in one of the two Avatars.";
                return false;
            }

            // Parents first, so every bone is aimed from its already-posed parent.
            foreach (var bone in dest.Keys.OrderBy(b => Depth(dest[b])).ToList())
            {
                if (!source.ContainsKey(bone)) continue;
                if (!TryGetAim(bone, source, dest, out HumanBodyBones child)) continue;

                Transform t = dest[bone];
                Vector3 wanted = source[child].position - source[bone].position;
                Vector3 current = dest[child].position - t.position;
                if (current.sqrMagnitude < 1e-12f || wanted.sqrMagnitude < 1e-12f) continue;

                if (TryGetSideAxis(bone, source, dest, out HumanBodyBones sideA, out HumanBodyBones sideB))
                {
                    Vector3 wantedSide = source[sideB].position - source[sideA].position;
                    Vector3 currentSide = dest[sideB].position - dest[sideA].position;
                    t.rotation = Quaternion.LookRotation(wanted, wantedSide) *
                                 Quaternion.Inverse(Quaternion.LookRotation(current, currentSide)) * t.rotation;
                }
                else
                {
                    t.rotation = Quaternion.FromToRotation(current, wanted) * t.rotation;
                }
            }

            foreach (var t in dest.Values)
                pose[t.name] = new LocalPose { position = t.localPosition, rotation = t.localRotation, scale = t.localScale };
            return true;
        }
        finally
        {
            if (rigInstance != null) DestroyImmediate(rigInstance);
            if (targetInstance != null) DestroyImmediate(targetInstance);
        }
    }

    /// <summary>Humanoid role -> transform in an instance, from the Avatar's bone mapping (bone names).</summary>
    private static Dictionary<HumanBodyBones, Transform> MapHumanBones(GameObject instance, HumanDescription description)
    {
        var byName = new Dictionary<string, Transform>();
        foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;

        var roleByName = new Dictionary<string, HumanBodyBones>();
        for (int b = 0; b < (int)HumanBodyBones.LastBone; b++) roleByName[HumanTrait.BoneName[b]] = (HumanBodyBones)b;

        var map = new Dictionary<HumanBodyBones, Transform>();
        foreach (var human in description.human)
        {
            if (roleByName.TryGetValue(human.humanName, out var role) && byName.TryGetValue(human.boneName, out var t))
                map[role] = t;
        }
        return map;
    }

    private static int Depth(Transform t)
    {
        int depth = 0;
        for (; t.parent != null; t = t.parent) depth++;
        return depth;
    }

    /// <summary>The bone a role points at: the first candidate mapped in BOTH Avatars. None = keep (head, toes, finger tips).</summary>
    private static bool TryGetAim(HumanBodyBones bone, Dictionary<HumanBodyBones, Transform> a,
                                  Dictionary<HumanBodyBones, Transform> b, out HumanBodyBones child)
    {
        HumanBodyBones[] candidates;
        switch (bone)
        {
            case HumanBodyBones.Hips: candidates = new[] { HumanBodyBones.Spine }; break;
            case HumanBodyBones.Spine: candidates = new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck }; break;
            case HumanBodyBones.Chest: candidates = new[] { HumanBodyBones.UpperChest, HumanBodyBones.Neck }; break;
            case HumanBodyBones.UpperChest: candidates = new[] { HumanBodyBones.Neck }; break;
            case HumanBodyBones.Neck: candidates = new[] { HumanBodyBones.Head }; break;
            case HumanBodyBones.LeftShoulder: candidates = new[] { HumanBodyBones.LeftUpperArm }; break;
            case HumanBodyBones.RightShoulder: candidates = new[] { HumanBodyBones.RightUpperArm }; break;
            case HumanBodyBones.LeftUpperArm: candidates = new[] { HumanBodyBones.LeftLowerArm }; break;
            case HumanBodyBones.RightUpperArm: candidates = new[] { HumanBodyBones.RightLowerArm }; break;
            case HumanBodyBones.LeftLowerArm: candidates = new[] { HumanBodyBones.LeftHand }; break;
            case HumanBodyBones.RightLowerArm: candidates = new[] { HumanBodyBones.RightHand }; break;
            case HumanBodyBones.LeftHand: candidates = new[] { HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftRingProximal }; break;
            case HumanBodyBones.RightHand: candidates = new[] { HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightRingProximal }; break;
            case HumanBodyBones.LeftUpperLeg: candidates = new[] { HumanBodyBones.LeftLowerLeg }; break;
            case HumanBodyBones.RightUpperLeg: candidates = new[] { HumanBodyBones.RightLowerLeg }; break;
            case HumanBodyBones.LeftLowerLeg: candidates = new[] { HumanBodyBones.LeftFoot }; break;
            case HumanBodyBones.RightLowerLeg: candidates = new[] { HumanBodyBones.RightFoot }; break;
            case HumanBodyBones.LeftFoot: candidates = new[] { HumanBodyBones.LeftToes }; break;
            case HumanBodyBones.RightFoot: candidates = new[] { HumanBodyBones.RightToes }; break;
            default:
                // Fingers: proximal -> intermediate -> distal are consecutive in the enum (distal has no aim).
                string name = bone.ToString();
                candidates = name.EndsWith("Proximal") || name.EndsWith("Intermediate")
                    ? new[] { bone + 1 } : new HumanBodyBones[0];
                break;
        }

        foreach (var candidate in candidates)
        {
            if (a.ContainsKey(candidate) && b.ContainsKey(candidate))
            {
                child = candidate;
                return true;
            }
        }
        child = default;
        return false;
    }

    /// <summary>Second axis that fixes the twist where it matters: pelvis (thigh to thigh) and hands (index to little finger).</summary>
    private static bool TryGetSideAxis(HumanBodyBones bone, Dictionary<HumanBodyBones, Transform> a,
                                       Dictionary<HumanBodyBones, Transform> b, out HumanBodyBones from, out HumanBodyBones to)
    {
        switch (bone)
        {
            case HumanBodyBones.Hips: from = HumanBodyBones.LeftUpperLeg; to = HumanBodyBones.RightUpperLeg; break;
            case HumanBodyBones.LeftHand: from = HumanBodyBones.LeftIndexProximal; to = HumanBodyBones.LeftLittleProximal; break;
            case HumanBodyBones.RightHand: from = HumanBodyBones.RightIndexProximal; to = HumanBodyBones.RightLittleProximal; break;
            default: from = to = default; return false;
        }
        return a.ContainsKey(from) && a.ContainsKey(to) && b.ContainsKey(from) && b.ContainsKey(to);
    }

    /// <summary>
    /// HumanTrait names finger muscles "Left Thumb 1 Stretched", but the Animator curve property is
    /// "LeftHand.Thumb.1 Stretched" (body muscles use the same name in both). Same mapping as HumanoidClipBaker.
    /// </summary>
    private static string MuscleCurveName(string muscleName)
    {
        foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
        {
            if (!muscleName.Contains(" " + finger + " ")) continue;
            return muscleName.Replace("Left ", "LeftHand.").Replace("Right ", "RightHand.").Replace(finger + " ", finger + ".");
        }
        return muscleName;
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
