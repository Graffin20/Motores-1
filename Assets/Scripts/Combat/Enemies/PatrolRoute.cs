using System.Collections.Generic;
using UnityEngine;

namespace StarterAssets.Combat
{
    public enum PatrolMode
    {
        Loop,      // 0 -> 1 -> 2 -> 0 -> ...
        PingPong,  // 0 -> 1 -> 2 -> 1 -> 0 -> ...
        Once       // 0 -> 1 -> 2 y se queda ahí
    }

    /// <summary>
    /// Ruta de patrulla reutilizable. Se pone en un GameObject vacío.
    /// Los waypoints se toman de la lista; si la lista está vacía, se usan los hijos
    /// de este GameObject en orden. Varios enemigos pueden compartir la misma ruta.
    /// </summary>
    public class PatrolRoute : MonoBehaviour
    {
        [Tooltip("Cómo se recorre la ruta.")]
        public PatrolMode Mode = PatrolMode.Loop;

        [Tooltip("Segundos de espera (mín, máx) al llegar a cada waypoint. Se elige un valor al azar.")]
        public Vector2 WaitTimeRange = new Vector2(1f, 2f);

        [Tooltip("Opcional. Si está vacío se usan los hijos de este objeto, en orden.")]
        public List<Transform> Waypoints = new List<Transform>();

        [Header("Gizmos")]
        public Color GizmoColor = Color.cyan;

        private readonly List<Transform> _points = new List<Transform>();
        private bool _initialized;

        public int Count
        {
            get { EnsureInitialized(); return _points.Count; }
        }

        public Vector3 GetPosition(int index)
        {
            EnsureInitialized();
            return _points[Mathf.Clamp(index, 0, _points.Count - 1)].position;
        }

        public float GetRandomWait()
        {
            float min = Mathf.Max(0f, Mathf.Min(WaitTimeRange.x, WaitTimeRange.y));
            float max = Mathf.Max(0f, Mathf.Max(WaitTimeRange.x, WaitTimeRange.y));
            return Random.Range(min, max);
        }

        /// <summary>Devuelve el índice del nodo más cercano a una posición (ignorando altura).</summary>
        public int GetNearestIndex(Vector3 position)
        {
            EnsureInitialized();
            int best = 0;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < _points.Count; i++)
            {
                Vector3 d = _points[i].position - position;
                d.y = 0f;
                float sqr = d.sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Siguiente índice según el modo. El estado (dirección) lo lleva cada enemigo,
        /// así la ruta se puede compartir. Devuelve -1 si la ruta terminó (modo Once).
        /// </summary>
        public int GetNextIndex(int current, ref int direction)
        {
            int n = Count;
            if (n <= 1) return Mode == PatrolMode.Once ? -1 : 0;

            switch (Mode)
            {
                case PatrolMode.PingPong:
                    int next = current + direction;
                    if (next >= n || next < 0)
                    {
                        direction = -direction;
                        next = current + direction;
                    }
                    return next;

                case PatrolMode.Once:
                    return current + 1 >= n ? -1 : current + 1;

                default: // Loop
                    return (current + 1) % n;
            }
        }

        private void Awake()
        {
            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            Gather(_points);
            _initialized = true;
        }

        private void Gather(List<Transform> dst)
        {
            dst.Clear();

            foreach (var t in Waypoints)
            {
                if (t != null) dst.Add(t);
            }

            if (dst.Count == 0)
            {
                foreach (Transform child in transform) dst.Add(child);
            }
        }

        private void OnDrawGizmos()
        {
            var pts = new List<Transform>();
            Gather(pts);
            if (pts.Count == 0) return;

            Gizmos.color = GizmoColor;
            for (int i = 0; i < pts.Count; i++)
            {
                Gizmos.DrawSphere(pts[i].position, 0.2f);

                bool isLast = i == pts.Count - 1;
                if (!isLast)
                {
                    Gizmos.DrawLine(pts[i].position, pts[i + 1].position);
                }
                else if (Mode == PatrolMode.Loop && pts.Count > 2)
                {
                    Gizmos.DrawLine(pts[i].position, pts[0].position);
                }
            }
        }
    }
}