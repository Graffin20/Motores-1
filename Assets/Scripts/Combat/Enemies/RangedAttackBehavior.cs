using System.Collections;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Ataque a distancia: apunta al objetivo, espera un windup y dispara un proyectil.
    /// Solo dispara si tiene línea de visión (configurable).
    ///
    /// Con Animator y AttackTriggerName: el proyectil sale en el Animation Event AE_FireProjectile del clip
    /// de ataque (el timing vive en la animación, como en el melee). WindupTime pasa a ser solo el
    /// fallback de seguridad si el evento nunca llega. Sin animación, dispara tras WindupTime como antes.
    /// Un stagger o la muerte cancelan el disparo pendiente (el Hit por Any State corta el clip).
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

        [Header("Pursuit")]
        [Tooltip("Una vez detectado el jugador, lo persigue aunque salga de DetectionRadius hasta volver a tenerlo " +
                 "en AttackRange. Desactivado = fuera del radio se queda quieto o patrulla.")]
        public bool KeepPursuingAfterDetection = true;

        public override bool PursuesAfterDetection => KeepPursuingAfterDetection;

        [Header("Timing")]
        [Tooltip("Sin animación: tiempo entre que empieza el ataque y sale el proyectil. Con animación el disparo " +
                 "lo hace AE_FireProjectile; este valor no se usa.")]
        public float WindupTime = 0.4f;

        [Tooltip("Con animación: si AE_FireProjectile no llega en este tiempo, dispara igual y avisa en consola " +
                 "(falta el evento en el clip o la transición no existe). Solo red de seguridad de desarrollo.")]
        public float FireEventSafetyTimeout = 2f;

        [Tooltip("Tiempo que queda quieto después de disparar.")]
        public float RecoveryTime = 0.4f;

        [Header("Audio")]
        [Tooltip("Sonido al empezar el ataque (carga o grito). Opcional.")]
        public SFXPlayer AttackSfx;

        [Header("Animation (Optional)")]
        [Tooltip("Nombre del Trigger del Animator a disparar al atacar. Vacío = sin animación.")]
        public string AttackTriggerName = "Attack";

        private Animator _animator;
        private Health _health;
        private int _attackTriggerHash;
        private bool _isAttacking;

        // Disparo pendiente: lo consume AE_FireProjectile (o el timeout).
        private bool _waitingForFireEvent;
        private Transform _pendingTarget;

        public override bool IsAttacking => _isAttacking;

        // Valores por defecto razonables para un enemigo de rango al agregar el componente en el editor.
        private void Reset()
        {
            AttackRange = 12f;
            TimeBetweenAttacks = 2.5f;
        }

        private void Awake()
        {
            TryGetComponent(out _animator); // en este mismo objeto: los Animation Events solo llegan aquí
            _health = GetComponent<Health>(); // opcional — null = sin cancelación por stagger/muerte
            if (!string.IsNullOrEmpty(AttackTriggerName))
            {
                _attackTriggerHash = Animator.StringToHash(AttackTriggerName);
            }
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            _isAttacking = false;
            _waitingForFireEvent = false;
            _pendingTarget = null;
        }

        /// <summary>Animation Event: ponelo en el frame del clip de ataque en que sale el proyectil.</summary>
        public void AE_FireProjectile()
        {
            if (!_waitingForFireEvent) return; // evento suelto (ataque cancelado o sin ataque en curso)
            _waitingForFireEvent = false;
            if (_pendingTarget != null) Fire(_pendingTarget);
        }

        private bool UsesAnimationEvent =>
            _animator != null && _attackTriggerHash != 0 && _animator.HasParameter(_attackTriggerHash);

        private bool IsInterrupted => _health != null && (_health.IsHitStunned || _health.IsDead);

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
            if (AttackSfx != null) AttackSfx.Play();

            if (UsesAnimationEvent)
            {
                _pendingTarget = target;
                _waitingForFireEvent = true;
                _animator.SetTrigger(_attackTriggerHash);

                float elapsed = 0f;
                while (_waitingForFireEvent)
                {
                    if (IsInterrupted)
                    {
                        CancelAttack();
                        yield break;
                    }

                    if (elapsed >= FireEventSafetyTimeout)
                    {
                        Debug.LogWarning($"{name}: AE_FireProjectile didn't fire within {FireEventSafetyTimeout}s of the " +
                                         $"'{AttackTriggerName}' trigger. Check the attack clip has the event. Firing anyway.", this);
                        AE_FireProjectile();
                        break;
                    }

                    elapsed += Time.deltaTime;
                    yield return null;
                }
                _pendingTarget = null;
            }
            else
            {
                if (_animator != null && _attackTriggerHash != 0)
                {
                    Debug.LogWarning($"{name}: Animator has no '{AttackTriggerName}' trigger; firing after WindupTime.", this);
                }

                if (WindupTime > 0f)
                {
                    yield return new WaitForSeconds(WindupTime);
                }

                if (IsInterrupted)
                {
                    CancelAttack();
                    yield break;
                }

                if (target != null)
                {
                    Fire(target);
                }
            }

            if (RecoveryTime > 0f)
            {
                yield return new WaitForSeconds(RecoveryTime);
            }

            _isAttacking = false;
        }

        private void CancelAttack()
        {
            _waitingForFireEvent = false;
            _pendingTarget = null;
            _isAttacking = false;
            // Un trigger no consumido quedaría armado y dispararía el ataque al salir del stagger.
            if (_animator != null && _attackTriggerHash != 0) _animator.ResetTrigger(_attackTriggerHash);
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
