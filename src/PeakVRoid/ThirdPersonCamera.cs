using PeakVRoid.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakVRoid
{
    /// <summary>
    /// Third-person view. PEAK's own camera code keeps running untouched (first-person
    /// position, look direction, ragdoll sway); right before the main camera renders we
    /// move it back over the shoulder, and right after rendering we put it back. Game
    /// logic that reads the camera therefore always sees the original first-person camera.
    /// </summary>
    public static class ThirdPersonCamera
    {
        public static bool Active { get; private set; }

        private static bool hooked;
        private static Camera moved;
        private static Vector3 savedPos;
        private static float currentDistance = -1f;
        private static float side = 1f;
        private static readonly RaycastHit[] hits = new RaycastHit[32];

        public static void Init()
        {
            Active = ModConfig.ThirdPersonOnStart.Value;
            if (hooked) return;
            hooked = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        public static void Shutdown()
        {
            if (!hooked) return;
            hooked = false;
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            Restore();
        }

        public static void Toggle()
        {
            Active = !Active;
            currentDistance = -1f;
            Plugin.Log.LogInfo(Active ? "Third person" : "First person");
        }

        public static void Update()
        {
            if (Keys.Down(ModConfig.ToggleViewKey.Value)) Toggle();
            if (!Active) return;
            float zoom = 0f;
            if (Keys.Held(ModConfig.ZoomInKey.Value)) zoom -= 1f;
            if (Keys.Held(ModConfig.ZoomOutKey.Value)) zoom += 1f;
            if (zoom != 0f)
            {
                ModConfig.CameraDistance.Value = Mathf.Clamp(
                    ModConfig.CameraDistance.Value + zoom * 3f * Time.unscaledDeltaTime,
                    ModConfig.MinDistance.Value, ModConfig.MaxDistance.Value);
            }
            if (Keys.Down(ModConfig.SwapShoulderKey.Value)) side = -side;
        }

        private static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam) => Offset(cam);
        private static void OnEndCamera(ScriptableRenderContext ctx, Camera cam) { if (cam == moved) Restore(); }
        private static void OnPreCull(Camera cam) => Offset(cam);
        private static void OnPostRender(Camera cam) { if (cam == moved) Restore(); }

        private static void Offset(Camera cam)
        {
            if (!Active || moved != null || cam == null || cam.cameraType != CameraType.Game) return;
            var character = Peak.LocalCharacter;
            if (character == null || cam != Peak.MainCamera || Peak.CameraBusy) return;

            var tr = cam.transform;
            Vector3 pivot = tr.position;
            Quaternion rot = tr.rotation;
            Vector3 offset = new Vector3(ModConfig.ShoulderOffset.Value * side, ModConfig.CameraHeight.Value, 0f);
            Vector3 raised = pivot + rot * offset;
            float want = ModConfig.CameraDistance.Value;

            if (ModConfig.CameraCollision.Value)
            {
                raised = Cast(pivot, raised, character.transform, out _);
                Vector3 back = -(rot * Vector3.forward);
                Cast(raised, raised + back * want, character.transform, out float free);
                want = Mathf.Min(want, free);
            }

            // Pull in instantly, ease back out.
            float dt = Time.unscaledDeltaTime;
            if (currentDistance < 0f || want < currentDistance) currentDistance = want;
            else currentDistance = Mathf.MoveTowards(currentDistance, want, dt * 4f);

            moved = cam;
            savedPos = pivot;
            tr.position = raised - rot * Vector3.forward * currentDistance;
        }

        /// <summary>Sphere-cast from a to b ignoring the local player; returns the free end point.</summary>
        private static Vector3 Cast(Vector3 a, Vector3 b, Transform self, out float freeDistance)
        {
            const float radius = 0.2f;
            Vector3 d = b - a;
            float len = d.magnitude;
            freeDistance = len;
            if (len < 1e-4f) return b;
            d /= len;
            int n = Physics.SphereCastNonAlloc(a, radius, d, hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = len;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                if (h.collider == null || h.distance <= 0f) continue;
                var ht = h.collider.attachedRigidbody != null ? h.collider.attachedRigidbody.transform : h.collider.transform;
                if (ht.IsChildOf(self)) continue;
                if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic && h.collider.attachedRigidbody.mass < 20f)
                    continue; // small loose props (items, ropes) shouldn't shove the camera around
                if (h.distance < best) best = h.distance;
            }
            freeDistance = Mathf.Max(0f, best - 0.05f);
            return a + d * freeDistance;
        }

        private static void Restore()
        {
            if (moved != null) moved.transform.position = savedPos;
            moved = null;
        }
    }
}
