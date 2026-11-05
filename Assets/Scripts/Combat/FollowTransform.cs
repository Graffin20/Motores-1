using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Copies a target's world position and rotation every LateUpdate (after animation), without being its
    /// child. Used for FX that must ride a moving bone/weapon: parenting would also apply the hierarchy's
    /// scale (Lillian's bones are 100x), shrinking or blowing up the effect. When the target is destroyed the
    /// object simply stays where it was.
    /// </summary>
    public class FollowTransform : MonoBehaviour
    {
        public Transform Target;

        private void LateUpdate()
        {
            if (Target == null) return;
            transform.SetPositionAndRotation(Target.position, Target.rotation);
        }
    }
}
