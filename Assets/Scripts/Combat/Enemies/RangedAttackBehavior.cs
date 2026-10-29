using System.Collections;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Ataque a distancia: apunta al objetivo, espera un windup y dispara un proyectil.
    /// Solo dispara si tiene línea de visión (configurable).
    /// </summary>
    public class RangedAttackBehavior : EnemyAttackBehavior
    {
        [Header("Projectile")]
        [Tooltip("Prefab con el componente EnemyProjectile.")]
        public EnemyProjectile ProjectilePrefab;

        [Tooltip("Opcional. De dónde sale el proyectil (mano, cañón...). Si está vacío sale del pecho del enemigo.")]
        public Transform FirePoint;

        public float ProjectileSpeed = 18f;
        public float Damage = 10f;

        [Header("Aiming")]
        [Tooltip("Altura sobre el pivote del objetivo a la que apunta (pecho/torso).")]
        public float AimHeightOffset = 1.2f;

        [Tooltip("Si está activo, no dispara sin línea de visión y se acerca hasta tenerla.")]
        public bool RequireLineOfSight = true;

        [Tooltip("Capas que bloquean los disparos (paredes, suelo). No incluyas Player ni Enemy si tienen capa propia.")]
        public LayerMask LineOfSightBlockers = 1; // Default

        [Header("Timing")]
        [Tooltip("Tiempo entre que empieza el ataque y sale el proyectil (para sincronizar con la animación).")]
        public float WindupTime = 0.4f;

        [Tooltip("Tiempo que queda quieto después de disparar.")]
        public float RecoveryTime = 0.4f;

        [Header("Animation (Optional)")]
        [Tooltip("Nombre del Trigger del Animator a disparar al atacar. Vacío = sin animación.")]
        public string AttackTriggerName = "";

        private Animator _animator;
        private int _attackTriggerHash;
        private bool _isAttacking;

        public override bool IsAttacking => _isAttacking;

        // Valores por defecto razonables para un enemigo de rango al agregar el componente en el editor.
        private void Reset()
        {
            AttackRange = 12f;
            TimeBetweenAttacks = 2.5f;
        }

        private void Awake()
        {
            TryGetComponent(out _animator);
            if (!string.IsNullOrEmpty(AttackTriggerName))
            {
                _attackTriggerHash = Animator.StringToHash(AttackTriggerName);
            }
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            _isAttacking = false;
        }

        public override bool CanAttack(Transform target)
        {
            if (!RequireLineOfSight) return true;

            Vector3 origin = GetOrigin();
            Vector3 aimPoint = GetAimPoint(target);

            if (Physics.Linecast(origin, aimPoint, out RaycastHit hit, LineOfSightBlockers, QueryTriggerInteraction.Ignore))
            {
                Transform hitRoot = hit.transform.root;
                // Chocar con el propio objetivo o con uno mismo no cuenta como bloqueo.
                return hitRoot == target.root || hitRoot == transform.root;
            }

            return true;
        }

        protected override void Execute(Transform target)
        {
            StartCoroutine(AttackRoutine(target));
        }

        private IEnumerator AttackRoutine(Transform target)
        {
            _isAttacking = true;

            if (_animator != null && _attackTriggerHash != 0)
            {
                _animator.SetTrigger(_attackTriggerHash);
            }

            if (WindupTime > 0f)
            {
                yield return new WaitForSeconds(WindupTime);
            }

            if (target != null)
            {
                Fire(target);
            }

            if (RecoveryTime > 0f)
            {
                yield return new WaitForSeconds(RecoveryTime);
            }

            _isAttacking = false;
        }

        private void Fire(Transform target)
        {
            if (ProjectilePrefab == null)
            {
                Debug.LogWarning($"{name}: RangedAttackBehavior has no ProjectilePrefab assigned.", this);
                return;
            }

            Vector3 origin = GetOrigin();
            Vector3 direction = (GetAimPoint(target) - origin).normalized;

            EnemyProjectile projectile = Instantiate(ProjectilePrefab, origin, Quaternion.LookRotation(direction));
            projectile.Initialize(transform, direction, ProjectileSpeed, Damage);
        }

        private Vector3 GetOrigin()
        {
            return FirePoint != null ? FirePoint.position : transform.position + Vector3.up * AimHeightOffset;
        }

        private Vector3 GetAimPoint(Transform target)
        {
            return target.position + Vector3.up * AimHeightOffset;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(transform.position, AttackRange);
        }
    }
}
