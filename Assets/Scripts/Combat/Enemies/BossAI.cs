using UnityEngine;
using UnityEngine.AI;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Boss brain (replaces EnemyAI on the boss; regular enemies keep EnemyAI). Decides WHAT to do; HOW is
    /// MeleeCombatController (3-hit combo, through AIMeleeCombatInput) and BossSpecialAttacks (fire cone,
    /// stomp, grab), both timed by animation events.
    ///
    /// Movement: slow. It owns the boss's rotation (NavMeshAgent.updateRotation is off) and turns at
    /// TurnSpeed; it only walks while the player is within MoveAngle of its forward, otherwise it turns in
    /// place and drives the Animator's Rotating state (IsTurning + TurnDirection: -1 left, +1 right).
    ///
    /// Attack choice (only when no attack is running and RecoveryBetweenAttacks has elapsed):
    ///   1. Player in the stun of THIS boss's stomp -> walk over fast (PunishSpeed) and grab.
    ///   2. Player inside the stomp cone behind the boss (StompConeAngle, StompRange) -> stomp (turns in the air).
    ///   3. Player in front (angle &lt;= FrontAngle):
    ///        within ComboRange    -> weighted random: combo / fire cone (whichever is off cooldown)
    ///        within FireConeRange -> fire cone if off cooldown, else walk in to melee
    ///   4. Otherwise turn / approach.
    /// Cooldowns start when an attack ENDS (so they mean "time before it can be used again").
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(Health))]
    [RequireComponent(typeof(BossSpecialAttacks))]
    [RequireComponent(typeof(MeleeCombatController))]
    [RequireComponent(typeof(AIMeleeCombatInput))]
    public class BossAI : MonoBehaviour
    {
        private enum BossAttack { None, Combo, FireCone, Stomp, Grab }

        [Header("Detection")]
        public float DetectionRadius = 15f;
        [Tooltip("Once detected, keep pursuing the player anywhere (boss arena).")]
        public bool KeepPursuingAfterDetection = true;
        [Tooltip("Fallback if no object is tagged Player.")]
        public LayerMask PlayerLayer;

        [Header("Movement")]
        [Tooltip("Walking speed (overrides the NavMeshAgent's speed).")]
        public float WalkSpeed = 1.8f;
        [Tooltip("Degrees/second the boss turns toward the player outside attacks.")]
        public float TurnSpeed = 60f;
        [Tooltip("Only walks while the player is within this angle of its forward; otherwise turns in place first.")]
        public float MoveAngle = 30f;
        [Tooltip("Rotating state hysteresis: standing still, starts turning above this angle...")]
        public float TurnStartAngle = 20f;
        [Tooltip("...and stops below this one.")]
        public float TurnStopAngle = 5f;
        [Tooltip("Stops walking this close to the player (keep it below ComboRange).")]
        public float MeleeStopDistance = 2.2f;

        [Header("Punish (walk over to a stomp-stunned player and grab)")]
        public float PunishSpeed = 4.5f;
        public float PunishTurnSpeed = 240f;

        [Header("Attack Zones (horizontal distance / angle from the boss's forward)")]
        [Tooltip("Half-angle of 'in front' (combo and fire cone).")]
        public float FrontAngle = 45f;
        [Tooltip("Total angle (degrees) of the cone behind the boss, centred on its back, where the player triggers the stomp. Editable with the handles in the Scene view.")]
        [Range(1f, 360f)]
        public float StompConeAngle = 140f;
        public float ComboRange = 3f;
        [Tooltip("Max distance to start the fire cone. Keep it at or below BossSpecialAttacks.FireConeLength.")]
        public float FireConeRange = 6f;
        [Tooltip("Reach of the stomp cone (also editable in the Scene view).")]
        public float StompRange = 4f;
        [Tooltip("Starts the grab within this distance (the grab itself connects at AE_GrabCheck via GrabRadius).")]
        public float GrabRange = 2f;

        [Header("Attack Choice (melee range)")]
        [Min(0f)] public float ComboWeight = 0.6f;
        [Min(0f)] public float FireConeWeight = 0.4f;

        [Header("Cooldowns (from the end of the attack)")]
        public float ComboCooldown = 2.5f;
        public float FireConeCooldown = 7f;
        public float StompCooldown = 5f;
        [Tooltip("Pause after any attack before choosing the next (it still turns/walks meanwhile).")]
        public float RecoveryBetweenAttacks = 0.8f;
        [Tooltip("The stomp is a reaction to the player getting behind/beside the boss, so it may start during RecoveryBetweenAttacks (e.g. right after a fire cone). Off = it waits like the other attacks, and the boss may have turned to face the player by then.")]
        public bool StompIgnoresRecovery = true;

        [Header("Animator")]
        public string IsTurningParameter = "IsTurning";
        public string TurnDirectionParameter = "TurnDirection";
        public float TurnDirectionDampTime = 0.15f;

        private NavMeshAgent _agent;
        private Animator _animator;
        private Health _health;
        private BossSpecialAttacks _special;
        private MeleeCombatController _melee;
        private AIMeleeCombatInput _input;

        private Transform _player;
        private Health _playerHealth;
        private CombatStateTracker _playerCombatState;
        private bool _isEngaged;
        private bool _hasDetectedPlayer;

        private BossAttack _currentAttack = BossAttack.None;
        private bool _comboStarted;
        private int _comboRequestFrame;
        private float _comboCooldown, _fireConeCooldown, _stompCooldown, _recoveryTimer;

        private bool _isTurning;

        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int MotionSpeedHash = Animator.StringToHash("MotionSpeed");
        private int _isTurningHash, _turnDirectionHash;
        private bool _hasSpeed, _hasMotionSpeed, _hasIsTurning, _hasTurnDirection;

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            _health = GetComponent<Health>();
            _special = GetComponent<BossSpecialAttacks>();
            _melee = GetComponent<MeleeCombatController>();
            _input = GetComponent<AIMeleeCombatInput>();
            TryGetComponent(out _animator);

            // This script turns the boss (slowly); the agent only moves it.
            _agent.updateRotation = false;
            _agent.speed = WalkSpeed;

            _isTurningHash = Animator.StringToHash(IsTurningParameter);
            _turnDirectionHash = Animator.StringToHash(TurnDirectionParameter);
            _hasSpeed = _animator.HasParameter(SpeedHash);
            _hasMotionSpeed = _animator.HasParameter(MotionSpeedHash);
            _hasIsTurning = _animator.HasParameter(_isTurningHash);
            _hasTurnDirection = _animator.HasParameter(_turnDirectionHash);
        }

        private void OnDisable() => SetEngaged(false);

        private void Update()
        {
            // Before the agent guard: death disables the agent (same as EnemyAI).
            if (_health.IsDead)
            {
                SetEngaged(false);
                return;
            }

            TickCooldowns();
            UpdateCurrentAttack();

            if (!_agent.isOnNavMesh || !_agent.isActiveAndEnabled) return;

            // Parried (the only stagger the boss has with StaggerOnPlainHits off) or pushed: Health owns the movement.
            if (_health.IsHitStunned || _health.IsBeingKnockedBack)
            {
                StandStill();
                return;
            }

            LocatePlayer();
            if (_player == null)
            {
                SetEngaged(false);
                StandStill();
                return;
            }

            Vector3 toPlayer = _player.position - transform.position;
            toPlayer.y = 0f;
            float distance = toPlayer.magnitude;
            float signedAngle = Vector3.SignedAngle(Flat(transform.forward), toPlayer, Vector3.up);
            float angle = Mathf.Abs(signedAngle);

            if (distance <= DetectionRadius) _hasDetectedPlayer = true;
            bool chasing = distance <= DetectionRadius || (_hasDetectedPlayer && KeepPursuingAfterDetection);
            bool attacking = _currentAttack != BossAttack.None;
            SetEngaged(attacking || chasing);

            if (attacking || !chasing)
            {
                StandStill(); // attacks own facing (MeleeCombatController / BossSpecialAttacks)
                return;
            }

            // 1. Punish a stomp stun with the grab (no recovery wait: the stun window is short).
            if (_playerHealth != null && _special.CanGrab(_playerHealth))
            {
                if (distance <= GrabRange && angle <= FrontAngle && _special.StartGrab(_playerHealth))
                {
                    BeginAttack(BossAttack.Grab);
                    return;
                }
                Locomote(distance, signedAngle, PunishSpeed, PunishTurnSpeed, GrabRange * 0.8f);
                return;
            }

            // 2. Stomp first, possibly skipping the recovery pause: otherwise the boss keeps turning during the
            // pause and a player beside it ends up in front before the stomp can be chosen.
            if ((StompIgnoresRecovery || _recoveryTimer <= 0f) && TryStomp(distance, angle)) return;

            if (_recoveryTimer <= 0f && TryChooseAttack(distance, angle)) return;

            Locomote(distance, signedAngle, WalkSpeed, TurnSpeed, MeleeStopDistance);
        }

        // ------------------------------------------------------------------ Attacks

        private bool TryChooseAttack(float distance, float angle)
        {
            if (angle > FrontAngle) return false;

            bool coneReady = _fireConeCooldown <= 0f && distance <= FireConeRange;

            // 3a. Melee range: weighted random among what's ready.
            if (distance <= ComboRange)
            {
                bool comboReady = _comboCooldown <= 0f;
                float comboWeight = comboReady ? ComboWeight : 0f;
                float coneWeight = coneReady ? FireConeWeight : 0f;
                if (comboWeight + coneWeight <= 0f) return false;

                if (Random.value * (comboWeight + coneWeight) < comboWeight) return StartCombo();
                return StartFireCone();
            }

            // 3b. Mid range: always the fire cone when ready; otherwise Locomote walks in to melee.
            return coneReady && StartFireCone();
        }

        /// <summary>2. Player inside the stomp cone (behind / beside): stomp, reorienting in the air.</summary>
        private bool TryStomp(float distance, float angle)
        {
            if (!IsInStompCone(distance, angle) || _stompCooldown > 0f) return false;
            if (!_special.StartStomp(_player)) return false;
            BeginAttack(BossAttack.Stomp);
            return true;
        }

        /// <summary>angle = unsigned angle between the boss's forward and the player (0 = in front, 180 = straight behind).</summary>
        public bool IsInStompCone(float distance, float angle)
        {
            return distance <= StompRange && 180f - angle <= StompConeAngle * 0.5f;
        }

        private bool StartFireCone()
        {
            if (!_special.StartFireCone(_player)) return false;
            BeginAttack(BossAttack.FireCone);
            return true;
        }

        private bool StartCombo()
        {
            // MeleeCombatController faces this at each stage start (limited by its MaxFacingSnapDegrees) and,
            // with TrackTargetDuringWindup, keeps turning (WindupTrackingTurnSpeed) until each swing commits.
            _melee.lookAtReference = _player;
            _input.RequestAttack();
            _comboStarted = false;
            _comboRequestFrame = Time.frameCount;
            BeginAttack(BossAttack.Combo);
            return true;
        }

        private void BeginAttack(BossAttack attack)
        {
            _currentAttack = attack;
            StandStill();
        }

        /// <summary>Keeps the combo going (one request per stage) and notices when the current attack ends.</summary>
        private void UpdateCurrentAttack()
        {
            switch (_currentAttack)
            {
                case BossAttack.None:
                    return;

                case BossAttack.Combo:
                    if (_melee.IsAttacking)
                    {
                        _comboStarted = true;
                        // Always the full 3 hits: buffer the next stage as soon as it can be buffered. Never
                        // after the last stage, so no request survives into the combo cooldown.
                        if (_melee.CanBufferNextComboStage) _input.RequestAttack();
                        return;
                    }
                    // Not attacking: finished, parried, or the first request was refused (cooldown/hitstun).
                    if (!_comboStarted && Time.frameCount - _comboRequestFrame <= 2) return;
                    break;

                default:
                    if (_special.IsBusy) return;
                    break;
            }

            EndAttack();
        }

        private void EndAttack()
        {
            switch (_currentAttack)
            {
                case BossAttack.Combo: _comboCooldown = ComboCooldown; break;
                case BossAttack.FireCone: _fireConeCooldown = FireConeCooldown; break;
                case BossAttack.Stomp: _stompCooldown = StompCooldown; break;
            }
            _currentAttack = BossAttack.None;
            _recoveryTimer = RecoveryBetweenAttacks;
        }

        private void TickCooldowns()
        {
            float dt = Time.deltaTime;
            _comboCooldown -= dt;
            _fireConeCooldown -= dt;
            _stompCooldown -= dt;
            _recoveryTimer -= dt;
        }

        // ------------------------------------------------------------------ Movement

        /// <summary>Turns toward the player at turnSpeed; walks only while roughly facing them, else turns in place.</summary>
        private void Locomote(float distance, float signedAngle, float speed, float turnSpeed, float stopDistance)
        {
            float angle = Mathf.Abs(signedAngle);
            bool walk = distance > stopDistance && angle <= MoveAngle;

            if (walk)
            {
                _isTurning = false;
                _agent.speed = speed;
                _agent.stoppingDistance = stopDistance;
                if (_agent.isStopped) _agent.isStopped = false;
                _agent.SetDestination(_player.position);
                RotateToward(turnSpeed);

                float movementSpeed = _agent.remainingDistance <= _agent.stoppingDistance ? 0f : _agent.velocity.magnitude;
                SetAnimator(movementSpeed, turning: false, signedAngle);
                return;
            }

            StopAgent();

            // Hysteresis so the Rotating state doesn't flicker around the threshold.
            if (!_isTurning && angle > TurnStartAngle) _isTurning = true;
            else if (_isTurning && angle < TurnStopAngle) _isTurning = false;

            if (_isTurning) RotateToward(turnSpeed);
            SetAnimator(0f, _isTurning, signedAngle);
        }

        private void RotateToward(float degreesPerSecond)
        {
            Vector3 direction = Flat(_player.position - transform.position);
            if (direction.sqrMagnitude < 0.001f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                Quaternion.LookRotation(direction.normalized, Vector3.up), degreesPerSecond * Time.deltaTime);
        }

        private void StandStill()
        {
            StopAgent();
            _isTurning = false;
            SetAnimator(0f, turning: false, 0f);
        }

        private void StopAgent()
        {
            if (_agent.isOnNavMesh && _agent.isActiveAndEnabled && !_agent.isStopped) _agent.isStopped = true;
        }

        private void SetAnimator(float speed, bool turning, float signedAngle)
        {
            if (_animator == null) return;

            if (_hasSpeed)
            {
                float smoothed = Mathf.Lerp(_animator.GetFloat(SpeedHash), speed, Time.deltaTime * 10f);
                if (smoothed < 0.01f) smoothed = 0f;
                _animator.SetFloat(SpeedHash, smoothed);
                if (_hasMotionSpeed) _animator.SetFloat(MotionSpeedHash, smoothed > 0.05f ? 1f : 0f);
            }

            if (_hasIsTurning) _animator.SetBool(_isTurningHash, turning);
            if (_hasTurnDirection && turning)
            {
                // Only updated while turning, so the blend doesn't snap to 0 on the frame the state exits.
                _animator.SetFloat(_turnDirectionHash, Mathf.Sign(signedAngle), TurnDirectionDampTime, Time.deltaTime);
            }
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        // ------------------------------------------------------------------ Player / engagement

        private void LocatePlayer()
        {
            if (_player != null) return;

            GameObject tagged = GameObject.FindGameObjectWithTag("Player");
            if (tagged != null)
            {
                SetPlayer(tagged.transform);
                return;
            }

            foreach (var hit in Physics.OverlapSphere(transform.position, DetectionRadius, PlayerLayer))
            {
                if (hit.transform.IsChildOf(transform)) continue;
                SetPlayer(hit.transform);
                return;
            }
        }

        private void SetPlayer(Transform player)
        {
            _player = player;
            _playerHealth = player.GetComponentInParent<Health>();
            _playerCombatState = player.GetComponentInParent<CombatStateTracker>();
        }

        private void SetEngaged(bool engaged)
        {
            if (engaged == _isEngaged) return;
            _isEngaged = engaged;

            if (_playerCombatState == null) return;
            if (engaged) _playerCombatState.RegisterEngagedEnemy(this);
            else _playerCombatState.UnregisterEngagedEnemy(this);
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 p = transform.position;
            Vector3 forward = Flat(transform.forward).normalized;

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(p, DetectionRadius);

            Gizmos.color = Color.red;
            DrawArc(p, forward, FrontAngle, ComboRange);
            Gizmos.color = new Color(1f, 0.5f, 0f);
            DrawArc(p, forward, FrontAngle, FireConeRange);
            // The stomp cone is drawn (and edited) by BossAIEditor's scene handles.
        }

        private static void DrawArc(Vector3 centre, Vector3 forward, float halfAngle, float radius)
        {
            const int segments = 16;
            Vector3 previous = centre;
            for (int i = 0; i <= segments; i++)
            {
                float a = Mathf.Lerp(-halfAngle, halfAngle, i / (float)segments);
                Vector3 point = centre + Quaternion.AngleAxis(a, Vector3.up) * forward * radius;
                Gizmos.DrawLine(previous, point);
                previous = point;
            }
            Gizmos.DrawLine(previous, centre);
        }
    }
}
