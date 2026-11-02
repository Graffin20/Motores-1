using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-only: blends the masked "Combat" Animator layer (upper-body combat stance) in while
    /// CombatStateTracker.IsInCombat, the same signal CombatCameraSwitcher uses. Like the Block layer, the
    /// layer holds a single looping stance state and only its weight is driven from code.
    /// With a SwordHolster on the character the stance follows the sword instead (SwordHolster.IsStanceVisible:
    /// drawn or being drawn/put away), and the holster plays Unsheath/CombatIdle/Sheath inside the layer.
    ///
    /// Two things override the stance:
    /// - Actions (ThirdPersonController.ShouldLockMovement: attacks, rolls, hit reactions, parry, death...).
    ///   They play on the Base Layer, and a weight-1 override layer would replace their upper body with the
    ///   idle stance, so the stance fades out fast while one runs.
    /// - Blocking. The applied weight is multiplied by (1 - Block layer weight), so the guard wins whatever
    ///   the layer order in the Animator and the two cross-fade instead of popping.
    /// </summary>
    [RequireComponent(typeof(CombatStateTracker))]
    public class CombatStanceLayer : MonoBehaviour
    {
        [Tooltip("Name of the Animator layer (with the upper-body Avatar Mask) holding the combat stance.")]
        public string CombatLayerName = "Combat";

        [Tooltip("Blend speed (weight per second) when entering or leaving combat.")]
        public float BlendSpeed = 4f;

        [Tooltip("Blend speed while an action (attack, roll, hit...) takes over. Keep it high so the first " +
                 "frames of an attack's windup aren't masked by the stance.")]
        public float ActionBlendOutSpeed = 20f;

        [Tooltip("Blend-in speed while SwordHolster plays Unsheath, so the start of the draw isn't lost in a slow fade.")]
        public float UnsheathBlendInSpeed = 12f;

        private CombatStateTracker _tracker;
        private ThirdPersonController _controller;
        private BlockController _block;
        private SwordHolster _holster;
        private Animator _animator;
        private int _combatLayerIndex = -1;
        private int _blockLayerIndex = -1;
        private float _stanceWeight; // before the block override

        private void Awake()
        {
            _tracker = GetComponent<CombatStateTracker>();
            _controller = GetComponent<ThirdPersonController>(); // optional — null means actions don't suppress the stance
            _block = GetComponent<BlockController>();            // optional — null means nothing overrides the stance
            _holster = GetComponent<SwordHolster>();             // optional — null means the stance follows combat only

            if (!TryGetComponent(out _animator)) return;

            _combatLayerIndex = _animator.GetLayerIndex(CombatLayerName);
            if (_combatLayerIndex < 0)
            {
                Debug.LogWarning($"{nameof(CombatStanceLayer)} on '{name}' couldn't find an Animator layer " +
                                 $"named '{CombatLayerName}'. The combat stance won't be shown.", this);
                return;
            }
            _animator.SetLayerWeight(_combatLayerIndex, 0f); // start fully off

            if (_block != null) _blockLayerIndex = _animator.GetLayerIndex(_block.BlockLayerName);
        }

        private void Update()
        {
            if (_combatLayerIndex < 0) return;

            bool inAction = _controller != null && _controller.ShouldLockMovement();
            // With a holster the stance follows the sword, not combat directly: it must stay up through the
            // Sheath animation after combat ends, and come up for an Unsheath triggered by blocking.
            bool wantsStance = _holster != null ? _holster.IsStanceVisible : _tracker.IsInCombat;
            float target = wantsStance && !inAction ? 1f : 0f;
            float speed = inAction ? ActionBlendOutSpeed
                : _holster != null && _holster.State == SwordHolster.HolsterState.Unsheathing ? UnsheathBlendInSpeed
                : BlendSpeed;
            _stanceWeight = Mathf.MoveTowards(_stanceWeight, target, speed * Time.deltaTime);

            // Read the Block layer's actual weight (BlockController blends it) rather than IsBlocking, so the
            // stance fades out exactly as fast as the guard fades in.
            float blockWeight = _blockLayerIndex >= 0 ? _animator.GetLayerWeight(_blockLayerIndex) : 0f;
            _animator.SetLayerWeight(_combatLayerIndex, _stanceWeight * (1f - blockWeight));
        }
    }
}
