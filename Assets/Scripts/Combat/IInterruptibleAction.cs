namespace StarterAssets.Combat
{
    /// <summary>
    /// An action driven by animation events that a stagger or death must cancel explicitly: the Any State
    /// reaction cuts its clip, so its own end event never fires. Health gathers every sibling implementing
    /// this in Awake and calls ForceCancel from Stagger / BeginHold / Die. (MeleeCombatController and
    /// RollController predate this and are still cancelled by name.)
    /// </summary>
    public interface IInterruptibleAction
    {
        void ForceCancel();
    }
}
