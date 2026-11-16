using System.Collections.Generic;
using PeakVRoid.Vrm;
using UnityEngine;

namespace PeakVRoid.Avatar
{
    /// <summary>Drives the VRM "blink" expression at natural random intervals.</summary>
    public sealed class Blinker
    {
        private struct Bind
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public float Weight;
        }

        private readonly List<Bind> binds = new List<Bind>();
        private float timer;
        private float phase = -1f; // <0 = eyes open, waiting
        private const float Duration = 0.16f;

        public Blinker(VrmModel model, Transform[] t)
        {
            foreach (var b in model.Blink)
            {
                if (!t[b.Renderer].TryGetComponent<SkinnedMeshRenderer>(out var smr) || smr.sharedMesh == null) continue;
                if (b.Index < 0 || b.Index >= smr.sharedMesh.blendShapeCount) continue;
                binds.Add(new Bind { Renderer = smr, Index = b.Index, Weight = b.Weight });
            }
            timer = Random.Range(1f, 4f);
        }

        public void Update(float dt)
        {
            if (binds.Count == 0) return;
            float amount = 0f;
            if (phase < 0f)
            {
                timer -= dt;
                if (timer <= 0f) phase = 0f;
            }
            else
            {
                phase += dt;
                float x = phase / Duration;
                amount = x < 0.5f ? x * 2f : 2f - x * 2f;
                if (x >= 1f)
                {
                    phase = -1f;
                    amount = 0f;
                    timer = Random.value < 0.15f ? 0.2f : Random.Range(2f, 6f); // occasional double blink
                }
            }
            amount = Mathf.Clamp01(amount);
            foreach (var b in binds)
                if (b.Renderer != null) b.Renderer.SetBlendShapeWeight(b.Index, b.Weight * amount);
        }
    }
}
