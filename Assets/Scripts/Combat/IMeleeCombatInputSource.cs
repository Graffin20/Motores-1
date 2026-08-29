namespace StarterAssets.Combat
{
    /// <summary>
    /// Abstracts "who wants to attack" away from the combat state machine.
    /// Implement this once for player input, once for AI decision-making,
    /// and MeleeCombatController never needs to know or care which one it's talking to.
    /// </summary>
    public interface IMeleeCombatInputSource
    {
        /// <summary>True the frame a light attack was requested. Consumed via ConsumeAttackRequest().</summary>
        bool AttackRequested { get; }

        /// <summary>True the frame a heavy/charged attack was requested. Consumed via ConsumeHeavyAttackRequest().</summary>
        bool HeavyAttackRequested { get; }

        /// <summary>Clears the light attack request after MeleeCombatController has acted on it.</summary>
        void ConsumeAttackRequest();

        /// <summary>Clears the heavy attack request after MeleeCombatController has acted on it.</summary>
        void ConsumeHeavyAttackRequest();
    }
}
