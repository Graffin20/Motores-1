using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Tracks and regenerates stamina for whichever GameObject it's attached to. Lives as its own
    /// component (not inside the combat/roll/block scripts) so it can be added independently and
    /// read/spent by any of them via GetComponent — combat scripts don't own stamina, they just
    /// spend it.
    /// </summary>
    public class StaminaSystem : MonoBehaviour
    {
        [Header("Stamina")]
        public float MaxStamina = 100f;

        [Header("Regeneration")]
        public float RegenPerSecond = 15f;
        [Tooltip("Time after the most recent spend before regeneration starts.")]
        public float RegenDelay = 1f;

        public float CurrentStamina { get; private set; }
        public float StaminaPercent => MaxStamina > 0f ? CurrentStamina / MaxStamina : 0f;

        private float _regenDelayTimer;

        private void Awake()
        {
            CurrentStamina = MaxStamina;
        }

        private void Update()
        {
            if (_regenDelayTimer > 0f)
            {
                _regenDelayTimer -= Time.deltaTime;
                return;
            }

            if (CurrentStamina < MaxStamina)
            {
                CurrentStamina = Mathf.Min(MaxStamina, CurrentStamina + RegenPerSecond * Time.deltaTime);
            }
        }

        /// <summary>Check without spending — use for UI graying-out, AI decision-making, etc.</summary>
        public bool HasEnoughStamina(float amount) => CurrentStamina >= amount;

        /// <summary>
        /// Attempts to spend stamina. Returns false and spends nothing if there isn't enough.
        /// Successful spends reset the regen delay.
        /// </summary>
        public bool TrySpend(float amount)
        {
            if (amount <= 0f) return true;
            if (CurrentStamina < amount) return false;

            CurrentStamina -= amount;
            _regenDelayTimer = RegenDelay;
            return true;
        }
    }
}