using UnityEngine;

namespace StarterAssets.Combat
{
    public class LockOnController : MonoBehaviour
    {
        [Header("Targeting Settings")]
        public float maxLockRange = 15f;
        public LayerMask targetLayer;
        public LayerMask obstacleLayer;
        public float maxLockAngle = 75f;
        public GameObject lockOnUIPrefab;

        [Tooltip("Optional override for the camera used to score targets and check line of sight. Leave empty to use ThirdPersonController's GameplayCamera.")]
        [SerializeField] private Transform _viewCameraTransform;

        private ILockable _currentTarget;
        private GameObject _activeUIInstance;
        private Transform _transformCache;
        private ThirdPersonController _thirdPersonController;

        public bool IsLockedOn => IsTargetUsable(_currentTarget);
        public Transform CurrentTargetTransform => IsTargetUsable(_currentTarget) ? _currentTarget.Transform : null;

        /// <summary>
        /// ILockable is an interface, so a plain `== null` / `?.` on it can't see Unity's "destroyed object"
        /// null, and touching .Transform on a destroyed MonoBehaviour throws. Check the underlying Unity
        /// object explicitly before touching anything on it.
        /// </summary>
        private static bool IsTargetUsable(ILockable target)
        {
            if (target == null) return false;
            if (target is UnityEngine.Object unityObject && unityObject == null) return false; // destroyed
            return target.Transform != null && target.IsAlive;
        }

        private void Awake()
        {
            _transformCache = transform;
            _thirdPersonController = GetComponent<ThirdPersonController>(); // optional camera source
        }

        private void Start()
        {
            // Not in Awake: ThirdPersonController resolves its camera in its own Awake, which may run
            // after this one. Camera.main isn't used — with several MainCamera-tagged cameras it can
            // resolve to the wrong one.
            ResolveViewCamera();
        }

        private void ResolveViewCamera()
        {
            if (_viewCameraTransform == null && _thirdPersonController != null)
            {
                _viewCameraTransform = _thirdPersonController.CameraTransform;
            }
        }

        private void Update()
        {
            if (_currentTarget != null)
            {
                if (!IsTargetUsable(_currentTarget) ||
                    Vector3.Distance(_transformCache.position, _currentTarget.Transform.position) > maxLockRange ||
                    HasLineOfSightObstacle(_currentTarget.Transform))
                {
                    ClearLock();
                }
            }
        }

        public void ToggleLockOn()
        {
            Debug.Log("[LockOn] ToggleLockOn called! Current IsLockedOn: " + IsLockedOn);
            if (IsLockedOn)
            {
                ClearLock();
            }
            else
            {
                AcquireBestTarget();
            }
        }

        public void AcquireBestTarget()
        {
            ResolveViewCamera();

            Collider[] hits = Physics.OverlapSphere(_transformCache.position, maxLockRange, targetLayer);
            Debug.Log($"[LockOn] OverlapSphere found {hits.Length} colliders on targetLayer.");

            ILockable bestTarget = null;
            float bestScore = float.MaxValue;

            Vector3 searchOrigin = _viewCameraTransform != null ? _viewCameraTransform.position : _transformCache.position;
            Vector3 searchDirection = _viewCameraTransform != null ? _viewCameraTransform.forward : _transformCache.forward;

            foreach (var hit in hits)
            {
                if (hit == null) continue;

                Debug.Log($"[LockOn] Checking collider on GameObject: {hit.gameObject.name}");

                if (!hit.TryGetComponent<ILockable>(out var target))
                {
                    Debug.Log($"[LockOn] -> REJECTED: GameObject '{hit.gameObject.name}' does NOT have an ILockable component!");
                    continue;
                }

                if (!target.IsAlive)
                {
                    Debug.Log($"[LockOn] -> REJECTED: Target is not alive.");
                    continue;
                }

                if (target.Transform == _transformCache) continue;

                Vector3 toTarget = target.Transform.position - searchOrigin;
                float distance = toTarget.magnitude;
                float angle = Vector3.Angle(searchDirection, toTarget);

                Debug.Log($"[LockOn] -> Candidate found: Distance = {distance:F2}, Angle = {angle:F2}");

                if (angle > maxLockAngle)
                {
                    Debug.Log($"[LockOn] -> REJECTED: Angle {angle} exceeds maxLockAngle {maxLockAngle}.");
                    continue;
                }

                if (HasLineOfSightObstacle(target.Transform))
                {
                    Debug.Log($"[LockOn] -> REJECTED: Line of sight blocked by an obstacle.");
                    continue;
                }

                float score = angle * 1.5f + distance;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = target;
                }
            }

            if (bestTarget != null)
            {
                Debug.Log($"[LockOn] SUCCESS! Locked onto: {bestTarget.Transform.name}");
                SetTarget(bestTarget);
            }
            else
            {
                Debug.Log("[LockOn] FAILED: No valid targets met all criteria.");
            }
        }

        public void SwitchTarget(Vector2 inputDir)
        {
            if (!IsLockedOn) return;
            // (Switch target logic remains standard)
        }

        private void SetTarget(ILockable newTarget)
        {
            _currentTarget = newTarget;
            UpdateUI();
        }

        public void ClearLock()
        {
            Debug.Log("[LockOn] Clearing lock.");
            _currentTarget = null;
            if (_activeUIInstance != null)
            {
                Destroy(_activeUIInstance);
            }
        }

        private bool HasLineOfSightObstacle(Transform targetTransform)
        {
            if (targetTransform == null) return true;

            // Use camera position, or player position offset upward
            Vector3 origin = _viewCameraTransform != null ? _viewCameraTransform.position : _transformCache.position + Vector3.up * 1.5f;

            // Target center mass of the enemy
            Vector3 targetPosition = targetTransform.position + Vector3.up * 1f;
            Vector3 direction = targetPosition - origin;
            float distance = direction.magnitude;

            // Perform raycast against obstacle layer
            if (Physics.Raycast(origin, direction.normalized, out RaycastHit hit, distance, obstacleLayer))
            {
                // Ignore hits that belong to the player or the target itself
                if (hit.transform.root != _transformCache.root && hit.transform.root != targetTransform.root)
                {
                    return true; // Genuinely blocked by a wall/obstacle
                }
            }

            return false;
        }

        private void UpdateUI()
        {
            if (_activeUIInstance != null) Destroy(_activeUIInstance);

            if (lockOnUIPrefab != null && _currentTarget != null)
            {
                _activeUIInstance = Instantiate(lockOnUIPrefab, _currentTarget.Transform);
                _activeUIInstance.transform.localPosition = new Vector3(0f, 2f, 0f);
            }
        }
    }
}