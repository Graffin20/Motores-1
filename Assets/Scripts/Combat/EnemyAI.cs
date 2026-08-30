using UnityEngine;
using UnityEngine.AI;

namespace StarterAssets.Combat
{
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(AIMeleeCombatInput))]
    [RequireComponent(typeof(MeleeCombatController))]
    public class EnemyAI : MonoBehaviour
    {
        [Header("Detection & Combat Ranges")]
        [Tooltip("Distance at which the enemy notices and starts chasing the player.")]
        public float DetectionRadius = 10f;

        [Tooltip("Distance at which the enemy stops to swing.")]
        public float AttackRange = 1.75f;

        [Tooltip("Delay in seconds after finishing an attack before starting a new one.")]
        public float TimeBetweenAttacks = 1.2f;

        [Header("Attack Decision Weights")]
        [Range(0f, 1f)]
        [Tooltip("Chance to use a heavy attack instead of a light attack when in range.")]
        public float HeavyAttackChance = 0.25f;

        [Header("Targeting")]
        [Tooltip("Assign the layer used by your Player GameObject.")]
        public LayerMask PlayerLayer;

        private Transform _playerTransform;
        private NavMeshAgent _agent;
        private AIMeleeCombatInput _aiInput;
        private MeleeCombatController _combatController;
        private Animator _animator;

        private float _attackCooldownTimer;
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _aiInput = GetComponent<AIMeleeCombatInput>();
            _combatController = GetComponent<MeleeCombatController>();
            TryGetComponent(out _animator);
        }

        private void Start()
        {
            if (_agent.isOnNavMesh)
            {
                // Ensure stopping distance sits slightly inside attack range
                _agent.stoppingDistance = Mathf.Max(0.1f, AttackRange - 0.25f);
            }
        }

        private void Update()
        {
            // Guard: Do not run AI logic if agent isn't anchored on a baked NavMesh
            if (!_agent.isOnNavMesh || !_agent.isActiveAndEnabled) return;

            LocatePlayer();

            // If no valid target is found, stand completely still
            if (_playerTransform == null)
            {
                SafeStopAgent();
                UpdateAnimationSpeed(0f);
                return;
            }

            float distanceToPlayer = Vector3.Distance(transform.position, _playerTransform.position);

            // Tick attack cooldown down toward 0
            if (_attackCooldownTimer > 0f)
            {
                _attackCooldownTimer -= Time.deltaTime;
            }

            // Lock movement and smoothly turn toward player while mid-swing
            if (_combatController.IsAttacking)
            {
                SafeStopAgent();
                FaceTarget(_playerTransform.position);
                UpdateAnimationSpeed(0f);
                return;
            }

            // 1. OUT OF DETECTION RANGE: Stand idle
            if (distanceToPlayer > DetectionRadius)
            {
                SafeStopAgent();
                UpdateAnimationSpeed(0f);
                return;
            }

            // 2. IN ATTACK RANGE: Stop moving and execute attack logic
            if (distanceToPlayer <= AttackRange)
            {
                SafeStopAgent();
                FaceTarget(_playerTransform.position);
                UpdateAnimationSpeed(0f);

                if (_attackCooldownTimer <= 0f)
                {
                    ExecuteAttackChoice();
                    _attackCooldownTimer = TimeBetweenAttacks;
                }
            }
            // 3. IN DETECTION RANGE: Chase player
            else
            {
                if (_agent.isStopped)
                {
                    _agent.isStopped = false;
                }

                _agent.SetDestination(_playerTransform.position);

                // Use actual physical velocity magnitude for animation blending
                float movementSpeed = _agent.velocity.magnitude;

                // Dampen speed parameter if close to target or pushing a collider
                if (_agent.remainingDistance <= _agent.stoppingDistance)
                {
                    movementSpeed = 0f;
                }

                UpdateAnimationSpeed(movementSpeed);
            }
        }

        private void ExecuteAttackChoice()
        {
            if (Random.value < HeavyAttackChance)
            {
                _aiInput.RequestHeavyAttack();
            }
            else
            {
                _aiInput.RequestAttack();
            }
        }

        private void LocatePlayer()
        {
            // 1. Try finding by Player tag first
            if (_playerTransform == null)
            {
                GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                {
                    _playerTransform = playerObj.transform;
                    return;
                }
            }

            // 2. Fallback: Search nearby colliders, strictly filtering out self
            if (_playerTransform == null)
            {
                Collider[] hits = Physics.OverlapSphere(transform.position, DetectionRadius, PlayerLayer);
                foreach (var hit in hits)
                {
                    if (hit.transform.root == transform.root) continue; // Ignore own hierarchy

                    _playerTransform = hit.transform;
                    break;
                }
            }
        }

        private void FaceTarget(Vector3 targetPosition)
        {
            Vector3 direction = (targetPosition - transform.position).normalized;
            direction.y = 0f;

            if (direction != Vector3.zero)
            {
                Quaternion lookRotation = Quaternion.LookRotation(direction);
                transform.rotation = Quaternion.Slerp(transform.rotation, lookRotation, Time.deltaTime * 10f);
            }
        }

        private void SafeStopAgent()
        {
            if (_agent.isOnNavMesh && !_agent.isStopped)
            {
                _agent.isStopped = true;
            }
        }

        private void UpdateAnimationSpeed(float targetSpeed)
        {
            if (_animator == null) return;

            // Smoothly blend the Speed parameter to avoid sudden animation snaps
            float currentSpeed = _animator.GetFloat(SpeedHash);
            float smoothedSpeed = Mathf.Lerp(currentSpeed, targetSpeed, Time.deltaTime * 10f);

            // Prevent floating-point underflow (like 1.016e-16)
            if (smoothedSpeed < 0.01f)
            {
                smoothedSpeed = 0f;
            }

            _animator.SetFloat(SpeedHash, smoothedSpeed);

            // Update MotionSpeed parameter required by StarterAssets Blend Trees
            float motionSpeed = smoothedSpeed > 0.05f ? 1f : 0f;
            _animator.SetFloat(MotionSpeedHash, motionSpeed);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, DetectionRadius);

            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, AttackRange);
        }
    }
}