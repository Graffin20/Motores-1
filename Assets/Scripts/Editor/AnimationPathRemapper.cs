using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rewrites the bone paths inside Generic AnimationClips so clips authored for one hierarchy (e.g.
/// Mesh2Motion's "Skinned Mesh 0/root/mixamorigHips/...") drive a different hierarchy with the same
/// humanoid skeleton but different bone names / nesting — or the other way round.
///
/// Each curve's path is resolved by its LAST segment (the bone name) to a transform under the target
/// root, and the new path is that transform's real path from the root. So extra/missing intermediate
/// objects ("Skinned Mesh 0", "root", "Armature"...) are handled automatically.
///
/// Bone name resolution, first match wins:
///   1. Manual overrides (AnimationBoneMap asset).
///   2. Same name exists under the target.
///   3. Humanoid bone identity: source name -> HumanBodyBones (via the optional Source Avatar, or the
///      built-in Mixamo naming table) -> target bone (via the target's humanoid Avatar, or Mixamo naming).
///   4. Normalized name match (case, "mixamorig" prefix and separators ignored).
///
/// IMPORTANT: this only renames paths, it does not retarget. Rotation curves are local rotations, so the
/// result is only correct if the target bones have the same rest orientation (bone roll/axes) as the
/// source, e.g. another Mixamo-style export. Position curves (usually only the hips) assume similar
/// proportions. For rigs with different bone orientations, use Unity's Humanoid retargeting instead
/// (import the source as FBX with Rig > Animation Type: Humanoid).
/// </summary>
public class AnimationPathRemapper : EditorWindow
{
    private enum UnmappedCurveMode { KeepOriginalPath, Drop }

    [SerializeField] private List<AnimationClip> _clips = new();
    [SerializeField] private GameObject _targetRoot;
    [SerializeField] private Avatar _sourceAvatar;
    [SerializeField] private Avatar _targetAvatar;
    [SerializeField] private AnimationBoneMap _boneMap;
    [SerializeField] private UnmappedCurveMode _unmappedMode = UnmappedCurveMode.KeepOriginalPath;
    [SerializeField] private bool _overwriteOriginals;
    [SerializeField] private string _outputFolder = "";
    [SerializeField] private string _suffix = "_Remapped";

    private string _report = "";
    private Vector2 _scroll;
    private SerializedObject _serializedWindow;

    [MenuItem("Tools/Animation/Animation Path Remapper")]
    public static void Open() => GetWindow<AnimationPathRemapper>("Anim Path Remapper");

    private void OnEnable() => _serializedWindow = new SerializedObject(this);

    private void OnGUI()
    {
        _serializedWindow.Update();

        EditorGUILayout.HelpBox(
            "Renames bone paths only (no retargeting). Correct only if target bones share the source's rest " +
            "orientation, e.g. Mixamo-style rigs with different names. Run Preview first.", MessageType.Info);

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
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_targetRoot)),
            new GUIContent("Target Root", "The GameObject that holds the Animator the clips will play on. New paths are relative to it."));
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_sourceAvatar)),
            new GUIContent("Source Avatar (optional)", "Humanoid Avatar of the rig the clips were made FOR (the old rig). Only needed if the source bone names aren't Mixamo-style."));
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_targetAvatar)),
            new GUIContent("Target Avatar", "Humanoid Avatar of the NEW rig (Target Root). Empty = the Avatar on the Target Root's Animator. Without one, only Mixamo-style target names can be matched by bone identity."));
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_boneMap)),
                new GUIContent("Bone Map (optional)", "Manual overrides for names the automatic matching misses."));
            if (_boneMap == null && GUILayout.Button("Create", GUILayout.Width(60))) CreateBoneMap();
        }

        EditorGUILayout.Space();
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_unmappedMode)),
            new GUIContent("Unmapped Curves", "Keep: leave the old path (shows as missing in the Animation window). Drop: remove those curves."));
        EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_overwriteOriginals)),
            new GUIContent("Overwrite Originals", "Only for standalone .anim assets. Off = write remapped copies."));
        if (!_overwriteOriginals)
        {
            EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_outputFolder)),
                new GUIContent("Output Folder", "Project path like Assets/Animations/Remapped. Empty = next to each source clip."));
            EditorGUILayout.PropertyField(_serializedWindow.FindProperty(nameof(_suffix)), new GUIContent("Name Suffix"));
        }

        _serializedWindow.ApplyModifiedProperties();

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(_clips.Count == 0 || _targetRoot == null))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Preview Mapping")) Preview();
            if (GUILayout.Button("Remap Clips")) Remap();
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------ actions

    private void Preview()
    {
        var resolver = new BoneResolver(_targetRoot, _sourceAvatar, _targetAvatar, _boneMap);
        var sourcePaths = ValidClips()
            .SelectMany(c => AllBindings(c).Select(b => b.path))
            .Distinct()
            .OrderBy(p => p.Count(ch => ch == '/')).ThenBy(p => p);

        var sb = new StringBuilder();
        int unmapped = 0;
        foreach (var path in sourcePaths)
        {
            string target = resolver.ResolvePath(path, out string method);
            if (target == null) unmapped++;
            sb.AppendLine(target == null
                ? $"UNMAPPED   {path}"
                : $"{method,-9}  {LastSegment(path)}  ->  {(target.Length == 0 ? "(root)" : target)}");
        }

        _report = $"{unmapped} unmapped path(s)\n{resolver.DescribeTargetHumanoid()}{resolver.DescribeWarnings()}\n\n{sb}";
    }

    private void Remap()
    {
        var clips = ValidClips().ToList();
        if (_overwriteOriginals)
        {
            var notStandalone = clips.Where(c => !AssetDatabase.GetAssetPath(c).EndsWith(".anim")).ToList();
            if (notStandalone.Count > 0)
            {
                _report = "Can't overwrite clips that live inside a model/GLB (not .anim files):\n" +
                          string.Join("\n", notStandalone.Select(c => c.name)) + "\nTurn off Overwrite Originals to write copies.";
                return;
            }
            if (!EditorUtility.DisplayDialog("Overwrite clips?", $"Rewrite paths in {clips.Count} clip(s) in place? (Undo is supported)", "Overwrite", "Cancel"))
                return;
        }

        if (!_overwriteOriginals && !string.IsNullOrEmpty(_outputFolder) && !AssetDatabase.IsValidFolder(_outputFolder))
        {
            _report = $"Output folder '{_outputFolder}' doesn't exist (must be a project path like Assets/...).";
            return;
        }

        var resolver = new BoneResolver(_targetRoot, _sourceAvatar, _targetAvatar, _boneMap);
        var sb = new StringBuilder();

        try
        {
            AssetDatabase.StartAssetEditing();
            for (int i = 0; i < clips.Count; i++)
            {
                var source = clips[i];
                EditorUtility.DisplayProgressBar("Remapping clips", source.name, (float)i / clips.Count);

                AnimationClip destination;
                if (_overwriteOriginals)
                {
                    Undo.RegisterCompleteObjectUndo(source, "Remap animation paths");
                    destination = source;
                }
                else
                {
                    destination = Instantiate(source);
                    destination.name = source.name + _suffix;
                }

                var stats = RemapInto(source, destination, resolver, _unmappedMode == UnmappedCurveMode.Drop);

                if (!_overwriteOriginals)
                {
                    string folder = string.IsNullOrEmpty(_outputFolder)
                        ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(source)).Replace('\\', '/')
                        : _outputFolder;
                    string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{destination.name}.anim");
                    AssetDatabase.CreateAsset(destination, path);
                    sb.AppendLine($"{source.name} -> {path}  ({stats})");
                }
                else
                {
                    EditorUtility.SetDirty(source);
                    sb.AppendLine($"{source.name} overwritten  ({stats})");
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }

        _report = $"Done.{resolver.DescribeWarnings()}\n\n{sb}";
    }

    /// <summary>Reads every curve from source first, then rewrites destination (may be the same clip).</summary>
    private static string RemapInto(AnimationClip source, AnimationClip destination, BoneResolver resolver, bool dropUnmapped)
    {
        var floatBindings = AnimationUtility.GetCurveBindings(source);
        var floatCurves = floatBindings.Select(b => AnimationUtility.GetEditorCurve(source, b)).ToArray();
        var refBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);
        var refCurves = refBindings.Select(b => AnimationUtility.GetObjectReferenceCurve(source, b)).ToArray();
        var settings = AnimationUtility.GetAnimationClipSettings(source);
        var events = AnimationUtility.GetAnimationEvents(source);

        int mapped = 0, kept = 0, dropped = 0;

        bool TryMap(ref EditorCurveBinding binding)
        {
            string newPath = resolver.ResolvePath(binding.path, out _);
            if (newPath != null) { binding.path = newPath; mapped++; return true; }
            if (dropUnmapped) { dropped++; return false; }
            kept++;
            return true;
        }

        var newFloatBindings = new List<EditorCurveBinding>();
        var newFloatCurves = new List<AnimationCurve>();
        for (int i = 0; i < floatBindings.Length; i++)
        {
            var b = floatBindings[i];
            if (!TryMap(ref b)) continue;
            newFloatBindings.Add(b);
            newFloatCurves.Add(floatCurves[i]);
        }

        destination.ClearCurves();
        AnimationUtility.SetEditorCurves(destination, newFloatBindings.ToArray(), newFloatCurves.ToArray());

        for (int i = 0; i < refBindings.Length; i++)
        {
            var b = refBindings[i];
            if (TryMap(ref b)) AnimationUtility.SetObjectReferenceCurve(destination, b, refCurves[i]);
        }

        // ClearCurves shouldn't touch these, but restore them explicitly so loop settings and the AE_
        // animation events (combat timing!) can never be lost by a remap.
        AnimationUtility.SetAnimationClipSettings(destination, settings);
        AnimationUtility.SetAnimationEvents(destination, events);

        return $"{mapped} curves remapped, {kept} kept unmapped, {dropped} dropped";
    }

    // ------------------------------------------------------------------ helpers

    private IEnumerable<AnimationClip> ValidClips() => _clips.Where(c => c != null).Distinct();

    private static IEnumerable<EditorCurveBinding> AllBindings(AnimationClip clip) =>
        AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip));

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path.Substring(slash + 1);
    }

    private void CreateBoneMap()
    {
        string path = EditorUtility.SaveFilePanelInProject("Create Bone Map", "AnimationBoneMap", "asset", "Where to save the bone map");
        if (string.IsNullOrEmpty(path)) return;

        var map = CreateInstance<AnimationBoneMap>();
        AssetDatabase.CreateAsset(map, path);
        AssetDatabase.SaveAssets();
        _boneMap = map;
        Selection.activeObject = map;
    }

    /// <summary>Resolves source curve paths to target paths. Built once per Preview/Remap.</summary>
    private class BoneResolver
    {
        private readonly Transform _root;
        private readonly Dictionary<string, Transform> _byName = new();
        private readonly Dictionary<string, Transform> _byNormalizedName = new();
        private readonly HashSet<string> _duplicateNames = new();
        private readonly Dictionary<string, HumanBodyBones> _sourceHumanByName = new();
        private readonly Dictionary<HumanBodyBones, string> _targetBoneNameByHuman = new();
        private readonly Dictionary<string, string> _manual = new();
        private readonly Dictionary<string, string> _cache = new();
        private readonly Dictionary<string, string> _methodCache = new();

        private readonly string _targetAvatarName;
        private readonly List<string> _targetAvatarBonesMissing = new();

        public BoneResolver(GameObject targetRoot, Avatar sourceAvatar, Avatar targetAvatar, AnimationBoneMap boneMap)
        {
            _root = targetRoot.transform;

            foreach (var t in _root.GetComponentsInChildren<Transform>(true))
            {
                if (!_byName.TryAdd(t.name, t)) _duplicateNames.Add(t.name);
                _byNormalizedName.TryAdd(Normalize(t.name), t);
            }

            if (sourceAvatar != null && sourceAvatar.isHuman)
            {
                foreach (var hb in sourceAvatar.humanDescription.human)
                {
                    if (TryHumanBone(hb.humanName, out var bone)) _sourceHumanByName[Normalize(hb.boneName)] = bone;
                }
            }

            // Explicit field first: an Avatar created separately (e.g. duplicated out of the FBX) usually
            // isn't on the Target Root's Animator, and without it non-Mixamo target names (pelvis,
            // thigh_l, ...) can't be identified as humanoid bones.
            if (targetAvatar == null && targetRoot.TryGetComponent(out Animator targetAnimator)) targetAvatar = targetAnimator.avatar;
            if (targetAvatar != null && targetAvatar.isHuman)
            {
                _targetAvatarName = targetAvatar.name;
                foreach (var hb in targetAvatar.humanDescription.human)
                {
                    if (!TryHumanBone(hb.humanName, out var bone)) continue;
                    _targetBoneNameByHuman[bone] = hb.boneName;
                    if (!_byName.ContainsKey(hb.boneName)) _targetAvatarBonesMissing.Add(hb.boneName);
                }
            }

            if (boneMap != null)
            {
                foreach (var e in boneMap.Entries.Where(e => !string.IsNullOrEmpty(e.Source) && !string.IsNullOrEmpty(e.Target)))
                {
                    _manual[Normalize(e.Source)] = e.Target;
                }
            }
        }

        /// <summary>Target path for a source path, or null if unmapped. "" = the target root itself.</summary>
        public string ResolvePath(string sourcePath, out string method)
        {
            if (_cache.TryGetValue(sourcePath, out var cached))
            {
                method = _methodCache[sourcePath];
                return cached;
            }

            // Empty path = curves on the Animator's own GameObject (e.g. root motion): stays on the root.
            Transform target;
            if (sourcePath.Length == 0)
            {
                target = _root;
                method = "root";
            }
            else
            {
                target = ResolveBone(LastSegment(sourcePath), out method);
            }

            string result = target == null ? null : AnimationUtility.CalculateTransformPath(target, _root);
            _cache[sourcePath] = result;
            _methodCache[sourcePath] = method;
            return result;
        }

        private Transform ResolveBone(string boneName, out string method)
        {
            string normalized = Normalize(boneName);

            if (_manual.TryGetValue(normalized, out var manualTarget))
            {
                method = "manual";
                return Find(manualTarget);
            }

            if (_byName.TryGetValue(boneName, out var exact))
            {
                method = "same";
                return exact;
            }

            if (TrySourceHumanBone(normalized, out var human) && TryFindTargetHumanBone(human, out var humanTarget))
            {
                method = "humanoid";
                return humanTarget;
            }

            method = "name";
            return _byNormalizedName.TryGetValue(normalized, out var byNormalized) ? byNormalized : null;
        }

        private bool TrySourceHumanBone(string normalizedName, out HumanBodyBones bone) =>
            _sourceHumanByName.TryGetValue(normalizedName, out bone) || MixamoNames.ToHuman.TryGetValue(normalizedName, out bone);

        private bool TryFindTargetHumanBone(HumanBodyBones bone, out Transform target)
        {
            target = null;
            if (_targetBoneNameByHuman.TryGetValue(bone, out var name) && _byName.TryGetValue(name, out target)) return true;
            return MixamoNames.FromHuman.TryGetValue(bone, out var mixamo) && _byNormalizedName.TryGetValue(mixamo, out target);
        }

        private Transform Find(string name) =>
            _byName.TryGetValue(name, out var t) ? t : _byNormalizedName.TryGetValue(Normalize(name), out t) ? t : null;

        public string DescribeTargetHumanoid()
        {
            // Curve paths are relative to the Animator's GameObject, so picking a bone (e.g. "root") as the
            // Target Root silently drops it and everything above it from every path.
            string text = _root.GetComponent<Animator>() != null
                ? ""
                : "WARNING: Target Root has no Animator. Paths are written relative to it, so it must be the " +
                  "GameObject that holds the Animator (usually the model's top object), not a bone.\n";

            if (_targetAvatarName == null)
                return text + "Target humanoid: NONE (no humanoid Target Avatar) - only Mixamo-style target names match by bone identity.";

            text += $"Target humanoid: {_targetAvatarName} ({_targetBoneNameByHuman.Count} bones)";
            if (_targetAvatarBonesMissing.Count > 0)
                text += $"\nWARNING: {_targetAvatarBonesMissing.Count} avatar bone(s) not found under the Target Root " +
                        $"(wrong root or avatar?): {string.Join(", ", _targetAvatarBonesMissing.Take(8))}";
            return text;
        }

        public string DescribeWarnings() => _duplicateNames.Count == 0
            ? ""
            : $"\nWARNING: duplicate names under the target root (first one used): {string.Join(", ", _duplicateNames)}";

        private static bool TryHumanBone(string humanName, out HumanBodyBones bone)
        {
            // HumanTrait.BoneName is index-aligned with the HumanBodyBones enum ("Left Thumb Proximal" etc.).
            int index = System.Array.IndexOf(HumanTrait.BoneName, humanName);
            bone = (HumanBodyBones)index;
            return index >= 0 && index < (int)HumanBodyBones.LastBone;
        }
    }

    /// <summary>Lowercase, without "mixamorig" and separators: "mixamorig:LeftArm" / "mixamorigLeftArm" -> "leftarm".</summary>
    internal static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.ToLowerInvariant().Replace("mixamorig", ""))
        {
            if (c != ':' && c != '_' && c != ' ' && c != '.' && c != '-') sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Standard Mixamo bone names (normalized) <-> Unity humanoid bones.</summary>
    internal static class MixamoNames
    {
        public static readonly Dictionary<string, HumanBodyBones> ToHuman = Build();
        public static readonly Dictionary<HumanBodyBones, string> FromHuman =
            ToHuman.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

        private static Dictionary<string, HumanBodyBones> Build()
        {
            var map = new Dictionary<string, HumanBodyBones>
            {
                ["hips"] = HumanBodyBones.Hips,
                ["spine"] = HumanBodyBones.Spine,
                ["spine1"] = HumanBodyBones.Chest,
                ["spine2"] = HumanBodyBones.UpperChest,
                ["neck"] = HumanBodyBones.Neck,
                ["head"] = HumanBodyBones.Head,
            };

            foreach (var side in new[] { "Left", "Right" })
            {
                string s = side.ToLowerInvariant();
                HumanBodyBones B(string humanName) => (HumanBodyBones)System.Enum.Parse(typeof(HumanBodyBones), side + humanName);

                map[s + "shoulder"] = B("Shoulder");
                map[s + "arm"] = B("UpperArm");
                map[s + "forearm"] = B("LowerArm");
                map[s + "hand"] = B("Hand");
                map[s + "upleg"] = B("UpperLeg");
                map[s + "leg"] = B("LowerLeg");
                map[s + "foot"] = B("Foot");
                map[s + "toebase"] = B("Toes");

                var fingers = new[] { ("thumb", "Thumb"), ("index", "Index"), ("middle", "Middle"), ("ring", "Ring"), ("pinky", "Little") };
                var segments = new[] { "Proximal", "Intermediate", "Distal" };
                foreach (var (mixamoFinger, humanFinger) in fingers)
                {
                    for (int i = 0; i < segments.Length; i++)
                    {
                        map[$"{s}hand{mixamoFinger}{i + 1}"] = B(humanFinger + segments[i]);
                    }
                }
            }

            return map;
        }
    }
}
