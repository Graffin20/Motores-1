using StarterAssets.Combat;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Scene-view editing of BossAI's stomp cone (the area behind the boss that triggers the stomp): a filled
/// cone on the ground, a handle on its back edge for StompRange and handles on its sides for
/// StompConeAngle (both sides move together; the cone stays centred on the boss's back). Undo-able, and
/// it works in Prefab Mode. Turns red while the player is inside it in Play mode.
/// </summary>
[CustomEditor(typeof(BossAI))]
public class BossAIEditor : Editor
{
    private static readonly Color FillColor = new(1f, 0f, 1f, 0.12f);
    private static readonly Color EdgeColor = new(1f, 0f, 1f, 0.9f);
    private static readonly Color ActiveFillColor = new(1f, 0.1f, 0.1f, 0.25f);

    private void OnSceneGUI()
    {
        var boss = (BossAI)target;
        Transform t = boss.transform;

        Vector3 up = Vector3.up;
        Vector3 centre = t.position + up * 0.02f; // just above the ground so the fill isn't z-fighting
        Vector3 back = -t.forward;
        back.y = 0f;
        if (back.sqrMagnitude < 0.0001f) back = Vector3.back;
        back.Normalize();

        float halfAngle = boss.StompConeAngle * 0.5f;
        float range = boss.StompRange;
        Vector3 leftEdge = Quaternion.AngleAxis(-halfAngle, up) * back;
        Vector3 rightEdge = Quaternion.AngleAxis(halfAngle, up) * back;

        // Filled cone + outline.
        Handles.color = PlayerInside(boss) ? ActiveFillColor : FillColor;
        Handles.DrawSolidArc(centre, up, leftEdge, boss.StompConeAngle, range);
        Handles.color = EdgeColor;
        Handles.DrawWireArc(centre, up, leftEdge, boss.StompConeAngle, range, 2f);
        if (boss.StompConeAngle < 360f)
        {
            Handles.DrawLine(centre, centre + leftEdge * range, 2f);
            Handles.DrawLine(centre, centre + rightEdge * range, 2f);
        }

        // Range handle: slides along the boss's back direction.
        Vector3 rangePos = centre + back * range;
        float size = HandleUtility.GetHandleSize(rangePos) * 0.08f;
        EditorGUI.BeginChangeCheck();
        Vector3 newRangePos = Handles.Slider(rangePos, back, size, Handles.DotHandleCap, 0f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(boss, "Edit Stomp Range");
            boss.StompRange = Mathf.Max(0.1f, Vector3.Dot(newRangePos - centre, back));
        }

        // Angle handles on both sides, dragged on the ground plane.
        DrawAngleHandle(boss, centre, back, up, centre + leftEdge * range);
        DrawAngleHandle(boss, centre, back, up, centre + rightEdge * range);

        Handles.Label(rangePos + up * 0.3f, $"Stomp  {boss.StompConeAngle:0}°  {boss.StompRange:0.0} m", EditorStyles.boldLabel);
    }

    private static void DrawAngleHandle(BossAI boss, Vector3 centre, Vector3 back, Vector3 up, Vector3 position)
    {
        float size = HandleUtility.GetHandleSize(position) * 0.08f;
        EditorGUI.BeginChangeCheck();
        Vector3 moved = Handles.Slider2D(position, up, back, Vector3.Cross(up, back), size, Handles.SphereHandleCap, Vector2.zero);
        if (!EditorGUI.EndChangeCheck()) return;

        Vector3 flat = moved - centre;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) return;

        Undo.RecordObject(boss, "Edit Stomp Cone Angle");
        boss.StompConeAngle = Mathf.Clamp(Vector3.Angle(back, flat) * 2f, 1f, 360f);
    }

    /// <summary>Play mode only: is the Player-tagged object currently inside the cone?</summary>
    private static bool PlayerInside(BossAI boss)
    {
        if (!Application.isPlaying) return false;
        GameObject player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) return false;

        Vector3 toPlayer = player.transform.position - boss.transform.position;
        toPlayer.y = 0f;
        Vector3 forward = boss.transform.forward;
        forward.y = 0f;
        return boss.IsInStompCone(toPlayer.magnitude, Vector3.Angle(forward, toPlayer));
    }
}
