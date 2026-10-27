using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Base de todos los tipos de ataque de enemigos. EnemyAI solo conoce esta clase:
    /// para crear un tipo nuevo (mágico, área, embestida...) heredá de acá e implementá
    /// IsAttacking y Execute. El rango y el cooldown viven en cada ataque, así cada
    /// enemigo puede tener los suyos.
    /// </summary>
    public abstract class EnemyAttackBehavior : MonoBehaviour
    {
        [Header("Attack Timing & Range")]
        [Tooltip("Distance at which the enemy stops to attack.")]
        public float AttackRange = 1.75f;

        [Tooltip("Delay in seconds between the start of one attack and the next.")]
        public float TimeBetweenAttacks = 1.2f;

        private float _cooldownTimer;

        /// <summary>True mientras el ataque está en curso (el enemigo no se mueve y mira al objetivo).</summary>
        public abstract bool IsAttacking { get; }

        public bool IsOnCooldown => _cooldownTimer > 0f;

        /// <summary>
        /// Condiciones de posición para poder atacar (ej: línea de visión). No incluye el cooldown.
        /// Si devuelve false estando en rango, el enemigo sigue acercándose.
        /// </summary>
        public virtual bool CanAttack(Transform target) => true;

        /// <summary>Intenta iniciar un ataque. Devuelve true si arrancó.</summary>
        public bool TryAttack(Transform target)
        {
            if (target == null || IsAttacking || IsOnCooldown || !CanAttack(target)) return false;

            Execute(target);
            _cooldownTimer = TimeBetweenAttacks;
            return true;
        }

        /// <summary>Lógica concreta del ataque. Solo se llama si TryAttack lo autorizó.</summary>
        protected abstract void Execute(Transform target);

        // Si un hijo define su propio Update, debe llamar a base.Update().
        protected virtual void Update()
        {
            if (_cooldownTimer > 0f)
            {
                _cooldownTimer -= Time.deltaTime;
            }
        }
    }
}