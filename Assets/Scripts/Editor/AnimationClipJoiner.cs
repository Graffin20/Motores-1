using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Joins two clips end to end into one new .anim (e.g. "Sword_Regular_A" + "Sword_Regular_A_Rec" ->
/// "Sword_Regular_A_Full"). Built for animation packs that ship an attack and its recovery as separate takes:
/// the FBX importer can only cut frame ranges out of ONE take, so it can't join them.
///
/// Works on Humanoid FBX clips: their curves are muscle/root curves, so the output is a Humanoid .anim
/// that retargets like the originals. The first clip's settings (Bake Into Pose, root offsets, loop) and
/// its animation events are copied to the output; the second clip's events are not.
///
/// Seam: the second clip's first key replaces the first clip's last key (they are the same frame of the
/// original animation). Root position (RootT) of the second clip is shifted to continue from where the
/// first one ends, in case the pack re-zeroed it when splitting; when it wasn't re-zeroed the shift is 0.
///
/// Re-running with the same output name overwrites the existing .anim in place, keeping its GUID, so
/// Animator states that already use it stay connected.
/// </summary>
public class AnimationClipJoiner : EditorWindow
{
    private const float Epsilon = 1e-4f;

    [SerializeField] private AnimationClip _first;
    [SerializeField] private AnimationClip _second;
    [SerializeField] private DefaultAsset _outputFolder;
    [SerializeField] private string _outputName;

    [MenuItem("Tools/Animation/Join Clips")]
    public static void Open() => GetWindow<AnimationClipJoiner>("Join Clips");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Plays First, then Second, as one clip. Expand the FBX in the Project window " +
                                "to drag its clips in. Settings and events come from First.", MessageType.Info);

        EditorGUI.BeginChangeCheck();
        _first = (AnimationClip)EditorGUILayout.ObjectField("First", _first, typeof(AnimationClip), false);
        if (EditorGUI.EndChangeCheck() && _first != null)
        {
            // Suggest defaults from the first clip; the user can still change them.
            _outputName = _first.name + "_Full";
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_first)).Replace('\\', '/');
            _outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folder);
        }

        _second = (AnimationClip)EditorGUILayout.ObjectField("Second", _second, typeof(AnimationClip), false);
        _outputFolder = (DefaultAsset)EditorGUILayout.ObjectField("Output Folder", _outputFolder, typeof(DefaultAsset), false);
        _outputName = EditorGUILayout.TextField("Output Name", _outputName);

        string folderPath = _outputFolder != null ? AssetDatabase.GetAssetPath(_outputFolder) : null;
        bool valid = _first != null && _second != null && _first != _second &&
                     !string.IsNullOrWhiteSpace(_outputName) && AssetDatabase.IsValidFolder(folderPath);

        using (new EditorGUI.DisabledScope(!valid))
        {
            if (GUILayout.Button("Join")) Run(_first, _second, $"{folderPath}/{_outputName.Trim()}.anim");
        }
    }

    private static void Run(AnimationClip first, AnimationClip second, string outputPath)
    {
        if (AnimationUtility.GetObjectReferenceCurveBindings(first).Length > 0 ||
            AnimationUtility.GetObjectReferenceCurveBindings(second).Length > 0)
            Debug.LogWarning("[AnimationClipJoiner] Object reference curves (sprite/material swaps) are not joined.");

        AnimationClip joined = Join(first, second);
        joined.name = Path.GetFileNameWithoutExtension(outputPath);

        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
        if (existing != null)
        {
            EditorUtility.CopySerialized(joined, existing); // keeps the GUID: Animator references survive
            EditorUtility.SetDirty(existing);
            DestroyImmediate(joined);
            joined = existing;
        }
        else
        {
            AssetDatabase.CreateAsset(joined, outputPath);
        }
        AssetDatabase.SaveAssets();

        string report = $"Wrote {outputPath}: {first.name} ({first.length:0.###}s) + {second.name} ({second.length:0.###}s) " +
                        $"= {joined.length:0.###}s, {AnimationUtility.GetAnimationEvents(joined).Length} event(s), " +
                        (joined.humanMotion ? "Humanoid." : "Generic.");
        Debug.Log("[AnimationClipJoiner] " + report, joined);
        EditorGUIUtility.PingObject(joined);
        EditorUtility.DisplayDialog("Join Clips", report, "OK");
    }

    private static AnimationClip Join(AnimationClip first, AnimationClip second)
    {
        float offset = first.length;
        float end = offset + second.length;
        var result = new AnimationClip { frameRate = first.frameRate };

        var bindings = AnimationUtility.GetCurveBindings(first)
                                       .Concat(AnimationUtility.GetCurveBindings(second))
                                       .Distinct();

        foreach (var binding in bindings)
        {
            AnimationCurve a = AnimationUtility.GetEditorCurve(first, binding);
            AnimationCurve b = AnimationUtility.GetEditorCurve(second, binding);
            var keys = new List<Keyframe>();

            if (a != null) keys.AddRange(a.keys);
            else keys.Add(new Keyframe(0f, b.Evaluate(0f))); // only animated in the second clip: hold its start

            if (b != null)
            {
                float shift = a != null && IsRootPosition(binding) ? a.Evaluate(offset) - b.Evaluate(0f) : 0f;
                foreach (var key in b.keys)
                {
                    var shifted = key;
                    shifted.time += offset;
                    shifted.value += shift;
                    // The seam frame exists in both clips: the second clip's copy wins.
                    while (keys.Count > 0 && keys[keys.Count - 1].time >= shifted.time - Epsilon) keys.RemoveAt(keys.Count - 1);
                    keys.Add(shifted);
                }
            }
            else
            {
                // Only animated in the first clip: hold its last value through the second.
                var last = keys[keys.Count - 1];
                last.outTangent = 0f;
                keys[keys.Count - 1] = last;
                keys.Add(new Keyframe(end, a.Evaluate(offset)));
            }

            AnimationUtility.SetEditorCurve(result, binding, new AnimationCurve(keys.ToArray()));
        }

        // Root-motion bake settings, mirror, loop, etc. from the first clip, stretched over the joined length.
        var settings = AnimationUtility.GetAnimationClipSettings(first);
        settings.startTime = 0f;
        settings.stopTime = end;
        AnimationUtility.SetAnimationClipSettings(result, settings);

        // The first clip starts at 0 in both, so its event times carry over unchanged.
        AnimationUtility.SetAnimationEvents(result, AnimationUtility.GetAnimationEvents(first));

        FixQuaternionFlips(result);
        return result;
    }

    /// <summary>
    /// Fixes quaternion sign flips in the selected .anim clips in place (curves elsewhere, events and
    /// settings untouched), for clips joined before the joiner did this itself.
    /// </summary>
    [MenuItem("Tools/Animation/Fix Quaternion Flips")]
    [MenuItem("Assets/Animation/Fix Quaternion Flips")]
    public static void FixSelectedClips()
    {
        var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets)
                             .Where(c => AssetDatabase.GetAssetPath(c).EndsWith(".anim")).ToArray();
        if (clips.Length == 0)
        {
            EditorUtility.DisplayDialog("Fix Quaternion Flips",
                "Select one or more .anim clips in the Project window (clips inside an FBX are read-only).", "OK");
            return;
        }

        var lines = new List<string>();
        foreach (var clip in clips)
        {
            Undo.RecordObject(clip, "Fix Quaternion Flips");
            int fixedGroups = FixQuaternionFlips(clip);
            if (fixedGroups > 0) EditorUtility.SetDirty(clip);
            lines.Add($"{clip.name}: {fixedGroups} rotation curve group(s) fixed");
        }
        AssetDatabase.SaveAssets();

        string report = string.Join("\n", lines);
        Debug.Log("[FixQuaternionFlips] " + report);
        EditorUtility.DisplayDialog("Fix Quaternion Flips", report, "OK");
    }

    /// <summary>
    /// q and -q are the same rotation, but curves interpolate each component separately: a jump from q to
    /// -q between two keys passes through a near-zero quaternion and plays as a full-speed spin. Clips
    /// stored with opposite signs (e.g. the two takes being joined) produce exactly that jump. Each
    /// quaternion (RootQ, IK goal Q, m_LocalRotation) is resampled per frame and kept in the same
    /// hemisphere as the previous frame. Only groups that actually flip are rewritten. Returns their count.
    /// </summary>
    public static int FixQuaternionFlips(AnimationClip clip)
    {
        const string Axes = "xyzw";
        int frames = Mathf.Max(1, Mathf.RoundToInt(clip.length * clip.frameRate));
        int fixedGroups = 0;

        var groups = AnimationUtility.GetCurveBindings(clip)
                                     .Where(IsQuaternionComponent)
                                     .GroupBy(b => (b.path, b.type, Prefix(b.propertyName)));

        foreach (var group in groups)
        {
            var byAxis = group.ToDictionary(b => b.propertyName[b.propertyName.Length - 1]);
            if (!Axes.All(byAxis.ContainsKey)) continue;
            var curves = Axes.Select(axis => AnimationUtility.GetEditorCurve(clip, byAxis[axis])).ToArray();

            var times = new float[frames + 1];
            var samples = new Vector4[frames + 1];
            bool flipped = false;
            for (int i = 0; i <= frames; i++)
            {
                times[i] = Mathf.Min(i / clip.frameRate, clip.length);
                var q = new Vector4(curves[0].Evaluate(times[i]), curves[1].Evaluate(times[i]),
                                    curves[2].Evaluate(times[i]), curves[3].Evaluate(times[i]));
                if (i > 0 && Vector4.Dot(q, samples[i - 1]) < 0f)
                {
                    q = -q;
                    flipped = true;
                }
                samples[i] = q;
            }
            if (!flipped) continue;

            for (int axis = 0; axis < 4; axis++)
            {
                var curve = new AnimationCurve(samples.Select((q, i) => new Keyframe(times[i], q[axis])).ToArray());
                for (int k = 0; k < curve.length; k++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                    AnimationUtility.SetKeyRightTangentMode(curve, k, AnimationUtility.TangentMode.ClampedAuto);
                }
                AnimationUtility.SetEditorCurve(clip, byAxis[Axes[axis]], curve);
            }
            fixedGroups++;
        }
        return fixedGroups;
    }

    private static bool IsQuaternionComponent(EditorCurveBinding binding)
    {
        string name = binding.propertyName;
        if (name.Length < 3 || name[name.Length - 2] != '.' || "xyzw".IndexOf(name[name.Length - 1]) < 0) return false;
        string prefix = Prefix(name);
        return binding.type == typeof(Animator) ? prefix.EndsWith("Q") : prefix == "m_LocalRotation";
    }

    private static string Prefix(string propertyName) => propertyName.Substring(0, propertyName.Length - 2);

    private static bool IsRootPosition(EditorCurveBinding binding) =>
        binding.type == typeof(Animator) && binding.propertyName.StartsWith("RootT.");
}
