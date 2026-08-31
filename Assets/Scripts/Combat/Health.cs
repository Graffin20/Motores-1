using UnityEngine;

namespace StarterAssets.Combat
{
    public class Health : MonoBehaviour, IDamageable
    {
        [field: SerializeField] public float MaxHealth { get; set; } = 100f;
        public float CurrentHealth { get; set; }

        private Animator _animator;
        private bool _isDead;

        private void Awake()
        {
            CurrentHealth = MaxHealth;
            TryGetComponent(out _animator);
        }

        public void TakeDamage(float damage)
        {
            if (_isDead) return;

            CurrentHealth -= damage;
            Debug.Log($"{gameObject.name} took {damage} damage. Health remaining: {CurrentHealth}");

            if (_animator != null)
            {
                _animator.SetTrigger("Hit"); // Optional hit-reaction animation
            }

            if (CurrentHealth <= 0f)
            {
                Die();
            }
        }

        public void Heal(float heal)
        {
            if (_isDead) return;
            CurrentHealth = Mathf.Min(CurrentHealth + heal, MaxHealth);
        }

        private void Die()
        {
            _isDead = true;
            Debug.Log($"{gameObject.name} died!");

            if (_animator != null)
            {
                _animator.SetTrigger("Die");
            }

            // Disable movement and collider on death
            if (TryGetComponent<UnityEngine.AI.NavMeshAgent>(out var agent)) agent.enabled = false;
            if (TryGetComponent<Collider>(out var col)) col.enabled = false;

            Destroy(gameObject, 5f); // Clean up after 5 seconds
        }
    }
}