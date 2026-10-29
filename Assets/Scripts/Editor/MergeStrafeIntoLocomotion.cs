using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// One-shot tool: folds a separate Strafe blend-tree state into the existing locomotion state, so every
/// transition already wired to "Idle Walk Run Blend" (attacks out, attack/roll/hit/fall returns in) works
/// for strafing too, instead of duplicating all of them onto a second state.
///
/// Result: the locomotion state's motion becomes a 1D parent blend tree on the float "StrafeBlend":
///   0 = the original free Idle/Walk/Run tree, 1 = the strafe 2D tree.
/// ThirdPersonController drives StrafeBlend toward 1 while locked on and 0 otherwise.
///
/// Usage: select the AnimatorController in the Project window (or leave nothing selected to use
/// DefaultControllerPath), then Tools > Animator > Merge Strafe State Into Locomotion. A backup copy
/// of the controller is saved next to it first.
/// </summary>
public static class MergeStrafeIntoLocomotion
{
    private const string DefaultControllerPath = "Assets/Assets/Animations/Animators/PlayerAnimator.controller";
    private const string LocomotionStateName = "Idle Walk Run Blend";
    private const string StrafeBlendParameter = "StrafeBlend";

    [MenuItem("Tools/Animator/Merge Strafe State Into Locomotion")]
    public static void Run()
    {
        var controller = Selection.activeObject as AnimatorController
                         ?? AssetDatabase.LoadAssetAtPath<AnimatorController>(DefaultControllerPath);
        if (controller == null)
        {
            Debug.LogError($"[StrafeMerge] Select an AnimatorController, or check DefaultControllerPath ({DefaultControllerPath}).");
            return;
        }

        AnimatorStateMachine root = controller.layers[0].stateMachine;
        List<(AnimatorState state, AnimatorStateMachine owner)> allStates = CollectStates(root);

        var locomotion = allStates.FirstOrDefault(s => s.state.name == LocomotionStateName);
        if (locomotion.state == null)
        {
            Debug.LogError($"[StrafeMerge] No state named '{LocomotionStateName}' in the base layer of '{controller.name}'.");
            return;
        }

        if (locomotion.state.motion is not BlendTree freeTree)
        {
            Debug.LogError($"[StrafeMerge] '{LocomotionStateName}' doesn't have a BlendTree motion.");
            return;
        }

        if (freeTree.blendParameter == StrafeBlendParameter)
        {
            Debug.LogWarning($"[StrafeMerge] '{LocomotionStateName}' is already merged (its tree blends on {StrafeBlendParameter}). Nothing to do.");
            return;
        }

        // The strafe state = the one other base-layer state whose motion is a 2D blend tree.
        var candidates = allStates
            .Where(s => s.state != locomotion.state && s.state.motion is BlendTree bt && Is2D(bt.blendType))
            .ToList();
        if (candidates.Count != 1)
        {
            string found = candidates.Count == 0 ? "none" : string.Join(", ", candidates.Select(c => $"'{c.state.name}'"));
            Debug.LogError($"[StrafeMerge] Expected exactly one other base-layer state with a 2D blend tree, found {found}. " +
                           "Rename/remove the extras and run again.");
            return;
        }

        var strafe = candidates[0];
        var strafeTree = (BlendTree)strafe.state.motion;

        if (!UsesParameter(strafeTree, "StrafeX") || !UsesParameter(strafeTree, "StrafeY"))
        {
            Debug.LogWarning($"[StrafeMerge] Strafe tree on '{strafe.state.name}' blends on '{strafeTree.blendParameter}'/'{strafeTree.blendParameterY}', " +
                             "not StrafeX/StrafeY — ThirdPersonController only drives StrafeX/StrafeY. Merging anyway.");
        }

        if (!EditorUtility.DisplayDialog("Merge Strafe State Into Locomotion",
                $"Fold '{strafe.state.name}' into '{LocomotionStateName}' on '{controller.name}'?\n\n" +
                $"- '{LocomotionStateName}' gets a parent 1D blend tree on '{StrafeBlendParameter}' (0 = free, 1 = strafe)\n" +
                $"- '{strafe.state.name}' and every transition into/out of it are removed\n" +
                "- A backup copy of the controller is saved first",
                "Merge", "Cancel"))
        {
            return;
        }

        // Flush pending edits (e.g. a just-created strafe state) to disk so the backup contains them.
        AssetDatabase.SaveAssets();
        string path = AssetDatabase.GetAssetPath(controller);
        string backupPath = AssetDatabase.GenerateUniqueAssetPath(path.Replace(".controller", "_BeforeStrafeMerge.controller"));
        if (!AssetDatabase.CopyAsset(path, backupPath))
        {
            Debug.LogError($"[StrafeMerge] Couldn't create backup at '{backupPath}'. Aborting without changes.");
            return;
        }

        if (controller.parameters.All(p => p.name != StrafeBlendParameter))
        {
            controller.AddParameter(StrafeBlendParameter, AnimatorControllerParameterType.Float);
        }

        var parentTree = new BlendTree
        {
            name = "Locomotion (Free/Strafe)",
            blendType = BlendTreeType.Simple1D,
            blendParameter = StrafeBlendParameter,
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy, // same as blend trees Unity creates itself
        };
        AssetDatabase.AddObjectToAsset(parentTree, controller);
        parentTree.AddChild(freeTree, 0f);
        parentTree.AddChild(strafeTree, 1f);

        locomotion.state.motion = parentTree;

        // Detach the strafe tree from its state BEFORE removing the state, so removing the state can't
        // take the (now shared) blend tree sub-asset with it.
        strafe.state.motion = null;

        int removed = RemoveTransitionsInto(root, strafe.state);
        strafe.owner.RemoveState(strafe.state);

        EditorUtility.SetDirty(parentTree);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log($"[StrafeMerge] Done. '{strafe.state.name}' merged into '{LocomotionStateName}' ({removed} transition(s) into it removed). " +
                  $"Backup: {backupPath}. The 'IsStrafing' bool is no longer needed by the graph; ThirdPersonController keeps setting it harmlessly.");
    }

