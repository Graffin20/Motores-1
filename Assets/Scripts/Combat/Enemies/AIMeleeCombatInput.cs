using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// AI-driven counterpart to PlayerMeleeCombatInput. Satisfies the same interface so
    /// MeleeCombatController, RollController, and BlockController treat a player and an enemy
    /// identically.
    ///
    /// No decision-making lives here. The AI behavior script (currently EnemyAI) calls the
    /// Request*/SetBlocking methods below when it decides to act, instead of implementing
    /// IMeleeCombatInputSource itself.
    /// </summary>
    public class AIMeleeCombatInput : MonoBehaviour, IMeleeCombatInputSource
    {
        public bool AttackRequested { get; private set; }
        public bool HeavyAttackRequested { get; private set; }
        public bool RollRequested { get; private set; }
        public bool BlockHeld { get; private set; }

        // Frame each pulse was raised on. Requests are pulses (same rule as PlayerMeleeCombatInput):
        // one the combat scripts refuse — cooldown, hitstun, low stamina — must expire instead of
        // firing later. Unlike player input, which arrives before Update, the AI script raises these
        // FROM an Update whose order relative to MeleeCombatController's is undefined, so clearing
        // unconditionally in LateUpdate could wipe a request before it was ever read. Instead, each
        // pulse survives exactly one full Update pass after the frame it was raised in.
        private int _attackFrame, _heavyAttackFrame, _rollFrame;

        /// <summary>AI aims through MeleeCombatController.lookAtReference (set by EnemyAI), not a direction.</summary>
        public bool TryGetAttackDirection(out Vector3 direction)
        {
            direction = default;
            return false;
        }

        public void ConsumeAttackRequest() => AttackRequested = false;
        public void ConsumeHeavyAttackRequest() => HeavyAttackRequested = false;
        public void ConsumeRollRequest() => RollRequested = false;

        /// <summary>Call from the AI behavior script when it decides to throw a light attack.</summary>
        public void RequestAttack()
        {
            AttackRequested = true;
            _attackFrame = Time.frameCount;
        }

        /// <summary>Call from the AI behavior script when it decides to throw a heavy attack.</summary>
        public void RequestHeavyAttack()
        {
            HeavyAttackRequested = true;
            _heavyAttackFrame = Time.frameCount;
        }

        /// <summary>Call from the AI behavior script when it decides to roll/dodge.</summary>
        public void RequestRoll()
        {
            RollRequested = true;
            _rollFrame = Time.frameCount;
        }

        private void LateUpdate()
        {
            int frame = Time.frameCount;
            if (frame > _attackFrame) AttackRequested = false;
            if (frame > _heavyAttackFrame) HeavyAttackRequested = false;
            if (frame > _rollFrame) RollRequested = false;
        }

        /// <summary>Call from the (future) AI behavior script to start/stop blocking.</summary>
        public void SetBlocking(bool blocking) => BlockHeld = blocking;

        // TODO: once the AI behavior script exists, consider whether it should live on this same
        // GameObject and call the methods above directly, or whether this adapter should instead
        // expose a reference the AI script pulls from. Leaving both open for now.
    }
}