using System;
using System.Collections.Generic;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-side "am I in combat" state. In combat while ANY of these hold:
    ///   - at least one enemy has registered itself as engaging the player (chasing/attacking),
    ///   - the player is attacking or rolling,
    ///   - the player is locked on to a target.
    /// Entering combat is immediate; leaving waits CombatExitDelay after the last condition clears, so
    /// a short gap (an enemy briefly stepping out of range, the pause between two swings) doesn't
    /// flicker the combat camera back and forth.
    ///
    /// Enemies are tracked as a SET, not a bool: with several pursuers, one giving up must not end
    /// combat while another is still attacking. The set can also serve as a lock-on candidate pool later.
    /// </summary>
    public class CombatStateTracker : MonoBehaviour
    {
        [Tooltip("Seconds after the last combat condition clears before IsInCombat turns false.")]
        public float CombatExitDelay = 2f;

        private readonly HashSet<UnityEngine.Object> _engagedEnemies = new();
        private readonly List<UnityEngine.Object> _destroyedBuffer = new();

        private MeleeCombatController _combat;
        private RollController _roll;
        private LockOnController _lockOn;

        private float _exitTimer;

        public bool IsInCombat { get; private set; }

        /// <summary>Number of enemies currently registered as engaging the player.</summary>
        public int EngagedEnemyCount => _engagedEnemies.Count;

        /// <summary>Raised with the new value whenever IsInCombat changes.</summary>
        public event Action<bool> CombatStateChanged;

        private void Awake()
        {
            _combat = GetComponent<MeleeCombatController>(); // optional — null means attacking never counts
            _roll = GetComponent<RollController>();           // optional — null means rolling never counts
            _lockOn = GetComponent<LockOnController>();       // optional — null means lock-on never counts
        }

        /// <summary>Called by an enemy when it starts chasing/attacking the player. Idempotent.</summary>
        public void RegisterEngagedEnemy(UnityEngine.Object enemy)
        {
            if (enemy != null) _engagedEnemies.Add(enemy);
        }

        /// <summary>Called by an enemy when it stops engaging (gave up, died, disabled). Idempotent.</summary>
        public void UnregisterEngagedEnemy(UnityEngine.Object enemy)
        {
            _engagedEnemies.Remove(enemy);
        }

        private void Update()
        {
            PruneDestroyedEnemies();

            if (HasActiveCombatCondition())
            {
                _exitTimer = CombatExitDelay;
                SetInCombat(true);
            }
            else if (IsInCombat)
            {
                _exitTimer -= Time.deltaTime;
                if (_exitTimer <= 0f) SetInCombat(false);
            }
        }

        private bool HasActiveCombatCondition()
        {
            if (_engagedEnemies.Count > 0) return true;
            if (_combat != null && _combat.IsAttacking) return true;
            if (_roll != null && _roll.IsRolling) return true;
            if (_lockOn != null && _lockOn.IsLockedOn) return true;
            return false;
        }

        /// <summary>Safety net: drops enemies that were destroyed without unregistering, which would
        /// otherwise keep the player in combat forever. Unity's == null catches destroyed objects because
        /// the set stores UnityEngine.Object, not an interface.</summary>
        private void PruneDestroyedEnemies()
        {
            _destroyedBuffer.Clear();
            foreach (var enemy in _engagedEnemies)
            {
                if (enemy == null) _destroyedBuffer.Add(enemy);
            }
            foreach (var enemy in _destroyedBuffer) _engagedEnemies.Remove(enemy);
        }

        private void SetInCombat(bool inCombat)
        {
            if (IsInCombat == inCombat) return;
            IsInCombat = inCombat;
            CombatStateChanged?.Invoke(inCombat);
        }
    }
}
