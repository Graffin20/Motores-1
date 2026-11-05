using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Converts Generic AnimationClips into Humanoid (muscle) clips, so animation made for one humanoid rig
/// plays on ANY rig with a humanoid Avatar, whatever its bone names, nesting or bone orientations.
///
/// How: each clip is played frame by frame on a hidden copy of the rig it was made for, and the pose is
/// read back through that rig's humanoid Avatar (HumanPoseHandler) as body position/rotation + muscle
/// values. Those are written as Animator curves (RootT/RootQ + muscles), which is exactly what an FBX
/// imported as Humanoid contains. Unlike the path remapper this is real retargeting: Unity maps the
/// muscles onto each target Avatar at runtime.
///
/// Not baked: IK goal curves (LeftFootT etc.), so leave Foot IK off on states using these clips; bones
/// outside the humanoid definition (twist/extra bones, props). AE_ events and clip settings are copied.
/// </summary>
public class HumanoidClipBaker : EditorWindow
{
    [SerializeField] private List<AnimationClip> _clips = new();
    [SerializeField] private GameObject _sourceRig;
    [SerializeField] private Avatar _sourceAvatar;
    [SerializeField] private string _outputFolder = "";
    [SerializeField] private string _suffix = "_Humanoid";
    [SerializeField] private float _sampleRate;

    private string _report = "";
    private Vector2 _scroll;
    private SerializedObject _serializedWindow;

    [MenuItem("Tools/Animation/Humanoid Clip Baker")]
    public static void Open() => GetWindow<HumanoidClipBaker>("Humanoid Baker");

    private void OnEnable() => _serializedWindow = new SerializedObject(this);

