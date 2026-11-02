using UnityEngine;

/// <summary>
/// Everything the defender needs to resolve one incoming hit, bundled so adding a new piece of
/// hit data (parry timing, attacker reference, ...) doesn't grow every TakeDamage signature.
/// </summary>
public struct HitInfo
{
    public float Damage;
    public Vector3 SourcePosition;
    public float KnockbackDistanceMultiplier;
    public float KnockbackDurationMultiplier;

    /// <summary>The attacker's character GameObject (the one with its MeleeCombatController), or null if the hit has no attacker (traps, hazards...).</summary>
    public GameObject Attacker;

    /// <summary>True if the attacker's clip opened a parry window (AE_ParryWindowOpen) during this attack.
    /// False = unparryable; the defender can still block it.</summary>
    public bool IsParryable;
    /// <summary>Time.time the parry window opened. Only meaningful when IsParryable.</summary>
    public float ParryWindowOpenTime;
    /// <summary>Time.time the parry window closed, or the hit time if it was still open when the hit landed.</summary>
    public float ParryWindowCloseTime;

    /// <summary>True if ContactPoint/ContactDirection are set (weapon trace or hitbox sphere hits). Hits
    /// without one (hazards...) leave it false; hit FX then fall back to the defender's chest.</summary>
    public bool HasContact;
    /// <summary>World point where the weapon touched the defender's collider.</summary>
    public Vector3 ContactPoint;
    /// <summary>Direction the weapon was moving at the contact (normalized), e.g. to aim sparks along the swing.</summary>
    public Vector3 ContactDirection;

    public HitInfo(float damage, Vector3 sourcePosition,
                   float knockbackDistanceMultiplier = 1f, float knockbackDurationMultiplier = 1f)
    {
        Damage = damage;
        SourcePosition = sourcePosition;
        KnockbackDistanceMultiplier = knockbackDistanceMultiplier;
        KnockbackDurationMultiplier = knockbackDurationMultiplier;
        Attacker = null;
        IsParryable = false;
        ParryWindowOpenTime = 0f;
        ParryWindowCloseTime = 0f;
        HasContact = false;
        ContactPoint = Vector3.zero;
        ContactDirection = Vector3.zero;
    }
}

/// <summary>
/// What happened to a hit, returned to the attacker so it can react (e.g. stagger itself when
/// Parried) without reaching into the defender's components.
/// </summary>
public enum HitResult
{
    /// <summary>Target was already dead; nothing happened.</summary>
    Ignored,
    /// <summary>Negated by an invulnerability source (roll i-frames, post-parry window...).</summary>
    Invulnerable,
    /// <summary>Full damage taken, defender staggered.</summary>
    Hit,
    /// <summary>Fully absorbed by the block (stamina paid).</summary>
    Blocked,
    /// <summary>Block ran out of stamina: defender staggered and took the damage stamina couldn't cover.</summary>
    GuardBroken,
    /// <summary>Defender pressed block inside the attacker's parry window: no damage, attacker should stagger.</summary>
    Parried,
    /// <summary>The hit (or the part of it that got through a guard break) killed the defender.</summary>
    Killed
}
