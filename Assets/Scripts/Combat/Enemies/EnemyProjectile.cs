using System.Collections.Generic;
using UnityEngine;

namespace StarterAssets.Combat
{
    /// <summary>
    /// Proyectil de enemigo. Se mueve en línea recta con SphereCast (no atraviesa paredes
    /// aunque sea rápido), ignora al personaje que lo disparó y se destruye al impactar.
    /// No necesita Rigidbody ni Collider: solo el visual.
    ///
    /// Parry: el HitInfo es parryable con una ventana de ParryWindowBeforeImpact segundos que termina en
    /// el impacto (un proyectil no tiene clip con AE_ParryWindowOpen/Close, así que la ventana sale de su
    /// llegada). BlockController lo resuelve igual que un golpe melee. Si el resultado es Parried, el
    /// proyectil no se destruye: pasa a ser del defensor (Attacker = el que hizo parry, ignora a su nuevo
    /// dueño), sale hacia donde mira el defensor y usa ReflectedHitMask, así puede dañar enemigos como si
    /// fuera un golpe del jugador.
    /// </summary>
    public class EnemyProjectile : MonoBehaviour
    {
        [Header("Defaults (RangedAttackBehavior los sobreescribe al disparar)")]
        public float Speed = 18f;
        public float Damage = 10f;

        [Header("Collision")]
        [Tooltip("Radio de colisión del proyectil.")]
        public float Radius = 0.15f;

        [Tooltip("Capas con las que choca: incluí la del Player y las del entorno.")]
        public LayerMask HitMask = ~0;

        [Tooltip("Segundos antes de autodestruirse si no choca con nada.")]
        public float Lifetime = 5f;

        [Header("Parry")]
        [Tooltip("Si está activo, un parry a tiempo devuelve el proyectil en vez de solo bloquearlo.")]
        public bool CanBeParried = true;

        [Tooltip("La ventana de parry son estos segundos ANTES del impacto: un toque de bloqueo dentro de ella " +
                 "es parry. Mantener el bloqueo desde antes solo bloquea (igual que en melee).")]
        public float ParryWindowBeforeImpact = 0.25f;

        [Tooltip("Capas con las que choca después de un parry: enemigos (su CharacterController y/o las capas de " +
                 "sus hurtboxes, p. ej. Enemies y Ragdoll) y el entorno.")]
        public LayerMask ReflectedHitMask = ~0;

        public float ReflectedSpeedMultiplier = 1.5f;
        public float ReflectedDamageMultiplier = 1f;

        [Tooltip("Jugador: el proyectil devuelto va hacia lo que está en el centro de la cámara. Activado = solo " +
                 "la dirección horizontal de la cámara (no apunta hacia arriba/abajo, nunca se clava en el suelo " +
                 "por la inclinación de la cámara). Sin cámara (p. ej. un enemigo) usa hacia dónde mira el defensor.")]
        public bool ReflectHorizontalOnly = false;

        [Tooltip("Distancia del punto de mira si el rayo de la cámara no toca nada.")]
        public float ReflectAimDistance = 100f;

        [Tooltip("Cuántas veces puede devolverse el mismo proyectil (por si algún día un enemigo también hace parry).")]
        public int MaxReflections = 1;

        [Header("Effects (Optional)")]
        [Tooltip("Se instancia en el punto de impacto.")]
        public GameObject ImpactEffect;

        [Tooltip("Se instancia donde el proyectil es devuelto por un parry.")]
        public GameObject ReflectEffect;

        private static readonly RaycastHit[] HitBuffer = new RaycastHit[8];

        // Dueño = personaje que lo lanzó (o el que lo devolvió). Se ignora con IsChildOf, así sus
        // hurtboxes hijas tampoco cuentan.
        private Transform _owner;
        private Vector3 _direction = Vector3.forward;
        private float _age;
        private int _reflections;

        // Personajes que lo esquivaron con invulnerabilidad (i-frames del roll, post-parry): el proyectil los
        // atraviesa y no vuelve a chocar con ellos, aunque la invulnerabilidad termine mientras sigue dentro.
        private readonly List<Transform> _passedThrough = new();

