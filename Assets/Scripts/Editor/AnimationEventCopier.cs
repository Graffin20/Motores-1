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
///
/// Single Clip mode: pick one source and one target clip directly (names don't need to match). If the
/// lengths are within the tolerance the events keep their exact time in seconds (same frame); if they
/// differ more, each event is moved to the same percentage of the target clip, since the motion was
/// most likely retimed (a faster/slower version of the same swing) rather than trimmed.
/// </summary>
public class AnimationEventCopier : EditorWindow
{
    private const float LengthMismatchTolerance = 0.05f;

    private enum CopyMode { SingleClip, Folder }

    [SerializeField] private CopyMode _mode = CopyMode.SingleClip;
    [SerializeField] private AnimationClip _sourceClip;
    [SerializeField] private AnimationClip _targetClip;
    [SerializeField] private float _rescaleTolerance = LengthMismatchTolerance;
    [SerializeField] private DefaultAsset _sourceFolder;
    [SerializeField] private DefaultAsset _targetFolder;
    [SerializeField] private bool _replaceExisting = true;

    private string _report = "";
    private Vector2 _scroll;

    [MenuItem("Tools/Animation/Animation Event Copier")]
    public static void Open() => GetWindow<AnimationEventCopier>("Anim Event Copier");

    private void OnGUI()
    {
        _mode = (CopyMode)GUILayout.Toolbar((int)_mode, new[] { "Single Clip", "Folder" });
        EditorGUILayout.Space();

        if (_mode == CopyMode.SingleClip) DrawSingleClipGUI();
        else DrawFolderGUI();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        EditorGUILayout.TextArea(_report, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    private void DrawSingleClipGUI()
    {
        EditorGUILayout.HelpBox("Copies the events of Source Clip into Target Clip. Lengths within the tolerance keep " +
                                "the same time in seconds; bigger differences place each event at the same percentage " +
                                "of the clip. Target can be an .anim or a clip inside an FBX. Run Preview first.", MessageType.Info);

        _sourceClip = (AnimationClip)EditorGUILayout.ObjectField(
            new GUIContent("Source Clip", "Clip that already has the events."), _sourceClip, typeof(AnimationClip), false);
        _targetClip = (AnimationClip)EditorGUILayout.ObjectField(
            new GUIContent("Target Clip", "Clip to receive them."), _targetClip, typeof(AnimationClip), false);
        _rescaleTolerance = EditorGUILayout.Slider(
            new GUIContent("Rescale Tolerance", "Length difference (fraction of the source length) above which events are " +
                                                "placed by percentage instead of seconds. 0 = always by percentage."),
            _rescaleTolerance, 0f, 0.5f);
        _replaceExisting = EditorGUILayout.Toggle(
            new GUIContent("Replace Existing Events", "On: target events become exactly the source's (safe to re-run). Off: add to the existing ones."), _replaceExisting);

        string error = null;
        if (_sourceClip == null || _targetClip == null) error = "Assign both clips.";
        else if (_sourceClip == _targetClip) error = "Source and Target are the same clip.";
        else if (_sourceClip.length <= 0f || _targetClip.length <= 0f) error = "A clip has zero length.";
        if (error != null) EditorGUILayout.HelpBox(error, MessageType.Warning);

        using (new EditorGUI.DisabledScope(error != null))
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Preview")) RunSingle(apply: false);
            if (GUILayout.Button("Copy Events")) RunSingle(apply: true);
        }
    }

    private void DrawFolderGUI()
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
    }

    // ------------------------------------------------------------------ single clip

    private void RunSingle(bool apply)
    {
        var sb = new StringBuilder();
        string targetPath = AssetDatabase.GetAssetPath(_targetClip);
        var sourceEvents = AnimationUtility.GetAnimationEvents(_sourceClip);
        float srcLen = _sourceClip.length, dstLen = _targetClip.length;

        // Seconds when the clips are (nearly) the same length: exact frames survive a re-export.
        // Percentage when they differ more: the same motion played faster/slower keeps each event on its pose.
        bool rescale = Mathf.Abs(dstLen - srcLen) > srcLen * _rescaleTolerance;
        float TargetSeconds(float t) => Mathf.Clamp(rescale ? t / srcLen * dstLen : t, 0f, dstLen);

        sb.AppendLine($"{_sourceClip.name} ({srcLen:0.###}s) -> {_targetClip.name} ({dstLen:0.###}s)");
        sb.AppendLine(rescale
            ? $"Lengths differ by {Mathf.Abs(dstLen - srcLen) / srcLen:P0}: events placed by PERCENTAGE."
            : "Lengths match within tolerance: events keep their time in SECONDS.");
        sb.AppendLine();

        if (sourceEvents.Length == 0) sb.AppendLine("Source clip has no events.");
        foreach (var e in sourceEvents)
        {
            float t = TargetSeconds(e.time);
            string clamped = !rescale && e.time > dstLen ? "   CLAMPED to clip end" : "";
            sb.AppendLine($"  {e.functionName,-24} {e.time:0.###}s ({e.time / srcLen:P1})  ->  {t:0.###}s ({t / dstLen:P1}){clamped}");
        }

        if (apply)
        {
            if (AssetImporter.GetAtPath(targetPath) is ModelImporter importer)
            {
                // Clip inside a model: events live in the import settings, in normalized (0..1) time.
                var infos = importer.clipAnimations;
                if (infos.Length == 0) infos = importer.defaultClipAnimations;
                var info = infos.FirstOrDefault(i => i.name == _targetClip.name);
                if (info == null)
                {
                    _report = $"Could not find clip '{_targetClip.name}' in the import settings of {targetPath}.";
                    return;
                }

                var existing = _replaceExisting ? new AnimationEvent[0] : info.events;
                var copied = sourceEvents.Select(e => Copy(e, TargetSeconds(e.time) / dstLen));
                info.events = existing.Concat(copied).OrderBy(e => e.time).ToArray();
                importer.clipAnimations = infos;
                importer.SaveAndReimport();
            }
            else
            {
                Undo.RegisterCompleteObjectUndo(_targetClip, "Copy animation events");
                var existing = _replaceExisting ? new AnimationEvent[0] : AnimationUtility.GetAnimationEvents(_targetClip);
                var copied = sourceEvents.Select(e => Copy(e, TargetSeconds(e.time)));
                AnimationUtility.SetAnimationEvents(_targetClip, existing.Concat(copied).OrderBy(e => e.time).ToArray());
                EditorUtility.SetDirty(_targetClip);
                AssetDatabase.SaveAssets();
            }
        }

        _report = $"{(apply ? "Copied" : "Preview:")} {sourceEvents.Length} event(s).\n\n{sb}";
    }

    // ------------------------------------------------------------------ folder run

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
