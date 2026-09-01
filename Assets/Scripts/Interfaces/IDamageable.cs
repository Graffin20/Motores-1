using UnityEngine;

namespace StarterAssets.Combat
{
public interface IDamageable
{
    public float MaxHealth { get; set; }
    public float CurrentHealth { get; set; }

    void TakeDamage(float damage, Vector3 hitSourcePosition, float knockbackDistanceMultiplier = 1f, float knockbackDurationMultiplier = 1f);

    public void Heal(float heal);
}
}