        public bool IsReflected => _reflections > 0;

        public void Initialize(Transform owner, Vector3 direction, float speed, float damage)
        {
            _owner = owner;
            _direction = direction.normalized;
            Speed = speed;
            Damage = damage;
            transform.rotation = Quaternion.LookRotation(_direction);
        }

        private void Update()
        {
            _age += Time.deltaTime;
            if (_age >= Lifetime)
            {
                Destroy(gameObject);
                return;
            }

            float step = Speed * Time.deltaTime;
            LayerMask mask = IsReflected ? ReflectedHitMask : HitMask;

            int count = Physics.SphereCastNonAlloc(transform.position, Radius, _direction, HitBuffer, step,
                mask, QueryTriggerInteraction.Ignore);

            // Elegir el impacto más cercano que no sea el propio dueño.
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (_owner != null && HitBuffer[i].transform.IsChildOf(_owner)) continue;
                if (IsPassedThrough(HitBuffer[i].transform)) continue;

                // Un collider en el prefab del proyectil (el propio, o el de otro proyectil) no es un impacto:
                // el SphereCast devuelve a distancia 0 lo que ya solapa, así que con una máscara que incluya su
                // capa se "chocaría" consigo mismo y se destruiría al instante.
                if (HitBuffer[i].collider.GetComponentInParent<EnemyProjectile>() != null) continue;

                if (HitBuffer[i].distance < bestDistance)
                {
                    bestDistance = HitBuffer[i].distance;
                    best = i;
                }
            }

            // Impact devuelve false si lo atravesó (i-frames): en ese caso avanza este frame igual.
            if (best >= 0 && Impact(HitBuffer[best]))
            {
                return;
            }

            transform.position += _direction * step;
        }

        /// <summary>Devuelve true si el proyectil se detuvo (destruido o devuelto por parry).</summary>
        private bool Impact(RaycastHit hit)
        {
            // distance 0 = el SphereCast ya empezó solapado: point no es válido, usar la posición actual.
            Vector3 point = hit.distance > 0f ? hit.point : transform.position;

            HitResult result = OnHitTarget(hit.collider, point, out Component defender);

            // Esquivado con i-frames: sigue de largo en vez de destruirse.
            if (result == HitResult.Invulnerable && defender != null)
            {
                _passedThrough.Add(defender.transform);
                return false;
            }

            if (result == HitResult.Parried && CanBeParried && _reflections < MaxReflections && defender != null)
            {
                Reflect(defender.transform, point);
                return true;
            }

            if (ImpactEffect != null)
            {
                Instantiate(ImpactEffect, point, Quaternion.LookRotation(hit.distance > 0f ? hit.normal : -_direction));
            }

            Destroy(gameObject);
            return true;
        }

