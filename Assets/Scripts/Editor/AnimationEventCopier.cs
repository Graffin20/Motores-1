using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Copies animation events (the AE_ combat timing) from the clips in one folder to the clips with the
/// same name in another, e.g. from the old Mesh2Motion GLB .anim clips to the same animations
/// re-downloaded as FBX for the new rig.
///
/// Events are placed by NORMALIZED time (fraction of the clip), so a re-export with a different frame
/// rate or a slightly different length keeps each event at the same point in the motion. Clips whose
/// lengths differ noticeably are flagged: check those events by hand.
///
/// Targets: standalone .anim files (AnimationUtility) and clips inside models, written to the model's
/// import settings (ModelImporter.clipAnimations), which is where FBX clip events live and what the
/// Inspector's Events foldout edits. Reimporting the FBX therefore keeps them.
///
/// Matching (case and separators ignored, Blender "Armature|" prefixes stripped): clip name to clip
/// name, or, for files holding a single clip, the file name.
/// </summary>
public class AnimationEventCopier : EditorWindow
{
    private const float LengthMismatchTolerance = 0.05f;

    [SerializeField] private DefaultAsset _sourceFolder;
    [SerializeField] private DefaultAsset _targetFolder;
    [SerializeField] private bool _replaceExisting = true;

    private string _report = "";
    private Vector2 _scroll;

