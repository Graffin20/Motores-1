using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Timed movement status effects on a character (currently: slow, from the boss fire cone).
    /// ThirdPersonController multiplies its locomotion speed by SpeedMultiplier; roll distance is untouched
    /// (a slowed player can still dodge out). Optional: without this component nothing can be slowed.
    /// </summary>
    public class MovementModifiers : MonoBehaviour
    {
        [Tooltip("Optional object (e.g. a burning/slow VFX) active only while slowed.")]
        public GameObject SlowedIndicator;

        private float _slowMultiplier = 1f;
        private float _slowTimer;

        public bool IsSlowed => _slowTimer > 0f;

        /// <summary>1 when not slowed.</summary>
        public float SpeedMultiplier => IsSlowed ? _slowMultiplier : 1f;

        /// <summary>
        /// Slows locomotion to multiplier (0..1) for duration seconds. Re-applying refreshes instead of
        /// stacking: the longer remaining time and the stronger slow win, so repeated fire ticks keep the
        /// slow topped up without compounding it.
        /// </summary>
        public void ApplySlow(float multiplier, float duration)
        {
            if (duration <= 0f) return;
            multiplier = Mathf.Clamp01(multiplier);

            _slowMultiplier = IsSlowed ? Mathf.Min(_slowMultiplier, multiplier) : multiplier;
            _slowTimer = Mathf.Max(_slowTimer, duration);
            if (SlowedIndicator != null) SlowedIndicator.SetActive(true);
        }

        public void ClearSlow()
        {
            _slowTimer = 0f;
            _slowMultiplier = 1f;
            if (SlowedIndicator != null) SlowedIndicator.SetActive(false);
        }

        private void Update()
        {
            if (!IsSlowed) return;
            _slowTimer -= Time.deltaTime;
            if (_slowTimer <= 0f) ClearSlow();
        }
    }
}
