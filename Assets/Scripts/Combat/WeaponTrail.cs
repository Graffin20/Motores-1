using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Blade-wide swing trail: a ribbon between the WeaponTrace's Base and Tip, emitted while the trace is
    /// active (AE_AttackActive -> AE_AttackRecovery), so the trail shows exactly where the weapon can hit.
    /// After the window it isn't cut off; each segment fades out over Lifetime.
    ///
    /// Why not a TrailRenderer: that's a line following one point (the tip). This samples both ends every
    /// frame and smooths between samples with a Catmull-Rom curve, so a fast swing that covers 90 degrees in
    /// two frames still draws a round arc instead of a corner.
    ///
    /// The mesh is built in world space on its own unparented object, so the trail stays where the blade
    /// was instead of moving with the character. UV: x = age (0 at the blade, 1 at the oldest end), y = 0 at
    /// Base, 1 at Tip. Vertex colour = ColorOverAge, so the material's shader must use vertex colour (URP:
    /// Universal Render Pipeline/Particles/Unlit, Transparent or Additive).
    /// </summary>
    [RequireComponent(typeof(WeaponTrace))]
    public class WeaponTrail : MonoBehaviour
    {
        [Tooltip("Transparent/additive material using vertex colour, e.g. URP Particles/Unlit with a gradient texture.")]
        public Material Material;
        [Tooltip("Seconds a trail segment lasts.")]
        public float Lifetime = 0.2f;
        [Tooltip("Colour/alpha by age: left = at the blade, right = oldest.")]
        public Gradient ColorOverAge = DefaultGradient();
        [Tooltip("Smoothing segments inserted between two frames' samples.")]
        [Range(1, 12)] public int Subdivisions = 4;
        [Tooltip("Skip a new sample if neither blade end moved more than this (metres); avoids degenerate slivers.")]
        public float MinSampleDistance = 0.01f;

        private struct Sample
        {
            public Vector3 Base, Tip;
            public float Time;
        }

        private WeaponTrace _trace;
        private readonly List<Sample> _samples = new();
        private Mesh _mesh;
        private GameObject _trailObject;
        private readonly List<Vector3> _vertices = new();
        private readonly List<Color> _colors = new();
        private readonly List<Vector2> _uvs = new();
        private readonly List<int> _triangles = new();

        private void Awake()
        {
            _trace = GetComponent<WeaponTrace>();

            _mesh = new Mesh { name = "WeaponTrail" };
            _mesh.MarkDynamic();

            _trailObject = new GameObject($"{name} Trail");
            _trailObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _trailObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            if (Material == null)
                Debug.LogWarning($"{nameof(WeaponTrail)} on '{name}' has no Material; the trail won't render.", this);
        }

        private void OnDestroy()
        {
            if (_trailObject != null) Destroy(_trailObject);
            if (_mesh != null) Destroy(_mesh);
        }

        private void OnDisable()
        {
            _samples.Clear();
            if (_mesh != null) _mesh.Clear();
        }

        // LateUpdate: after the Animator has posed the blade (and after MeleeCombatController started/stopped
        // the trace in its own LateUpdate or the animation events).
        private void LateUpdate()
        {
            float now = Time.time;

            if (_trace.IsTracing && _trace.IsValid) AddSample(now);

            // Drop samples older than Lifetime; keep one extra so the oldest visible segment fades to its end.
            while (_samples.Count > 2 && now - _samples[1].Time > Lifetime) _samples.RemoveAt(0);
            if (_samples.Count > 0 && now - _samples[_samples.Count - 1].Time > Lifetime) _samples.Clear();

            BuildMesh(now);
        }

        private void AddSample(float now)
        {
            var sample = new Sample { Base = _trace.Base.position, Tip = _trace.Tip.position, Time = now };

            if (_samples.Count > 0)
            {
                var last = _samples[_samples.Count - 1];
                bool moved = (sample.Base - last.Base).sqrMagnitude > MinSampleDistance * MinSampleDistance ||
                             (sample.Tip - last.Tip).sqrMagnitude > MinSampleDistance * MinSampleDistance;
                if (!moved)
                {
                    // Keep the head glued to the blade (fresh time) without adding a sliver.
                    last.Time = now;
                    _samples[_samples.Count - 1] = last;
                    return;
                }
            }
            _samples.Add(sample);
        }

        private void BuildMesh(float now)
        {
            _mesh.Clear();
            if (_samples.Count < 2 || Material == null) return;

            _vertices.Clear();
            _colors.Clear();
            _uvs.Clear();
            _triangles.Clear();

            // Walk from newest to oldest so UV.x / colour go blade -> tail.
            int segments = _samples.Count - 1;
            for (int s = segments; s >= 1; s--)
            {
                int stepsHere = s == 1 ? Subdivisions + 1 : Subdivisions; // last segment also emits its far end
                for (int k = 0; k < stepsHere; k++)
                {
                    float t = (float)k / Subdivisions; // 0 = sample s, 1 = sample s-1
                    Vector3 basePoint = CatmullRom(BaseAt(s + 1), BaseAt(s), BaseAt(s - 1), BaseAt(s - 2), t);
                    Vector3 tipPoint = CatmullRom(TipAt(s + 1), TipAt(s), TipAt(s - 1), TipAt(s - 2), t);
                    float time = Mathf.Lerp(_samples[s].Time, _samples[s - 1].Time, t);
                    float age = Mathf.Clamp01((now - time) / Mathf.Max(0.0001f, Lifetime));
                    Color color = ColorOverAge.Evaluate(age);

                    _vertices.Add(basePoint);
                    _vertices.Add(tipPoint);
                    _colors.Add(color);
                    _colors.Add(color);
                    _uvs.Add(new Vector2(age, 0f));
                    _uvs.Add(new Vector2(age, 1f));
                }
            }

            for (int i = 0; i + 3 < _vertices.Count; i += 2)
            {
                // Two triangles per quad, both windings so the ribbon is visible from either side.
                _triangles.Add(i); _triangles.Add(i + 1); _triangles.Add(i + 2);
                _triangles.Add(i + 2); _triangles.Add(i + 1); _triangles.Add(i + 3);
                _triangles.Add(i + 2); _triangles.Add(i + 1); _triangles.Add(i);
                _triangles.Add(i + 3); _triangles.Add(i + 1); _triangles.Add(i + 2);
            }

            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }

        private Vector3 BaseAt(int i) => _samples[Mathf.Clamp(i, 0, _samples.Count - 1)].Base;
        private Vector3 TipAt(int i) => _samples[Mathf.Clamp(i, 0, _samples.Count - 1)].Tip;

        /// <summary>Point between p1 (t = 0) and p2 (t = 1), shaped by the neighbours p0 and p3.</summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }

        private static Gradient DefaultGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }
    }
}
