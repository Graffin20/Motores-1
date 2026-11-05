using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reusable manual bone-name overrides for the Animation Path Remapper (Tools > Animation > Animation
/// Path Remapper). Only needed for names the automatic matching can't resolve, e.g. mixamorigHips ->
/// "Hip". Matching ignores case, "mixamorig" prefixes and separators (":", "_", " ", ".", "-").
/// </summary>
[CreateAssetMenu(fileName = "AnimationBoneMap", menuName = "Animation/Bone Name Map")]
public class AnimationBoneMap : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("Bone name as it appears in the source clips (last segment of the curve path), e.g. mixamorigHips.")]
        public string Source;
        [Tooltip("Name of the matching transform under the target root, e.g. Hip.")]
        public string Target;
    }

    public List<Entry> Entries = new();
}