    private void OnGUI()
    {
        _serializedWindow.Update();

        EditorGUILayout.HelpBox(
            "Source Rig = the model the clips were MADE for (its Animator object, e.g. the one holding " +
            "\"Skinned Mesh 0\"). Source Avatar = a humanoid Avatar of that rig. The baked clips play on any " +
            "model with a humanoid Avatar.", MessageType.Info);

        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_clips)), new GUIContent("Clips"), true);
        if (GUILayout.Button("Add Selected Clips"))
        {
            foreach (var clip in Selection.GetFiltered<AnimationClip>(SelectionMode.Assets))
            {
                if (!_clips.Contains(clip)) _clips.Add(clip);
            }
            _serializedWindow.Update();
        }

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_sourceRig)),
            new GUIContent("Source Rig", "Prefab or scene object the clips' paths are relative to. Never modified (a hidden copy is sampled)."));
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_sourceAvatar)),
                new GUIContent("Source Avatar", "Humanoid Avatar of the Source Rig."));
            using (new EditorGUI.DisabledScope(_sourceRig == null))
            {
                if (GUILayout.Button(new GUIContent("Build (Mixamo)", "Build a humanoid Avatar for the Source Rig from Mixamo-style bone names. The rig's stored pose is used as the rest pose, so it should be a T-pose."), GUILayout.Width(110)))
                    BuildMixamoAvatar();
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_sampleRate)),
            new GUIContent("Sample Rate", "Frames per second to sample. 0 = each clip's own frame rate."));
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_outputFolder)),
            new GUIContent("Output Folder", "Project path like Assets/Animations/Humanoid. Empty = next to each source clip."));
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_suffix)), new GUIContent("Name Suffix"));

        _serializedWindow.ApplyModifiedProperties();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(_clips.Count == 0 || _sourceRig == null || _sourceAvatar == null))
        {
            if (GUILayout.Button("Bake Humanoid Clips")) Bake();
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------ bake

    private void Bake()
    {
        if (!ValidateAvatar(out string error))
        {
            _report = error;
            return;
        }
        if (!string.IsNullOrEmpty(_outputFolder) && !AssetDatabase.IsValidFolder(_outputFolder))
        {
            _report = $"Output folder '{_outputFolder}' doesn't exist (must be a project path like Assets/...).";
            return;
        }

        var clips = _clips.Where(c => c != null).Distinct().ToList();
        var sb = new StringBuilder();

        // Hidden copy: sampling writes every animated transform, and the user's prefab/scene must not change.
        var rig = Instantiate(_sourceRig);
        rig.hideFlags = HideFlags.HideAndDontSave;
        rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        // An Animator with a humanoid Avatar on the copy would claim the bones the Generic clip animates.
        if (rig.TryGetComponent(out Animator rigAnimator))
        {
            rigAnimator.avatar = null;
            rigAnimator.runtimeAnimatorController = null;
        }

        try
        {
            using var poseHandler = new HumanPoseHandler(_sourceAvatar, rig.transform);
            for (int i = 0; i < clips.Count; i++)
            {
                var source = clips[i];
                EditorUtility.DisplayProgressBar("Baking humanoid clips", source.name, (float)i / clips.Count);

                if (source.humanMotion)
                {
                    sb.AppendLine($"{source.name}: skipped, already a Humanoid clip.");
                    continue;
                }

                var baked = BakeClip(source, rig, poseHandler);
                string folder = string.IsNullOrEmpty(_outputFolder)
                    ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(source)).Replace('\\', '/')
                    : _outputFolder;
                string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{source.name}{_suffix}.anim");
                AssetDatabase.CreateAsset(baked, path);
                sb.AppendLine($"{source.name} -> {path}" + (baked.humanMotion ? "" : "  WARNING: Unity doesn't see it as humanoid"));
            }
        }
        finally
        {
            DestroyImmediate(rig);
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }

        _report = $"Done.\n\n{sb}";
    }

    private AnimationClip BakeClip(AnimationClip source, GameObject rig, HumanPoseHandler poseHandler)
    {
        float rate = _sampleRate > 0f ? _sampleRate : Mathf.Max(1f, source.frameRate);
        int frames = Mathf.Max(1, Mathf.RoundToInt(source.length * rate));

        var muscleCount = HumanTrait.MuscleCount;
        var rootT = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
        var rootQ = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };
        var muscles = new AnimationCurve[muscleCount];
        for (int m = 0; m < muscleCount; m++) muscles[m] = new AnimationCurve();

        var pose = new HumanPose();
        Quaternion previousQ = Quaternion.identity;

        for (int f = 0; f <= frames; f++)
        {
            float time = Mathf.Min(f / rate, source.length);
            source.SampleAnimation(rig, time);
            poseHandler.GetHumanPose(ref pose);

            // q and -q are the same rotation, but interpolating between them spins the body the long way.
            Quaternion q = pose.bodyRotation;
            if (f > 0 && Quaternion.Dot(previousQ, q) < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            previousQ = q;

            rootT[0].AddKey(time, pose.bodyPosition.x);
            rootT[1].AddKey(time, pose.bodyPosition.y);
            rootT[2].AddKey(time, pose.bodyPosition.z);
            rootQ[0].AddKey(time, q.x);
            rootQ[1].AddKey(time, q.y);
            rootQ[2].AddKey(time, q.z);
            rootQ[3].AddKey(time, q.w);
            for (int m = 0; m < muscleCount; m++) muscles[m].AddKey(time, pose.muscles[m]);
        }

        var bindings = new List<EditorCurveBinding>();
        var curves = new List<AnimationCurve>();
        void Add(string property, AnimationCurve curve)
        {
            for (int k = 0; k < curve.length; k++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
            }
            bindings.Add(EditorCurveBinding.FloatCurve("", typeof(Animator), property));
            curves.Add(curve);
        }

        string[] axes = { "x", "y", "z", "w" };
        for (int a = 0; a < 3; a++) Add("RootT." + axes[a], rootT[a]);
        for (int a = 0; a < 4; a++) Add("RootQ." + axes[a], rootQ[a]);
        for (int m = 0; m < muscleCount; m++) Add(MuscleCurveName(HumanTrait.MuscleName[m]), muscles[m]);

        var baked = new AnimationClip { name = source.name + _suffix, frameRate = rate };
        AnimationUtility.SetEditorCurves(baked, bindings.ToArray(), curves.ToArray());
        AnimationUtility.SetAnimationClipSettings(baked, AnimationUtility.GetAnimationClipSettings(source));
        AnimationUtility.SetAnimationEvents(baked, AnimationUtility.GetAnimationEvents(source));
        return baked;
    }

    /// <summary>
    /// HumanTrait names finger muscles "Left Thumb 1 Stretched", but the Animator curve property is
    /// "LeftHand.Thumb.1 Stretched" (body muscles use the same name in both).
    /// </summary>
    private static string MuscleCurveName(string muscleName)
    {
        foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
        {
            if (!muscleName.Contains(" " + finger + " ")) continue;
            return muscleName
                .Replace("Left ", "LeftHand.")
                .Replace("Right ", "RightHand.")
                .Replace(finger + " ", finger + ".");
        }
        return muscleName;
    }

    // ------------------------------------------------------------------ avatar

    private bool ValidateAvatar(out string error)
    {
        error = null;
        if (!_sourceAvatar.isHuman || !_sourceAvatar.isValid)
        {
            error = $"'{_sourceAvatar.name}' is not a valid humanoid Avatar.";
            return false;
        }

        var names = new HashSet<string>(_sourceRig.GetComponentsInChildren<Transform>(true).Select(t => t.name));
        var missing = _sourceAvatar.humanDescription.human.Select(h => h.boneName).Where(n => !names.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            error = $"Source Avatar doesn't match the Source Rig: {missing.Count} bone(s) not found " +
                    $"({string.Join(", ", missing.Take(8))}). Use the Avatar built for this rig.";
            return false;
        }
        return true;
    }

    private void BuildMixamoAvatar()
    {
        var transforms = _sourceRig.GetComponentsInChildren<Transform>(true);

        var human = new List<HumanBone>();
        var used = new HashSet<HumanBodyBones>();
        foreach (var t in transforms)
        {
            if (!AnimationPathRemapper.MixamoNames.ToHuman.TryGetValue(AnimationPathRemapper.Normalize(t.name), out var bone)) continue;
            if (!used.Add(bone)) continue;
            human.Add(new HumanBone
            {
                boneName = t.name,
                humanName = HumanTrait.BoneName[(int)bone],
                limit = new HumanLimit { useDefaultValues = true },
            });
        }

        // The rest pose is the rig's stored transforms. Muscles are measured from it, so it must be a T-pose
        // like the target Avatar's, or every baked clip inherits the difference (e.g. arms too low).
        var description = new HumanDescription
        {
            human = human.ToArray(),
            skeleton = transforms.Select(t => new SkeletonBone
            {
                name = t.name,
                position = t.localPosition,
                rotation = t.localRotation,
                scale = t.localScale,
            }).ToArray(),
            upperArmTwist = 0.5f,
            lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f,
            lowerLegTwist = 0.5f,
            armStretch = 0.05f,
            legStretch = 0.05f,
            feetSpacing = 0f,
            hasTranslationDoF = false,
        };

        var avatar = AvatarBuilder.BuildHumanAvatar(_sourceRig, description);
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
        {
            _report = $"Couldn't build a humanoid Avatar ({human.Count} Mixamo bones found). Check the Console for Unity's reason; " +
                      "required bones like Hips, legs, spine, arms and head must all be present.";
            return;
        }

        string path = EditorUtility.SaveFilePanelInProject("Save Source Avatar", _sourceRig.name + "_Avatar", "asset", "Where to save the Avatar");
        if (string.IsNullOrEmpty(path)) return;

        avatar.name = Path.GetFileNameWithoutExtension(path);
        AssetDatabase.CreateAsset(avatar, path);
        AssetDatabase.SaveAssets();
        _sourceAvatar = avatar;
        _report = $"Built humanoid Avatar with {human.Count} bones -> {path}";
    }
}
