using UnityEngine;

public class DamageableLockTarget : MonoBehaviour, ILockable
{
    private IDamageable _damageable;

    public Transform Transform => transform;

    public bool IsAlive => _damageable is { CurrentHealth: > 0f };

    private void Awake()
    {
        _damageable = GetComponent<IDamageable>();
    }
}