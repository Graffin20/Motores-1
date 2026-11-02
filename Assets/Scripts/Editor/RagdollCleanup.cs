using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns Ragdoll Wizard output into kinematic limb colliders (e.g. Lillian's legs pushing ragdolls/props):
/// every Rigidbody's GameObject under the selected objects is moved to one layer; optionally the bodies are
/// made kinematic and joints removed. Also usable to just re-layer an enemy ragdoll (leave both options off).
///
/// Only GameObjects that carry a Rigidbody change layer: the Wizard puts each limb's collider on the same
/// object as its body, and other children (meshes, sword, sockets) keep their layer. The selected root is
/// skipped if it has a Rigidbody, so a character's root layer (what its CharacterController uses) is never
/// touched. Everything is undoable; works on scene objects and in Prefab Mode.
/// </summary>
public class RagdollCleanup : EditorWindow
{
    [SerializeField] private int _layer;
    [SerializeField] private bool _makeKinematic = true;
    [SerializeField] private bool _removeJoints = true;
    [SerializeField] private bool _includeInactive = true;

    [MenuItem("Tools/Physics/Ragdoll Cleanup")]
    public static void Open() => GetWindow<RagdollCleanup>("Ragdoll Cleanup");

    private void OnSelectionChange() => Repaint();

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Applies to every Rigidbody under the selected GameObject(s) (the selected root's own " +
                                "Rigidbody is skipped).", MessageType.Info);

        _layer = EditorGUILayout.LayerField("Layer", _layer);

        _makeKinematic = EditorGUILayout.Toggle(new GUIContent("Make Kinematic",
            "Is Kinematic on, interpolation off: the Animator moves the bones, physics never moves them."), _makeKinematic);
        _removeJoints = EditorGUILayout.Toggle(new GUIContent("Remove Joints",
            "Removes every Joint (CharacterJoint, ConfigurableJoint...) on the affected objects."), _removeJoints);
        _includeInactive = EditorGUILayout.Toggle("Include Inactive", _includeInactive);

        var roots = Selection.gameObjects;
        int count = roots.Sum(r => FindBodies(r).Length);
        EditorGUILayout.LabelField("Rigidbodies found", roots.Length == 0 ? "(nothing selected)" : count.ToString());

        using (new EditorGUI.DisabledScope(count == 0))
        {
            if (GUILayout.Button("Apply")) Apply(roots);
        }
    }

    private Rigidbody[] FindBodies(GameObject root) =>
        root.GetComponentsInChildren<Rigidbody>(_includeInactive)
            .Where(b => b.gameObject != root)
            .ToArray();

    private void Apply(GameObject[] roots)
    {
        Undo.SetCurrentGroupName("Ragdoll Cleanup");
        int group = Undo.GetCurrentGroup();
        int bodies = 0, joints = 0;

        foreach (var root in roots)
        {
            foreach (var body in FindBodies(root))
            {
                bodies++;
                var go = body.gameObject;

                if (go.layer != _layer)
                {
                    Undo.RecordObject(go, "Ragdoll Cleanup");
                    go.layer = _layer;
                }

                if (_makeKinematic)
                {
                    Undo.RecordObject(body, "Ragdoll Cleanup");
                    body.isKinematic = true;
                    body.interpolation = RigidbodyInterpolation.None;
                }

                if (_removeJoints)
                {
                    foreach (var joint in go.GetComponents<Joint>())
                    {
                        Undo.DestroyObjectImmediate(joint);
                        joints++;
                    }
                }

                EditorUtility.SetDirty(go);
            }
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"[RagdollCleanup] {bodies} rigidbodies moved to layer '{LayerMask.LayerToName(_layer)}'" +
                  (_makeKinematic ? ", made kinematic" : "") +
                  (_removeJoints ? $", {joints} joints removed" : "") + ".");
    }
}
