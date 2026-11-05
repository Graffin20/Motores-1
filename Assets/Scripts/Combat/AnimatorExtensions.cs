using UnityEngine;

namespace StarterAssets.Combat
{
    public static class AnimatorExtensions
    {
        /// <summary>
        /// True if the Animator's controller defines a parameter with this hash. Lets optional
        /// triggers (Parry, Parried, GuardBreak...) fall back or be skipped instead of logging
        /// "Parameter does not exist" when a character's controller doesn't have them yet.
        /// Only called on rare events (hits, parries), so the parameters array allocation is fine.
        /// </summary>
        public static bool HasParameter(this Animator animator, int nameHash)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return false;

            foreach (var parameter in animator.parameters)
            {
                if (parameter.nameHash == nameHash) return true;
            }
            return false;
        }
    }
}
