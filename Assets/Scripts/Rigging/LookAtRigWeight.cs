using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace StarterAssets
{
    /// <summary>
    /// Fades the look-at Rig out when its target goes behind the character, so she looks straight ahead instead
    /// of jittering against the Multi-Aim limits trying to follow something she can't turn her head to.
    ///
    /// The angle is measured horizontally (on the character's ground plane) between the body's forward and the
    /// direction from the head to the target, so looking up/down never disables it. Two thresholds (hysteresis)
    /// stop it flickering when the target sits right on the boundary, and the weight is blended rather than
    /// switched so the head eases back to forward.
    /// </summary>
    public class LookAtRigWeight : MonoBehaviour
    {
        [Tooltip("The look-at Rig (LookAtRig) whose weight this drives.")]
        public Rig Rig;

        [Tooltip("What she looks at. Empty = the first source of the first Multi-Aim Constraint under the Rig.")]
        public Transform Target;

        [Tooltip("Where the angle is measured from. Empty = this transform. The head bone gives the most accurate result.")]
        public Transform Head;

        [Tooltip("Facing reference (its forward = straight ahead). Empty = this transform (the character root, which ThirdPersonController rotates).")]
        public Transform Body;

        [Tooltip("Horizontal angle from straight ahead beyond which the rig fades out. Keep it around the Multi-Aim limits.")]
        [Range(0f, 180f)] public float MaxAngle = 100f;

        [Tooltip("The rig only fades back in once the target is this many degrees inside MaxAngle, so it doesn't flicker on the boundary.")]
        [Range(0f, 45f)] public float Hysteresis = 10f;

        [Tooltip("Rig weight while the target is in front.")]
        [Range(0f, 1f)] public float ActiveWeight = 1f;

        [Tooltip("How fast the weight blends, in weight units per second (4 = a quarter second for a full fade).")]
        [Min(0.01f)] public float BlendSpeed = 4f;

        private bool _targetInFront = true;

        private void Awake()
        {
            if (Body == null) Body = transform;
            if (Head == null) Head = transform;

            if (Target == null && Rig != null)
            {
                var aim = Rig.GetComponentInChildren<MultiAimConstraint>();
                if (aim != null && aim.data.sourceObjects.Count > 0) Target = aim.data.sourceObjects[0].transform;
            }

            if (Rig == null || Target == null)
            {
                Debug.LogWarning("[LookAtRigWeight] Rig or Target missing; disabled.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            Vector3 up = Body.up;
            Vector3 forward = Vector3.ProjectOnPlane(Body.forward, up);
            Vector3 toTarget = Vector3.ProjectOnPlane(Target.position - Head.position, up);

            // Target straight above/below: keep the previous decision rather than reading a meaningless angle.
            if (toTarget.sqrMagnitude > 1e-6f && forward.sqrMagnitude > 1e-6f)
            {
                float angle = Vector3.Angle(forward, toTarget);
                if (_targetInFront && angle > MaxAngle) _targetInFront = false;
                else if (!_targetInFront && angle < MaxAngle - Hysteresis) _targetInFront = true;
            }

            float wanted = _targetInFront ? ActiveWeight : 0f;
            Rig.weight = Mathf.MoveTowards(Rig.weight, wanted, BlendSpeed * Time.deltaTime);
        }
    }
}
