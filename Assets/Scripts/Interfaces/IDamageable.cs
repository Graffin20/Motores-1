public interface IDamageable
{
    public float MaxHealth { get; set; }
    public float CurrentHealth { get; set; }
    
    public void TakeDamage(float damage);
    
    public void Heal(float heal);
}