    [MenuItem("Tools/Animation/Animation Event Copier")]
    public static void Open() => GetWindow<AnimationEventCopier>("Anim Event Copier");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Copies events from each clip in Source to the clip with the same name in Target " +
                                "(subfolders included). Run Preview first.", MessageType.Info);

        _sourceFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Source Folder", "Clips that already have the events (e.g. the old .anim files)."), _sourceFolder, typeof(DefaultAsset), false);
        _targetFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("Target Folder", "Clips to receive them (e.g. the new FBX files)."), _targetFolder, typeof(DefaultAsset), false);
        _replaceExisting = EditorGUILayout.Toggle(
            new GUIContent("Replace Existing Events", "On: target events become exactly the source's (safe to re-run). Off: add to the existing ones."), _replaceExisting);

        bool valid = IsFolder(_sourceFolder) && IsFolder(_targetFolder);
        if (!valid) EditorGUILayout.HelpBox("Drag a folder from the Project window into both fields.", MessageType.Warning);

        using (new EditorGUI.DisabledScope(!valid))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Preview")) Run(apply: false);
            if (GUILayout.Button("Copy Events")) Run(apply: true);
        }

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------ run

    private class SourceClip
    {
        public AnimationClip Clip;
        public string AssetPath;
        public AnimationEvent[] Events;
    }

    private void Run(bool apply)
    {
        var sb = new StringBuilder();
        var sources = CollectSources(AssetDatabase.GetAssetPath(_sourceFolder), sb);
        var matched = new HashSet<SourceClip>();
        int updatedClips = 0, eventsCopied = 0;

        foreach (string path in AssetPathsIn(AssetDatabase.GetAssetPath(_targetFolder)))
        {
            if (path.EndsWith(".anim"))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null || !TryMatch(sources, clip.name, Path.GetFileNameWithoutExtension(path), true, out var source)) continue;

                matched.Add(source);
                Describe(sb, source, path, clip.name, clip.length);
                if (apply)
                {
                    Undo.RegisterCompleteObjectUndo(clip, "Copy animation events");
                    var existing = _replaceExisting ? new AnimationEvent[0] : AnimationUtility.GetAnimationEvents(clip);
                    var copied = source.Events.Select(e => Copy(e, e.time / source.Clip.length * clip.length));
                    AnimationUtility.SetAnimationEvents(clip, existing.Concat(copied).OrderBy(e => e.time).ToArray());
                    EditorUtility.SetDirty(clip);
                }
                updatedClips++;
                eventsCopied += source.Events.Length;
            }
            else if (AssetImporter.GetAtPath(path) is ModelImporter importer)
            {
                var infos = importer.clipAnimations;
                if (infos.Length == 0) infos = importer.defaultClipAnimations; // never customized: start from the defaults
                var imported = AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>().ToList();
                bool changed = false;

                foreach (var info in infos)
                {
                    if (!TryMatch(sources, info.name, Path.GetFileNameWithoutExtension(path), infos.Length == 1, out var source)) continue;

                    matched.Add(source);
                    var importedClip = imported.FirstOrDefault(c => c.name == info.name);
                    Describe(sb, source, path, info.name, importedClip != null ? importedClip.length : -1f);

                    // ModelImporter event times are normalized (0..1), as shown in the clip's Events foldout.
                    var existing = _replaceExisting ? new AnimationEvent[0] : info.events;
                    var copied = source.Events.Select(e => Copy(e, Mathf.Clamp01(e.time / source.Clip.length)));
                    info.events = existing.Concat(copied).OrderBy(e => e.time).ToArray();
                    changed = true;
                    updatedClips++;
                    eventsCopied += source.Events.Length;
                }

                if (apply && changed)
                {
                    importer.clipAnimations = infos;
                    importer.SaveAndReimport();
                }
            }
        }

        var unmatched = sources.Values.Distinct().Where(s => s.Events.Length > 0 && !matched.Contains(s)).ToList();
        if (unmatched.Count > 0)
        {
            sb.AppendLine().AppendLine("Source clips WITH events but no target match:");
            foreach (var s in unmatched) sb.AppendLine($"  {s.Clip.name}  ({s.Events.Length} events)  {s.AssetPath}");
        }

        if (apply) AssetDatabase.SaveAssets();
        _report = $"{(apply ? "Copied" : "Preview:")} {eventsCopied} event(s) into {updatedClips} clip(s).\n\n{sb}";
    }

    private Dictionary<string, SourceClip> CollectSources(string folder, StringBuilder sb)
    {
        var byKey = new Dictionary<string, SourceClip>();
        var ambiguous = new HashSet<string>();

        void AddKey(string key, SourceClip source)
        {
            if (byKey.TryGetValue(key, out var other) && other != source) ambiguous.Add(key);
            else byKey[key] = source;
        }

        foreach (string path in AssetPathsIn(folder))
        {
            var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__")).ToList();
            foreach (var clip in clips)
            {
                var source = new SourceClip { Clip = clip, AssetPath = path, Events = AnimationUtility.GetAnimationEvents(clip) };
                AddKey(Normalize(clip.name), source);
                if (clips.Count == 1) AddKey(Normalize(Path.GetFileNameWithoutExtension(path)), source);
            }
        }

        // Two different source clips claiming one name: copying either could be wrong, so copy neither.
        foreach (var key in ambiguous)
        {
            sb.AppendLine($"SKIPPED name '{key}': more than one source clip has it.");
            byKey.Remove(key);
        }
        return byKey;
    }

    private static bool TryMatch(Dictionary<string, SourceClip> sources, string clipName, string fileName, bool allowFileName, out SourceClip source) =>
        sources.TryGetValue(Normalize(clipName), out source) ||
        (allowFileName && sources.TryGetValue(Normalize(fileName), out source));

    private static void Describe(StringBuilder sb, SourceClip source, string targetPath, string targetClip, float targetLength)
    {
        string warning = "";
        if (targetLength > 0f && Mathf.Abs(targetLength - source.Clip.length) > source.Clip.length * LengthMismatchTolerance)
            warning = $"   CHECK TIMING: length {source.Clip.length:0.##}s -> {targetLength:0.##}s";
        else if (targetLength < 0f)
            warning = "   (new clip, length unknown until import)";

        string events = source.Events.Length == 0 ? "no events" : string.Join(", ", source.Events.Select(e => e.functionName));
        sb.AppendLine($"{source.Clip.name} -> {Path.GetFileName(targetPath)} [{targetClip}]: {events}{warning}");
    }

    // ------------------------------------------------------------------ helpers

    private static AnimationEvent Copy(AnimationEvent e, float time) => new()
    {
        time = time,
        functionName = e.functionName,
        floatParameter = e.floatParameter,
        intParameter = e.intParameter,
        stringParameter = e.stringParameter,
        objectReferenceParameter = e.objectReferenceParameter,
        messageOptions = e.messageOptions,
    };

    private static IEnumerable<string> AssetPathsIn(string folder) =>
        AssetDatabase.FindAssets("t:AnimationClip", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Distinct();

    /// <summary>"Armature|Walk_Forward" / "walk forward" / "WalkForward" -> "walkforward".</summary>
    private static string Normalize(string name)
    {
        int bar = name.LastIndexOf('|');
        if (bar >= 0) name = name.Substring(bar + 1);
        return new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static bool IsFolder(DefaultAsset asset) => asset != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(asset));
}
