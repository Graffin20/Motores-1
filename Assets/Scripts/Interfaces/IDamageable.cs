using UnityEngine;

public interface IDamageable
{
    public float MaxHealth { get; set; }
    public float CurrentHealth { get; set; }

    /// <summary>Resolves an incoming hit and reports what happened, so the attacker can react to
    /// the outcome (e.g. stagger itself on Parried).</summary>
    HitResult TakeDamage(HitInfo hit);

    public void Heal(float heal);
}
