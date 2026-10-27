using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// AI-driven counterpart to PlayerMeleeCombatInput. Satisfies the same interface so
    /// MeleeCombatController, RollController, and BlockController treat a player and an enemy
    /// identically.
    ///
    /// PLACEHOLDER: no decision-making lives here. A future AI behavior script (state machine,
    /// behavior tree, whatever we build) should call the Request*/SetBlocking methods below when
    /// it decides to act, instead of implementing IMeleeCombatInputSource itself.
    /// </summary>
    public class AIMeleeCombatInput : MonoBehaviour, IMeleeCombatInputSource
    {
        public bool AttackRequested { get; private set; }
        public bool HeavyAttackRequested { get; private set; }
        public bool RollRequested { get; private set; }
        public bool BlockHeld { get; private set; }

        public void ConsumeAttackRequest() => AttackRequested = false;
        public void ConsumeHeavyAttackRequest() => HeavyAttackRequested = false;
        public void ConsumeRollRequest() => RollRequested = false;

        /// <summary>Call from the (future) AI behavior script when it decides to throw a light attack.</summary>
        public void RequestAttack() => AttackRequested = true;

        /// <summary>Call from the (future) AI behavior script when it decides to throw a heavy attack.</summary>
        public void RequestHeavyAttack() => HeavyAttackRequested = true;

        /// <summary>Call from the (future) AI behavior script when it decides to roll/dodge.</summary>
        public void RequestRoll() => RollRequested = true;

        /// <summary>Call from the (future) AI behavior script to start/stop blocking.</summary>
        public void SetBlocking(bool blocking) => BlockHeld = blocking;

        // TODO: once the AI behavior script exists, consider whether it should live on this same
        // GameObject and call the methods above directly, or whether this adapter should instead
        // expose a reference the AI script pulls from. Leaving both open for now.
    }
}