    private static bool Is2D(BlendTreeType type) =>
        type == BlendTreeType.SimpleDirectional2D ||
        type == BlendTreeType.FreeformDirectional2D ||
        type == BlendTreeType.FreeformCartesian2D;

    private static bool UsesParameter(BlendTree tree, string parameter) =>
        tree.blendParameter == parameter || tree.blendParameterY == parameter;

    private static List<(AnimatorState, AnimatorStateMachine)> CollectStates(AnimatorStateMachine machine)
    {
        var result = new List<(AnimatorState, AnimatorStateMachine)>();
        foreach (var child in machine.states) result.Add((child.state, machine));
        foreach (var sub in machine.stateMachines) result.AddRange(CollectStates(sub.stateMachine));
        return result;
    }

    /// <summary>Removes every transition (state, Any State, Entry) targeting the given state, recursively.
    /// Transitions FROM it go away with the state itself.</summary>
    private static int RemoveTransitionsInto(AnimatorStateMachine machine, AnimatorState target)
    {
        int removed = 0;

        foreach (var child in machine.states)
        {
            foreach (var t in child.state.transitions.Where(t => t.destinationState == target).ToArray())
            {
                child.state.RemoveTransition(t);
                removed++;
            }
        }

        foreach (var t in machine.anyStateTransitions.Where(t => t.destinationState == target).ToArray())
        {
            machine.RemoveAnyStateTransition(t);
            removed++;
        }

        foreach (var t in machine.entryTransitions.Where(t => t.destinationState == target).ToArray())
        {
            machine.RemoveEntryTransition(t);
            removed++;
        }

        foreach (var sub in machine.stateMachines) removed += RemoveTransitionsInto(sub.stateMachine, target);
        return removed;
    }
}
