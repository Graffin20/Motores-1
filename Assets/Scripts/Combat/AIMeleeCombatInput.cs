using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// AI-driven counterpart to PlayerMeleeCombatInput. Satisfies the same interface so
    /// MeleeCombatController treats a player and an enemy identically.
    ///
    /// PLACEHOLDER: no decision-making lives here. A future AI behavior script (state machine,
    /// behavior tree, whatever we build) should call RequestAttack()/RequestHeavyAttack() on this
    /// component when it decides to swing, instead of implementing IMeleeCombatInputSource itself.
    /// Keeping the interface implementation on its own component means the eventual AI script
    /// can focus purely on decision-making (targeting, range checks, aggro, etc.) and just calls
    /// into this thin adapter.
    /// </summary>
    public class AIMeleeCombatInput : MonoBehaviour, IMeleeCombatInputSource
    {
        public bool AttackRequested { get; private set; }
        public bool HeavyAttackRequested { get; private set; }

        public void ConsumeAttackRequest() => AttackRequested = false;
        public void ConsumeHeavyAttackRequest() => HeavyAttackRequested = false;

        /// <summary>Call from the (future) AI behavior script when it decides to throw a light attack.</summary>
        public void RequestAttack() => AttackRequested = true;

        /// <summary>Call from the (future) AI behavior script when it decides to throw a heavy attack.</summary>
        public void RequestHeavyAttack() => HeavyAttackRequested = true;

        // TODO: once the AI behavior script exists, consider whether it should live on this same
        // GameObject and call the methods above directly, or whether this adapter should instead
        // expose a reference the AI script pulls from. Leaving both open for now.
    }
}
