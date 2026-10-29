using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-only glue between lock-on input, LockOnController and MeleeCombatController.
    ///
    /// Deliberately does NOT touch the camera: ThirdPersonController.CameraRotation() is the single owner
    /// of camera rotation (its locked-on branch already tracks LockOnController's target). Having this
    /// script also write the same camera-root transform meant two systems fought over it, and whichever
    /// ran later in the frame silently won.
    /// </summary>
    [RequireComponent(typeof(PlayerMeleeCombatInput))]
    [RequireComponent(typeof(LockOnController))]
    [RequireComponent(typeof(MeleeCombatController))]
    public class LockBridge : MonoBehaviour
    {
        private PlayerMeleeCombatInput _input;
        private LockOnController _lockOnController;
        private MeleeCombatController _meleeCombat;

        private void Awake()
        {
            _input = GetComponent<PlayerMeleeCombatInput>();
            _lockOnController = GetComponent<LockOnController>();
            _meleeCombat = GetComponent<MeleeCombatController>();
        }

        private void Update()
        {
            // 1. Lock-on toggle request
            if (_input.LockOnRequested)
            {
                _lockOnController.ToggleLockOn();
                _input.ConsumeLockOnRequest();
            }

            // 2. While locked on: handle target switching, and keep attacks facing the target
            if (_lockOnController.IsLockedOn)
            {
                if (_input.TargetSwitchDirection != Vector2.zero)
                {
                    _lockOnController.SwitchTarget(_input.TargetSwitchDirection);
                }

                _meleeCombat.lookAtReference = _lockOnController.CurrentTargetTransform;
            }
            else if (_meleeCombat.lookAtReference != null)
            {
                _meleeCombat.lookAtReference = null;
            }
        }
    }
}