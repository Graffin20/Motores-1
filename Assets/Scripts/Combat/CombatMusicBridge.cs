using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Player-only: drives the teammate's AdaptiveMusic (MusicManager prefab). Combat music plays only while
    /// at least one enemy is engaging the player (CombatStateTracker.EngagedEnemyCount), NOT on the player's
    /// own actions (attacking, rolling, locking on), which also count as "in combat" for the camera and
    /// stance but shouldn't start the music by themselves. When GameManager.levelComplete turns true it calls
    /// Win() once and stops following combat.
    ///
    /// AdaptiveMusic owns the mixer snapshots and transition times; this only decides WHEN. The singleton is
    /// read lazily: the MusicManager may initialise after the player, and test scenes may have none, in which
    /// case nothing happens.
    /// </summary>
    [RequireComponent(typeof(CombatStateTracker))]
    public class CombatMusicBridge : MonoBehaviour
    {
        [Tooltip("Seconds with no engaged enemy before the music returns to exploration, so an enemy briefly " +
                 "stepping out of range doesn't flick the music back and forth.")]
        public float ExitDelay = 2f;

        private CombatStateTracker _tracker;
        private bool _combatMusic;
        private float _exitTimer;
        private bool _won;

        private void Awake()
        {
            _tracker = GetComponent<CombatStateTracker>();
        }

        private void Update()
        {
            if (_won) return;

            if (GameManager.Instance != null && GameManager.Instance.levelComplete)
            {
                _won = true;
                if (AdaptiveMusic.Instance != null) AdaptiveMusic.Instance.Win();
                return;
            }

            if (_tracker.EngagedEnemyCount > 0)
            {
                _exitTimer = ExitDelay;
                SetCombatMusic(true);
            }
            else if (_combatMusic)
            {
                _exitTimer -= Time.deltaTime;
                if (_exitTimer <= 0f) SetCombatMusic(false);
            }
        }

        private void SetCombatMusic(bool combat)
        {
            if (combat == _combatMusic) return;
            if (AdaptiveMusic.Instance == null) return; // retried next frame, e.g. MusicManager not up yet
            _combatMusic = combat;

            if (combat) AdaptiveMusic.Instance.Combat();
            else AdaptiveMusic.Instance.Exploration();
        }
    }
}
