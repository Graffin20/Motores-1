using System.Linq;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Entity-agnostic block. Works for player or AI via IMeleeCombatInputSource. Currently only
    /// drives the blocking animation state — the actual gameplay effects of blocking (damage
    /// mitigation, stamina drain, parry timing, guard break) are intentionally left as a TODO
    /// below rather than guessed at.
    /// </summary>
    public class BlockController : MonoBehaviour
    {
        [Header("Animation")]
        [Tooltip("Animator bool parameter that stays true for as long as blocking is held. Optional now that the masked layer's weight is what actually controls visibility, but harmless to keep if other systems read it.")]
        public string BlockAnimatorBool = "IsBlocking";

        [Header("Masked Block Layer")]
        [Tooltip("Name of the Animator layer (with the block Avatar Mask assigned) that should only be active while blocking. That layer should contain a single BlockLoop state — no internal transitions needed, since weight alone controls whether it has any effect.")]
        public string BlockLayerName = "Block";
        [Tooltip("How fast the layer's weight blends between 0 and 1. Set very high (e.g. 100) for an instant on/off snap instead of a blend.")]
        public float LayerWeightBlendSpeed = 12f;

        private IMeleeCombatInputSource _inputSource;
        private StaminaSystem _stamina;
        private Animator _animator;
        private bool _hasAnimator;
        private int _blockBoolHash;
        private int _blockLayerIndex = -1;

        public bool IsBlocking { get; private set; }

        private void Awake()
        {
            _inputSource = GetComponents<MonoBehaviour>().OfType<IMeleeCombatInputSource>().FirstOrDefault();
            _stamina = GetComponent<StaminaSystem>(); // optional, for future stamina-drain-while-blocking logic
            _hasAnimator = TryGetComponent(out _animator);
            _blockBoolHash = Animator.StringToHash(BlockAnimatorBool);

            if (_hasAnimator)
            {
                _blockLayerIndex = _animator.GetLayerIndex(BlockLayerName);
                if (_blockLayerIndex < 0)
                {
                    Debug.LogWarning($"{nameof(BlockController)} on '{name}' couldn't find an Animator layer " +
                                      $"named '{BlockLayerName}'. The masked block layer's weight won't be controlled.", this);
                }
                else
                {
                    _animator.SetLayerWeight(_blockLayerIndex, 0f); // start fully off
                }
            }

            if (_inputSource == null)
            {
                Debug.LogWarning($"{nameof(BlockController)} on '{name}' found no " +
                                  $"{nameof(IMeleeCombatInputSource)} implementation. Add " +
                                  $"{nameof(PlayerMeleeCombatInput)} or {nameof(AIMeleeCombatInput)}.", this);
            }
        }

        private void Update()
        {
            if (_inputSource == null) return;

            bool wantsToBlock = _inputSource.BlockHeld;

            // ---------------------------------------------------------------------------------
            // TODO: block gameplay logic goes here. This currently only drives the animation.
            //
            // DONE elsewhere: MeleeCombatController and RollController both check IsBlocking and
            // refuse to start an attack/roll while blocking is held (see their Try*() methods).
            // Movement is untouched by blocking, so moving while blocking already works.
            //
            // Still not implemented:
            //   - Stamina cost: either a flat cost to raise the guard, a per-second drain while
            //     held, or a cost per hit absorbed — pick one once combat feel is nailed down.
            //   - Damage mitigation: reduce or fully negate incoming damage while IsBlocking.
            //   - Parry window: a short grace period at the start of a block that rewards
            //     precise timing (e.g. stagger the attacker) rather than just reducing damage.
            //   - Guard break: what happens if stamina hits zero while blocking — forced
            //     unblock, stagger, vulnerability window, etc.
            //   - Directional blocking, if attacks from behind shouldn't be blockable.
            //   - The reverse direction isn't handled either: currently attacking/rolling doesn't
            //     forcibly drop an active block — decide if that should interrupt it.
            // ---------------------------------------------------------------------------------

            SetBlocking(wantsToBlock);
            TickLayerWeight();
        }

        private void TickLayerWeight()
        {
            if (!_hasAnimator || _blockLayerIndex < 0) return;

            float target = IsBlocking ? 1f : 0f;
            float current = _animator.GetLayerWeight(_blockLayerIndex);

            if (Mathf.Approximately(current, target)) return;

            float next = Mathf.MoveTowards(current, target, LayerWeightBlendSpeed * Time.deltaTime);
            _animator.SetLayerWeight(_blockLayerIndex, next);
        }

        private void SetBlocking(bool blocking)
        {
            if (blocking == IsBlocking) return;

            IsBlocking = blocking;
            if (_hasAnimator) _animator.SetBool(_blockBoolHash, IsBlocking);
        }
    }
}