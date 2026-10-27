using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Ataque cuerpo a cuerpo: es la lógica de ataque que antes estaba dentro de EnemyAI.
    /// Usa AIMeleeCombatInput + MeleeCombatController tal cual.
    /// </summary>
    [RequireComponent(typeof(AIMeleeCombatInput))]
    [RequireComponent(typeof(MeleeCombatController))]
    public class MeleeAttackBehavior : EnemyAttackBehavior
    {
        [Header("Attack Decision Weights")]
        [Range(0f, 1f)]
        [Tooltip("Chance to use a heavy attack instead of a light attack when in range.")]
        public float HeavyAttackChance = 0.25f;

        private AIMeleeCombatInput _aiInput;
        private MeleeCombatController _combatController;

        public override bool IsAttacking => _combatController != null && _combatController.IsAttacking;

        private void Awake()
        {
            _aiInput = GetComponent<AIMeleeCombatInput>();
            _combatController = GetComponent<MeleeCombatController>();
        }

        protected override void Execute(Transform target)
        {
            if (Random.value < HeavyAttackChance)
            {
                _aiInput.RequestHeavyAttack();
            }
            else
            {
                _aiInput.RequestAttack();
            }
        }
    }
}
