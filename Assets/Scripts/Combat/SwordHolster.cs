using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-only: moves the sword between a back socket and a hand socket and plays the Unsheath / Sheath
    /// animations on the masked "Combat" Animator layer (whose weight CombatStanceLayer drives).
    ///
    /// - Attack start (sword not in hand): snapped to the hand instantly, the layer jumps to CombatIdle so the
    ///   stance doesn't replay Unsheath when it fades back in after the attack.
    /// - Entering combat any other way, or pressing block (IsBlockRequested): Unsheath plays. Blocking waits
    ///   for the end of Unsheath (IBlockReadiness), so the draw is visible before the guard.
    /// - Out of combat and not blocking for SheathDelay seconds: Sheath plays; the stance layer stays up
    ///   until it ends (CombatStanceLayer reads IsStanceVisible).
    /// - Any action (ThirdPersonController.ShouldLockMovement: attack, roll, hit, parry, death...) interrupts
    ///   Unsheath/Sheath: the sword stays wherever it is, and the next frame without an action plays whatever
    ///   is still missing.
    ///
    /// Timing comes from animation events on the Combat layer clips (AE_SwordToHand + AE_UnsheathEnd on
    /// Unsheath, AE_SwordToBack + AE_SheathEnd on Sheath), each phase with a dev safety timeout. Events that
    /// don't match the current phase are ignored, so a clip still playing on a faded-out layer (after an
    /// interruption) can't move the sword.
    /// </summary>
    // After MeleeCombatController (default order), so an attack started this frame snaps the sword this frame.
    [DefaultExecutionOrder(50)]
    [RequireComponent(typeof(CombatStateTracker))]
    public class SwordHolster : MonoBehaviour, IBlockReadiness
    {
        public enum HolsterState { Sheathed, Unsheathing, Drawn, Sheathing }

        [Header("Sword")]
        [Tooltip("The sword object. It is re-parented between the two sockets (local position/rotation zeroed), " +
                 "so place each socket where and how the sword should sit.")]
        public Transform Sword;
        [Tooltip("Empty child of a spine bone: the sheathed position.")]
        public Transform BackSocket;
        [Tooltip("Empty child of the right hand bone: the drawn grip.")]
        public Transform HandSocket;

        [Header("Combat Layer")]
        public string CombatLayerName = "Combat";
        public string UnsheathState = "Unsheath";
        public string CombatIdleState = "CombatIdle";
        public string SheathState = "Sheath";
        [Tooltip("Cross-fade time into Unsheath/Sheath/CombatIdle within the Combat layer.")]
        public float StateCrossFade = 0.1f;

        [Header("Timing")]
        [Tooltip("Seconds out of combat and not blocking before the sword is put away (combat itself already " +
                 "waits CombatStateTracker.CombatExitDelay). Mostly matters after blocking outside combat.")]
        public float SheathDelay = 1f;
        [Tooltip("Dev safety fallback only: Unsheath normally ends via AE_UnsheathEnd.")]
        public float UnsheathSafetyDuration = 2f;
        [Tooltip("Dev safety fallback only: Sheath normally ends via AE_SheathEnd.")]
        public float SheathSafetyDuration = 2f;

        private CombatStateTracker _tracker;
        private ThirdPersonController _controller;
        private MeleeCombatController _combat;
        private BlockController _block;
        private Animator _animator;
        private int _layer = -1;
        private int _unsheathHash, _idleHash, _sheathHash;

        private bool _swordInHand;
        private float _phaseTimer;     // safety timeout of the running Unsheath/Sheath
        private float _idleOutOfCombat; // time spent Drawn with no reason to keep the sword out
        private Vector3 _swordWorldScale;
        private bool _worldScaleCaptured;

        public HolsterState State { get; private set; } = HolsterState.Sheathed;

        /// <summary>The stance layer should be shown: the sword is out or being moved.</summary>
        public bool IsStanceVisible => State != HolsterState.Sheathed;

        public bool IsReadyToBlock => State == HolsterState.Drawn;

        private void Awake()
        {
            _tracker = GetComponent<CombatStateTracker>();
            _controller = GetComponent<ThirdPersonController>(); // optional — null means nothing interrupts
            _combat = GetComponent<MeleeCombatController>();     // optional — null means no instant draw on attack
            _block = GetComponent<BlockController>();            // optional — null means block never draws

            if (TryGetComponent(out _animator))
            {
                _layer = _animator.GetLayerIndex(CombatLayerName);
                _unsheathHash = Animator.StringToHash(UnsheathState);
                _idleHash = Animator.StringToHash(CombatIdleState);
                _sheathHash = Animator.StringToHash(SheathState);

                if (_layer < 0)
                    Debug.LogWarning($"{nameof(SwordHolster)} on '{name}' couldn't find an Animator layer named " +
                                     $"'{CombatLayerName}'. The sword will move without Unsheath/Sheath animations.", this);
                else
                    foreach (var state in new[] { UnsheathState, CombatIdleState, SheathState })
                        if (!_animator.HasState(_layer, Animator.StringToHash(state)))
                            Debug.LogWarning($"{nameof(SwordHolster)}: layer '{CombatLayerName}' has no state named '{state}'.", this);
            }

            if (Sword == null || BackSocket == null || HandSocket == null)
                Debug.LogWarning($"{nameof(SwordHolster)} on '{name}' is missing the Sword or a socket.", this);

            AttachTo(BackSocket); // sheathed by default
            _swordInHand = false;
        }

        private void Update()
        {
            bool attacking = _combat != null && _combat.IsAttacking;
            bool inAction = _controller != null && _controller.ShouldLockMovement();

            // Attacks never wait for an animation.
            if (attacking && State != HolsterState.Drawn)
            {
                DrawInstantly();
                return;
            }

            if (inAction)
            {
                Interrupt();
                return;
            }

            bool wantsSword = _tracker.IsInCombat || (_block != null && _block.IsBlockRequested);

            switch (State)
            {
                case HolsterState.Sheathed:
                    if (wantsSword) StartUnsheath();
                    break;

                case HolsterState.Unsheathing:
                    TickSafety(UnsheathSafetyDuration, "AE_UnsheathEnd", AE_UnsheathEnd);
                    break;

                case HolsterState.Drawn:
                    _idleOutOfCombat = wantsSword ? 0f : _idleOutOfCombat + Time.deltaTime;
                    if (_idleOutOfCombat >= SheathDelay) StartSheath();
                    break;

                case HolsterState.Sheathing:
                    if (wantsSword)
                    {
                        // Back from the current point: if the blade hasn't reached the scabbard yet, just
                        // return to the stance; otherwise draw it again.
                        if (_swordInHand) SetDrawn(StateCrossFade);
                        else StartUnsheath();
                    }
                    else
                    {
                        TickSafety(SheathSafetyDuration, "AE_SheathEnd", AE_SheathEnd);
                    }
                    break;
            }
        }

        // ---- Animation events (Combat layer clips) ----

        public void AE_SwordToHand()
        {
            if (State == HolsterState.Unsheathing) AttachToHand();
        }

        public void AE_UnsheathEnd()
        {
            if (State != HolsterState.Unsheathing) return;
            AttachToHand(); // in case AE_SwordToHand is missing
            SetDrawn(StateCrossFade);
        }

        public void AE_SwordToBack()
        {
            if (State == HolsterState.Sheathing) AttachToBack();
        }

        public void AE_SheathEnd()
        {
            if (State != HolsterState.Sheathing) return;
            AttachToBack();
            State = HolsterState.Sheathed; // CombatStanceLayer fades the layer out from here
        }

        // ---- Transitions ----

        private void StartUnsheath()
        {
            State = HolsterState.Unsheathing;
            _phaseTimer = 0f;
            CrossFade(_unsheathHash, StateCrossFade);
        }

        private void StartSheath()
        {
            State = HolsterState.Sheathing;
            _phaseTimer = 0f;
            CrossFade(_sheathHash, StateCrossFade);
        }

        private void DrawInstantly()
        {
            AttachToHand();
            SetDrawn(0f);
        }

        private void SetDrawn(float crossFade)
        {
            State = HolsterState.Drawn;
            _idleOutOfCombat = 0f;
            CrossFade(_idleHash, crossFade);
        }

        /// <summary>An action took over mid-animation: settle on wherever the sword is now.</summary>
        private void Interrupt()
        {
            if (State == HolsterState.Unsheathing || State == HolsterState.Sheathing)
            {
                if (_swordInHand) SetDrawn(0f);
                else State = HolsterState.Sheathed;
            }
            _idleOutOfCombat = 0f; // the sheath countdown restarts after the action
        }

        private void TickSafety(float duration, string eventName, System.Action finish)
        {
            _phaseTimer += Time.deltaTime;
            if (_phaseTimer < duration) return;

            Debug.LogWarning($"{nameof(SwordHolster)} on '{name}': {eventName} never fired; forcing it after " +
                             $"{duration}s. Add the event to the clip.", this);
            finish();
        }

        private void CrossFade(int stateHash, float duration)
        {
            if (_layer < 0) return;

            // Already in that state (e.g. an interrupted Unsheath still playing on the faded-out layer):
            // restart it explicitly instead of relying on a cross-fade into itself.
            var current = _animator.GetCurrentAnimatorStateInfo(_layer);
            if (current.shortNameHash == stateHash && !_animator.IsInTransition(_layer))
                _animator.Play(stateHash, _layer, 0f);
            else
                _animator.CrossFadeInFixedTime(stateHash, duration, _layer);
        }

        private void AttachToHand()
        {
            AttachTo(HandSocket);
            _swordInHand = true;
        }

        private void AttachToBack()
        {
            AttachTo(BackSocket);
            _swordInHand = false;
        }

#if UNITY_EDITOR
        // ---- Placement helpers (right-click the component header) ----
        // Workflow: preview a socket, move/rotate the SWORD in the Scene view until the hilt sits right,
        // then "Save Sword Placement To Socket". The socket takes the sword's pose and the sword goes back to
        // zero offset, which is exactly what AttachTo reproduces at runtime. The grip is relative to the hand
        // bone, so placing it in the T-pose works for every animation; to check against the real grip, preview
        // a clip (e.g. CombatIdle) in the Animation window with PlayerArmature selected.

        [ContextMenu("Sword Placement/Preview In Hand")]
        private void PreviewInHand() => Preview(HandSocket);

        [ContextMenu("Sword Placement/Preview On Back")]
        private void PreviewOnBack() => Preview(BackSocket);

        [ContextMenu("Sword Placement/Save Sword Placement To Socket")]
        private void SavePlacementToSocket()
        {
            if (Sword == null) return;
            Transform socket = Sword.parent;
            if (socket != HandSocket && socket != BackSocket)
            {
                Debug.LogWarning($"{nameof(SwordHolster)}: the sword isn't on a socket; use Preview In Hand / On Back first.", this);
                return;
            }

            Vector3 worldScale = Sword.lossyScale;
            UnityEditor.Undo.RecordObjects(new Object[] { socket, Sword }, "Save Sword Placement");
            socket.SetPositionAndRotation(Sword.position, Sword.rotation); // children follow...
            Sword.localPosition = Vector3.zero;                             // ...so re-zero the sword
            Sword.localRotation = Quaternion.identity;
            Sword.localScale = Divide(worldScale, socket.lossyScale);
            Debug.Log($"{nameof(SwordHolster)}: saved the sword placement into '{socket.name}'.", socket);
        }

        private void Preview(Transform socket)
        {
            if (Sword == null || socket == null)
            {
                Debug.LogWarning($"{nameof(SwordHolster)}: assign the Sword and both sockets first.", this);
                return;
            }

            Vector3 worldScale = Sword.lossyScale;
            UnityEditor.Undo.SetTransformParent(Sword, socket, "Preview Sword Placement");
            UnityEditor.Undo.RecordObject(Sword, "Preview Sword Placement");
            Sword.localPosition = Vector3.zero;
            Sword.localRotation = Quaternion.identity;
            Sword.localScale = Divide(worldScale, socket.lossyScale);
            UnityEditor.Selection.activeTransform = Sword; // ready to rotate in the Scene view
        }
#endif

        private static Vector3 Divide(Vector3 a, Vector3 b) => new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);

        private void AttachTo(Transform socket)
        {
            if (Sword == null || socket == null) return;
            if (!_worldScaleCaptured)
            {
                _swordWorldScale = Sword.lossyScale;
                _worldScaleCaptured = true;
            }

            Sword.SetParent(socket, false);
            Sword.localPosition = Vector3.zero;
            Sword.localRotation = Quaternion.identity;

            // Keep the sword's original world size: Lillian's bones carry a 100x scale, which a plain
            // re-parent would apply to the sword.
            Sword.localScale = Divide(_swordWorldScale, socket.lossyScale);
        }
    }
}
