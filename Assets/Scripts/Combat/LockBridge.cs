using UnityEngine;
using Unity.Cinemachine;

namespace StarterAssets.Combat
{
    [RequireComponent(typeof(PlayerMeleeCombatInput))]
    [RequireComponent(typeof(LockOnController))]
    [RequireComponent(typeof(MeleeCombatController))]
    public class LockBridge : MonoBehaviour
    {
        private PlayerMeleeCombatInput _input;
        private LockOnController _lockOnController;
        private MeleeCombatController _meleeCombat;

        [Header("Camera Integration")]
        [Tooltip("Manually assign your PlayerCameraRoot here, or leave blank to auto-detect from Cinemachine Camera.")]
        public Transform cameraRoot; 
        [Tooltip("How fast the camera snaps/rotates to face the locked target.")]
        public float rotationSpeed = 15f;

        private void Awake()
        {
            _input = GetComponent<PlayerMeleeCombatInput>();
            _lockOnController = GetComponent<LockOnController>();
            _meleeCombat = GetComponent<MeleeCombatController>();
            
            if (cameraRoot == null)
            {
                var cinemachineCam = FindObjectOfType<CinemachineCamera>();
                if (cinemachineCam != null)
                {
                    cameraRoot = cinemachineCam.Target.TrackingTarget;
                }
            }
        }

        private void Update()
        {
            // 1. Handle Lock-On Toggle Request
            if (_input.LockOnRequested)
            {
                _lockOnController.ToggleLockOn();
                _input.ConsumeLockOnRequest();
            }

            // 2. Handle Target Switch Request
            if (_lockOnController.IsLockedOn && _input.TargetSwitchDirection != Vector2.zero)
            {
                _lockOnController.SwitchTarget(_input.TargetSwitchDirection);
            }

            // 3. Keep Combat and Camera locked onto the target
            if (_lockOnController.IsLockedOn)
            {
                Transform targetTransform = _lockOnController.CurrentTargetTransform;
                _meleeCombat.lookAtReference = targetTransform;
                
                Debug.Log($"[Bridge] IsLockedOn is TRUE. Target: {(targetTransform != null ? targetTransform.name : "NULL")}");
                Debug.Log($"[Bridge] cameraRoot assigned: {(cameraRoot != null ? cameraRoot.name : "NULL - ASSIGN IN INSPECTOR!")}");
                
                if (cameraRoot != null)
                {
                    // Calculate direction from camera root to enemy
                    Vector3 directionToTarget = (targetTransform.position + Vector3.up * 1f) - cameraRoot.position;
                    Quaternion targetRotation = Quaternion.LookRotation(directionToTarget);

                    // Smoothly rotate the camera root to face the enemy
                    cameraRoot.rotation = Quaternion.Slerp(cameraRoot.rotation, targetRotation, Time.deltaTime * rotationSpeed);
                }
            }
            else
            {
                if (_meleeCombat.lookAtReference != null)
                {
                    _meleeCombat.lookAtReference = null;
                }
            }
        }
    }
}