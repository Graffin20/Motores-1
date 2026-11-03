namespace StarterAssets.Combat
{
    /// <summary>
    /// Something on a character that must be ready before it can block (e.g. the player's sword must be
    /// drawn). BlockController gathers every sibling implementing this in Awake; with none, blocking is
    /// always ready, so enemies and other characters are unaffected.
    /// </summary>
    public interface IBlockReadiness
    {
        bool IsReadyToBlock { get; }
    }
}
