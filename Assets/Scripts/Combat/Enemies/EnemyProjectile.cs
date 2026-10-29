using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Proyectil de enemigo. Se mueve en línea recta con SphereCast (no atraviesa paredes
    /// aunque sea rápido), ignora al enemigo que lo disparó y se destruye al impactar.
    /// No necesita Rigidbody ni Collider: solo el visual.
    /// </summary>
    public class EnemyProjectile : MonoBehaviour
    {
        [Header("Defaults (RangedAttackBehavior los sobreescribe al disparar)")]
        public float Speed = 18f;
        public float Damage = 10f;

        [Header("Collision")]
        [Tooltip("Radio de colisión del proyectil.")]
        public float Radius = 0.15f;

        [Tooltip("Capas con las que choca: incluí la del Player y las del entorno.")]
        public LayerMask HitMask = ~0;

        [Tooltip("Segundos antes de autodestruirse si no choca con nada.")]
        public float Lifetime = 5f;

        [Header("Effects (Optional)")]
        [Tooltip("Se instancia en el punto de impacto.")]
        public GameObject ImpactEffect;

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[8];

        private Transform _ownerRoot;
        private Vector3 _direction = Vector3.forward;
        private float _age;

        public void Initialize(Transform owner, Vector3 direction, float speed, float damage)
        {
            _ownerRoot = owner != null ? owner.root : null;
            _direction = direction.normalized;
            Speed = speed;
            Damage = damage;
            transform.rotation = Quaternion.LookRotation(_direction);
        }

        private void Update()
        {
            _age += Time.deltaTime;
            if (_age >= Lifetime)
            {
                Destroy(gameObject);
                return;
            }

            float step = Speed * Time.deltaTime;

            int count = Physics.SphereCastNonAlloc(transform.position, Radius, _direction, HitBuffer, step,
                HitMask, QueryTriggerInteraction.Ignore);

            // Elegir el impacto más cercano que no sea el propio dueño.
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (_ownerRoot != null && HitBuffer[i].transform.root == _ownerRoot) continue;

                if (HitBuffer[i].distance < bestDistance)
                {
                    bestDistance = HitBuffer[i].distance;
                    best = i;
                }
            }

            if (best >= 0)
            {
                Impact(HitBuffer[best]);
                return;
            }

            transform.position += _direction * step;
        }

        private void Impact(RaycastHit hit)
        {
            OnHitTarget(hit.collider);

            if (ImpactEffect != null)
            {
                Instantiate(ImpactEffect, hit.point, Quaternion.LookRotation(hit.normal));
            }

            Destroy(gameObject);
        }

        /// <summary>
        /// PUNTO DE CONEXIÓN con tu sistema de vida. Por defecto manda el mensaje
        /// "TakeDamage(float)" hacia arriba en la jerarquía del objeto golpeado.
        /// Reemplazalo por tu llamada real, por ejemplo:
        ///     other.GetComponentInParent&lt;Health&gt;()?.TakeDamage(Damage);
        /// </summary>
        protected virtual void OnHitTarget(Collider hit)
        {
            if (hit.TryGetComponent<IDamageable>(out var damageable))
            {
                damageable.TakeDamage(Damage, transform.position);
            }        
        }
    }
}
