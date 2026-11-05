using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Renames the animation clip inside each selected model file (or every model in the selected folders)
/// to the file's name, e.g. the "mixamo.com" take in "left strafe walk.fbx" becomes "left strafe walk".
///
/// Written to the model's import settings (ModelImporter.clipAnimations), the same place the Inspector's
/// Animation tab writes the clip name, so the name survives reimports and the clips stay inside the FBX
/// (still Humanoid, import settings like Loop Time stay editable).
///
/// Only files holding exactly one clip are renamed: with several clips the file name can't tell them
/// apart, so those are skipped and reported.
/// </summary>
public static class RenameClipsToFileName
{
    [MenuItem("Tools/Animation/Rename Clips To File Name")]
    [MenuItem("Assets/Animation/Rename Clips To File Name")]
    public static void Run()
    {
        var modelPaths = CollectModelPaths();
        if (modelPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Rename Clips To File Name",
                "Select one or more folders or model files (.fbx) in the Project window first.", "OK");
            return;
        }

        int renamed = 0, unchanged = 0;
        var skipped = new List<string>();

        foreach (var path in modelPaths)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;

            var clips = importer.clipAnimations;
            if (clips.Length == 0) clips = importer.defaultClipAnimations; // never customized: start from the defaults

            if (clips.Length != 1)
            {
                skipped.Add($"{path} ({clips.Length} clips)");
                continue;
            }

            string fileName = Path.GetFileNameWithoutExtension(path);
            if (clips[0].name == fileName && importer.clipAnimations.Length == 1)
            {
                unchanged++;
                continue;
            }

            clips[0].name = fileName;
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            renamed++;
        }

        string report = $"Renamed {renamed} clip(s), {unchanged} already named correctly.";
        if (skipped.Count > 0)
            report += "\nSkipped (not exactly one clip):\n  " + string.Join("\n  ", skipped);
        Debug.Log("[RenameClipsToFileName] " + report);
        EditorUtility.DisplayDialog("Rename Clips To File Name", report, "OK");
    }

    /// <summary>
    /// Turns Loop Time on for every clip in the selected model files / folders. A separate command
    /// rather than part of the rename because not every clip should loop (jump, one-shot turns,
    /// attacks): select only the ones that should.
    /// </summary>
    [MenuItem("Tools/Animation/Set Loop Time On Clips")]
    [MenuItem("Assets/Animation/Set Loop Time On Clips")]
    public static void SetLoopTime()
    {
        var modelPaths = CollectModelPaths();
        if (modelPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Set Loop Time On Clips",
                "Select one or more folders or model files (.fbx) in the Project window first.", "OK");
            return;
        }

        int changed = 0, unchanged = 0;
        foreach (var path in modelPaths)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;

            var clips = importer.clipAnimations;
            if (clips.Length == 0) clips = importer.defaultClipAnimations; // never customized: start from the defaults
            if (clips.Length == 0) continue; // model without animation

            if (importer.clipAnimations.Length > 0 && clips.All(c => c.loopTime))
            {
                unchanged++;
                continue;
            }

            foreach (var clip in clips) clip.loopTime = true;
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            changed++;
        }

        string report = $"Loop Time turned on in {changed} file(s), {unchanged} already looping.";
        Debug.Log("[SetLoopTime] " + report);
        EditorUtility.DisplayDialog("Set Loop Time On Clips", report, "OK");
    }

    /// <summary>
    /// Ticks "Bake Into Pose" for Root Transform Rotation, Position Y and Position XZ on every clip in the
    /// selected model files / folders. Our movement never uses root motion (ThirdPersonController and
    /// CharacterController.Move own position and facing), so unbaked root motion is simply discarded: the
    /// hips lose their yaw (whole body twists) and the step bounce disappears, while the Inspector preview,
    /// which applies root motion, still looks right.
    /// Rotation is based on Original with a 180 offset because the source rig's root bone faces backwards;
    /// Y on Original; XZ on Center of Mass (Unity's default).
    /// </summary>
    [MenuItem("Tools/Animation/Bake Root Motion Into Pose")]
    [MenuItem("Assets/Animation/Bake Root Motion Into Pose")]
    public static void BakeRootMotionIntoPose()
    {
        const float RotationOffset = 180f;

        var modelPaths = CollectModelPaths();
        if (modelPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Bake Root Motion Into Pose",
                "Select one or more folders or model files (.fbx) in the Project window first.", "OK");
            return;
        }

        int files = 0, clipCount = 0;
        foreach (var path in modelPaths)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;

            var clips = importer.clipAnimations;
            if (clips.Length == 0) clips = importer.defaultClipAnimations; // never customized: start from the defaults
            if (clips.Length == 0) continue; // model without animation

            foreach (var clip in clips)
            {
                clip.lockRootRotation = true;
                clip.keepOriginalOrientation = true;
                clip.rotationOffset = RotationOffset;

                clip.lockRootHeightY = true;
                clip.keepOriginalPositionY = true;
                clip.heightFromFeet = false;

                clip.lockRootPositionXZ = true;
                clip.keepOriginalPositionXZ = false; // Center of Mass
            }

            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            files++;
            clipCount += clips.Length;
        }

        string report = $"Baked root motion into pose on {clipCount} clip(s) in {files} file(s) " +
                        $"(rotation: Original, offset {RotationOffset}; Y: Original; XZ: Center of Mass).";
        Debug.Log("[BakeRootMotionIntoPose] " + report);
        EditorUtility.DisplayDialog("Bake Root Motion Into Pose", report, "OK");
    }

    private static List<string> CollectModelPaths()
    {
        var paths = new HashSet<string>();
        foreach (var obj in Selection.GetFiltered<Object>(SelectionMode.Assets))
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { path }))
                    paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            else if (AssetImporter.GetAtPath(path) is ModelImporter)
            {
                paths.Add(path);
            }
        }
        return paths.OrderBy(p => p).ToList();
    }
}
