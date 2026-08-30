namespace StarterAssets.Combat
{
    /// <summary>
    /// Abstracts "who wants to do what" away from the combat/movement state machines.
    /// Implement this once for player input, once for AI decision-making, and none of
    /// MeleeCombatController, RollController, or BlockController need to know or care
    /// which one they're talking to.
    /// </summary>
    public interface IMeleeCombatInputSource
    {
        /// <summary>True the frame a light attack was requested. Consumed via ConsumeAttackRequest().</summary>
        bool AttackRequested { get; }

        /// <summary>True the frame a heavy/charged attack was requested. Consumed via ConsumeHeavyAttackRequest().</summary>
        bool HeavyAttackRequested { get; }

        /// <summary>True the frame a roll was requested. Consumed via ConsumeRollRequest().</summary>
        bool RollRequested { get; }

        /// <summary>True for as long as block is held. Not consumed — read directly, it's a held state, not a pulse.</summary>
        bool BlockHeld { get; }

        void ConsumeAttackRequest();
        void ConsumeHeavyAttackRequest();
        void ConsumeRollRequest();
    }
}