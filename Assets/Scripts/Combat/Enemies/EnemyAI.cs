using UnityEngine;
using UnityEngine.AI;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Cerebro del enemigo: detecta, persigue, patrulla y decide cuándo atacar.
    /// CÓMO ataca lo define el componente EnemyAttackBehavior del mismo objeto
    /// (MeleeAttackBehavior, RangedAttackBehavior, o uno propio).
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class EnemyAI : MonoBehaviour
    {
        [Header("Detection")]
        [Tooltip("Distance at which the enemy notices and starts chasing the player.")]
        public float DetectionRadius = 10f;

        [Header("Patrol (Optional)")]
        [Tooltip("Optional. Leave empty and the enemy stands idle when the player is out of range, exactly as before.")]
        public PatrolRoute Route;

        [Tooltip("Patrol speed as a fraction of the NavMeshAgent speed (which is used for chasing).")]
        [Range(0.1f, 1f)]
        public float PatrolSpeedMultiplier = 0.5f;

        [Tooltip("How close (in meters, ignoring height) the enemy must get to a waypoint to count as arrived.")]
        public float WaypointReachedDistance = 0.5f;

        [Tooltip("When returning to patrol after a chase, go to the nearest waypoint instead of the one it was heading to.")]
        public bool ResumeAtNearestWaypoint = true;

        [Header("Targeting")]
        [Tooltip("Assign the layer used by your Player GameObject.")]
        public LayerMask PlayerLayer;

        private Transform _playerTransform;
        private NavMeshAgent _agent;
        private EnemyAttackBehavior _attack;
        private Animator _animator;

        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");

        // Patrol state
        private bool _isPatrolling;
        private bool _hasPatrolDestination;
        private bool _patrolFinished;
        private int _patrolIndex;
        private int _patrolDirection = 1;
        private float _patrolWaitTimer;
        private float _chaseSpeed;
        private float _chaseStoppingDistance;

        private bool HasPatrolRoute => Route != null && Route.Count > 0;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            TryGetComponent(out _animator);

            _attack = GetComponent<EnemyAttackBehavior>();

            // Compatibilidad: los enemigos melee existentes sin EnemyAttackBehavior siguen funcionando.
            if (_attack == null
                && TryGetComponent<AIMeleeCombatInput>(out _)
                && TryGetComponent<MeleeCombatController>(out _))
            {
                _attack = gameObject.AddComponent<MeleeAttackBehavior>();
            }

            if (_attack == null)
            {
                Debug.LogError($"{name}: EnemyAI needs an EnemyAttackBehavior (Melee or Ranged) on the same GameObject.", this);
                enabled = false;
                return;
            }

            _chaseSpeed = _agent.speed;
        }

        private void Start()
        {
            if (_agent.isOnNavMesh)
            {
                // Ensure stopping distance sits slightly inside attack range
                _agent.stoppingDistance = Mathf.Max(0.1f, _attack.AttackRange - 0.25f);
            }

            _chaseStoppingDistance = _agent.stoppingDistance;

            if (HasPatrolRoute)
            {
                _patrolIndex = Route.GetNearestIndex(transform.position);
            }
        }

        private void Update()
        {
            // Guard: Do not run AI logic if agent isn't anchored on a baked NavMesh
            if (!_agent.isOnNavMesh || !_agent.isActiveAndEnabled) return;

            LocatePlayer();

            // If no valid target is found, stand still (or patrol if a route is set)
            if (_playerTransform == null)
            {
                IdleOrPatrol();
                return;
            }

            float distanceToPlayer = Vector3.Distance(transform.position, _playerTransform.position);

            // Lock movement and smoothly turn toward player while mid-attack
            if (_attack.IsAttacking)
            {
                SafeStopAgent();
                FaceTarget(_playerTransform.position);
                UpdateAnimationSpeed(0f);
                return;
            }

            // 1. OUT OF DETECTION RANGE: Stand idle (or patrol)
            if (distanceToPlayer > DetectionRadius)
            {
                IdleOrPatrol();
                return;
            }

            bool inAttackRange = distanceToPlayer <= _attack.AttackRange;

            // 2. IN ATTACK RANGE (and allowed to attack, e.g. has line of sight): Stop and attack
            if (inAttackRange && _attack.CanAttack(_playerTransform))
            {
                ExitPatrolMode();
                SafeStopAgent();
                FaceTarget(_playerTransform.position);
                UpdateAnimationSpeed(0f);

                _attack.TryAttack(_playerTransform);
            }
            // 3. Otherwise: Chase player (also when in range but unable to attack, to get a clear shot)
            else
            {
                ExitPatrolMode();

                // In range but blocked: keep closing in instead of stopping at the attack distance.
                _agent.stoppingDistance = inAttackRange ? 0.1f : _chaseStoppingDistance;

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

        // ---------------------------------------------------------------- Patrol

        /// <summary>Without a route: stand idle (original behaviour). With a route: patrol it.</summary>
        private void IdleOrPatrol()
        {
            if (!HasPatrolRoute || _patrolFinished)
            {
                SafeStopAgent();
                UpdateAnimationSpeed(0f);
                return;
            }

            EnterPatrolMode();

            // Waiting at a waypoint
            if (_patrolWaitTimer > 0f)
            {
                _patrolWaitTimer -= Time.deltaTime;
                SafeStopAgent();
                UpdateAnimationSpeed(0f);
                return;
            }

            Vector3 target = Route.GetPosition(_patrolIndex);

            if (!_hasPatrolDestination)
            {
                if (_agent.isStopped)
                {
                    _agent.isStopped = false;
                }

                _agent.SetDestination(target);
                _hasPatrolDestination = true;
            }

            Vector3 flatToTarget = target - transform.position;
            flatToTarget.y = 0f;

            bool arrived = flatToTarget.magnitude <= WaypointReachedDistance;
            bool unreachable = !_agent.pathPending && _agent.pathStatus == NavMeshPathStatus.PathInvalid;

            if (arrived || unreachable)
            {
                OnWaypointReached();
                return;
            }

            UpdateAnimationSpeed(_agent.velocity.magnitude);
        }

        private void OnWaypointReached()
        {
            _patrolWaitTimer = Route.GetRandomWait();
            _hasPatrolDestination = false;

            int next = Route.GetNextIndex(_patrolIndex, ref _patrolDirection);
            if (next < 0)
            {
                _patrolFinished = true; // PatrolMode.Once: stay at the last waypoint
            }
            else
            {
                _patrolIndex = next;
            }

            if (_patrolWaitTimer > 0f || _patrolFinished)
            {
                SafeStopAgent();
                UpdateAnimationSpeed(0f);
            }
        }

        private void EnterPatrolMode()
        {
            if (_isPatrolling) return;

            _isPatrolling = true;
            _hasPatrolDestination = false;
            _patrolWaitTimer = 0f;

            _agent.speed = _chaseSpeed * PatrolSpeedMultiplier;
            // The chase stopping distance is about the attack range; patrol needs to actually reach waypoints.
            _agent.stoppingDistance = 0.1f;

            if (ResumeAtNearestWaypoint)
            {
                _patrolIndex = Route.GetNearestIndex(transform.position);
            }
        }

        private void ExitPatrolMode()
        {
            if (!_isPatrolling) return;

            _isPatrolling = false;
            _hasPatrolDestination = false;

            _agent.speed = _chaseSpeed;
            _agent.stoppingDistance = _chaseStoppingDistance;
        }

        // ---------------------------------------------------------------- Helpers

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

            EnemyAttackBehavior attack = _attack != null ? _attack : GetComponent<EnemyAttackBehavior>();
            if (attack != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(transform.position, attack.AttackRange);
            }
        }
    }
}