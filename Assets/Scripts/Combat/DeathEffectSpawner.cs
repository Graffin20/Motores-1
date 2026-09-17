using UnityEngine;
using StarterAssets.Combat;

namespace StarterAssets.Combat
{
    public class DeathEffectSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject _deathEffectPrefab;
        private Health _health;
        private bool _hasSpawnedEffect = false;

        private void Awake()
        {
            _health = GetComponent<Health>();
        }

        private void Update()
        {
            // Check if the character just died
            if (_health.IsDead && !_hasSpawnedEffect)
            {
                SpawnDeathEffect();
            }
        }

        private void SpawnDeathEffect()
        {
            if (_deathEffectPrefab == null)
            {
                Debug.LogWarning($"Death effect prefab not assigned on '{gameObject.name}'", this);
                return;
            }

            _hasSpawnedEffect = true;
            Instantiate(_deathEffectPrefab, transform.position, transform.rotation);
            Debug.Log($"Death effect spawned at {gameObject.name}", this);
        }
    }
}