using Unity.Cinemachine;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-only: switches to the combat camera while CombatStateTracker.IsInCombat, by raising that
    /// camera's Priority above the exploration camera's. The CinemachineBrain then blends between them
    /// (the blend style/duration lives on the Brain's Default Blend or a Custom Blends asset), so this
    /// script never moves a camera itself.
    ///
    /// Both cameras should follow the same target (ThirdPersonController.CinemachineCameraTarget):
    /// ThirdPersonController stays the single owner of camera rotation, including lock-on, and the two
    /// cameras only differ in framing (distance, shoulder offset, FOV...).
    /// </summary>
    [RequireComponent(typeof(CombatStateTracker))]
    public class CombatCameraSwitcher : MonoBehaviour
    {
        [Tooltip("The Cinemachine camera used in combat.")]
        public CinemachineVirtualCameraBase CombatCamera;

        [Tooltip("Combat camera priority while in combat. Must be higher than the exploration camera's priority.")]
        public int InCombatPriority = 20;

        [Tooltip("Combat camera priority out of combat. Must be lower than the exploration camera's priority.")]
        public int OutOfCombatPriority = 0;

        private CombatStateTracker _tracker;

        private void Awake()
        {
            _tracker = GetComponent<CombatStateTracker>();

            if (CombatCamera == null)
            {
                Debug.LogWarning($"{nameof(CombatCameraSwitcher)} on '{name}' has no CombatCamera assigned — the camera won't switch.", this);
            }
        }

        private void OnEnable()
        {
            _tracker.CombatStateChanged += ApplyPriority;
            ApplyPriority(_tracker.IsInCombat); // start in the right camera without waiting for a change
        }

        private void OnDisable()
        {
            _tracker.CombatStateChanged -= ApplyPriority;
        }

        private void ApplyPriority(bool inCombat)
        {
            if (CombatCamera == null) return;
            CombatCamera.Priority = inCombat ? InCombatPriority : OutOfCombatPriority;
        }
    }
}
