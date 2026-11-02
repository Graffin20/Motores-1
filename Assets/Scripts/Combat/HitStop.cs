using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Freezes this character's Animator for a moment (hit-stop), so a hit "lands" with weight. Local per
    /// character (Animator.speed = 0), not Time.timeScale: other enemies, the camera, physics and a pause menu
    /// keep their own time, and nothing else ever has to restore a global value.
    ///
    /// Each character owns its own freeze: if the defender's component restored the attacker's Animator and
    /// the defender died/was destroyed mid-freeze, the attacker would stay frozen. HitFeedback calls
    /// Freeze on both the defender and the attacker (adding this component to the attacker if missing).
    /// Overlapping freezes extend to the longest; only animation stops, code-driven movement (knockback,
    /// forward push) and the timers keep running, which is unnoticeable at these durations.
    /// </summary>
    public class HitStop : MonoBehaviour
    {
        private Animator _animator;
        private float _remaining;
        private float _savedSpeed = 1f;
        private bool _frozen;

        private void Awake()
        {
            TryGetComponent(out _animator);
        }

        public void Freeze(float duration)
        {
            if (duration <= 0f || _animator == null) return;

            if (!_frozen)
            {
                _savedSpeed = _animator.speed;
                _animator.speed = 0f;
                _frozen = true;
            }
            _remaining = Mathf.Max(_remaining, duration);
        }

        private void Update()
        {
            if (!_frozen) return;

            // Unscaled: a pause (timeScale 0) during a freeze shouldn't stretch it, and slow-motion shouldn't either.
            _remaining -= Time.unscaledDeltaTime;
            if (_remaining <= 0f) Release();
        }

        private void OnDisable()
        {
            if (_frozen) Release(); // never leave a disabled/destroyed character's Animator frozen
        }

        private void Release()
        {
            _frozen = false;
            _remaining = 0f;
            if (_animator != null) _animator.speed = _savedSpeed;
        }
    }
}