        private bool IsPassedThrough(Transform hit)
        {
            for (int i = _passedThrough.Count - 1; i >= 0; i--)
            {
                if (_passedThrough[i] == null) { _passedThrough.RemoveAt(i); continue; } // destruido
                if (hit.IsChildOf(_passedThrough[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// Daña al IDamageable golpeado (buscado hacia arriba: las hurtboxes de los enemigos son colliders
        /// de sus huesos y la Health está en la raíz). Devuelve el resultado para poder reaccionar al parry.
        /// </summary>
        protected virtual HitResult OnHitTarget(Collider hit, Vector3 point, out Component defender)
        {
            defender = null;
            IDamageable damageable = hit.GetComponentInParent<IDamageable>();
            if (damageable == null) return HitResult.Ignored; // pared, suelo...

            defender = damageable as Component;
            return damageable.TakeDamage(BuildHitInfo(point));
        }

        private HitInfo BuildHitInfo(Vector3 point)
        {
            float now = Time.time;
            return new HitInfo(Damage, transform.position)
            {
                Attacker = _owner != null ? _owner.gameObject : null,
                IsParryable = CanBeParried && _reflections < MaxReflections,
                ParryWindowOpenTime = now - ParryWindowBeforeImpact,
                ParryWindowCloseTime = now,
                HasContact = true,
                ContactPoint = point,
                ContactDirection = _direction,
            };
        }

        /// <summary>El proyectil cambia de bando: dueño = defensor, sale hacia donde mira (horizontal, a la
        /// altura actual) y con la máscara de proyectil devuelto.</summary>
        private void Reflect(Transform defender, Vector3 point)
        {
            _reflections++;
            _owner = defender;
            _age = 0f;

            Vector3 direction = GetReflectDirection(defender);
            if (ReflectHorizontalOnly) direction.y = 0f;
            _direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : -_direction;

            Speed *= ReflectedSpeedMultiplier;
            Damage *= ReflectedDamageMultiplier;
            transform.rotation = Quaternion.LookRotation(_direction);

            if (ReflectEffect != null)
            {
                Instantiate(ReflectEffect, point, transform.rotation);
            }
        }

        /// <summary>
        /// Con cámara (jugador): hacia el punto que la cámara tiene en el centro de la pantalla, no paralelo a su
        /// forward: la cámara está detrás y al costado del personaje, y una dirección paralela pasaría al lado del
        /// objetivo apuntado. Sin cámara: el forward del defensor.
        /// </summary>
        private Vector3 GetReflectDirection(Transform defender)
        {
            Transform cam = defender.TryGetComponent(out ThirdPersonController controller) ? controller.CameraTransform : null;
            if (cam == null) return defender.forward;

            Vector3 forward = cam.forward;
            if (ReflectHorizontalOnly)
            {
                forward.y = 0f;
                return forward;
            }

            // El rayo empieza a la altura del proyectil a lo largo de la línea de la cámara, así lo que hay entre
            // la cámara y el personaje (paredes, el propio personaje) no cuenta como punto de mira.
            float startAlong = Mathf.Max(0f, Vector3.Dot(transform.position - cam.position, forward));
            Vector3 origin = cam.position + forward * startAlong;
            Vector3 aimPoint = origin + forward * ReflectAimDistance;

            int count = Physics.RaycastNonAlloc(origin, forward, HitBuffer, ReflectAimDistance,
                ReflectedHitMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                RaycastHit h = HitBuffer[i];
                if (h.transform.IsChildOf(defender)) continue;
                if (h.collider.GetComponentInParent<EnemyProjectile>() != null) continue;
                if (h.distance < best)
                {
                    best = h.distance;
                    aimPoint = h.point;
                }
            }

            Vector3 direction = aimPoint - transform.position;
            // Punto de mira pegado al proyectil (o detrás): la dirección no sirve, usar la de la cámara.
            return Vector3.Dot(direction, forward) > 0.5f ? direction : forward;
        }

        [Header("Debug")]
        [Tooltip("Dibuja en la Scene view la esfera de colisión y el barrido de este frame (rojo = del enemigo, " +
                 "cian = devuelto). Sin seleccionar, para ver los proyectiles en vuelo.")]
        public bool DrawCastGizmo = true;

        private void OnDrawGizmos()
        {
            if (!DrawCastGizmo) return;

            // En edición no hay dirección ni deltaTime todavía: forward del prefab y un frame a 60 fps.
            Vector3 direction = Application.isPlaying ? _direction : transform.forward;
            float step = Speed * (Application.isPlaying ? Time.deltaTime : 1f / 60f);
            Vector3 start = transform.position;
            Vector3 end = start + direction * step;

            Gizmos.color = IsReflected ? Color.cyan : Color.red;
            Gizmos.DrawWireSphere(start, Radius);
            Gizmos.DrawWireSphere(end, Radius);

            // Contorno del barrido (cápsula start->end) en dos planos perpendiculares a la dirección.
            Vector3 side = Vector3.Cross(direction, Vector3.up);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(direction, Vector3.right);
            side = side.normalized * Radius;
            Vector3 up = Vector3.Cross(side, direction).normalized * Radius;
            foreach (Vector3 offset in new[] { side, -side, up, -up })
            {
                Gizmos.DrawLine(start + offset, end + offset);
            }

            // Hacia dónde va, más largo que un frame para que se lea la dirección.
            Gizmos.DrawLine(end, end + direction * Mathf.Max(Radius * 4f, 0.5f));
        }
    }
}
