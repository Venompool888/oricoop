using System;
using System.Collections.Generic;
using UnityEngine;

namespace OriCoop
{
    // T-095: on the 删除服务器 page the blue wisp behind the selected row turns red (owner's sketch: the red pulse means "delete").
    // The wisp is each item's "glows" child (TransparencyAnimator + sprites + particle systems, see TitleMenu.FixClonedGlows). The
    // animator only drives the alpha of its material colour property, so tinting the RGB of that property and of the particle
    // start colours changes the wisp's colour and leaves its pulse alone. The materials come from the game's menu, so every tint
    // is recorded and put back when the page is left or the menu closes.
    public static partial class NativeModeMenu
    {
        static readonly Color RemoveGlowColor = new Color(1f, 0.12f, 0.08f);
        static readonly Dictionary<Material, KeyValuePair<string, Color>> s_glowMaterials = new Dictionary<Material, KeyValuePair<string, Color>>();
        static readonly Dictionary<ParticleSystem, Color> s_glowParticles = new Dictionary<ParticleSystem, Color>();

        static bool GlowTintDisabled
        {
            get { return Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1" && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_REMOVE_GLOW") == "1"; }
        }

        // Called after every page change and when the menu closes.
        static void UpdateGlowTint()
        {
            try
            {
                if (s_root != null && s_page == PageServerRemove && !GlowTintDisabled) TintGlows();
                else RestoreGlows();
            }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: glow tint failed: " + e.Message); }
        }

        static void TintGlows()
        {
            foreach (CleverMenuItem item in s_items)
            {
                if (item == null) continue;
                foreach (TransparencyAnimator ta in item.GetComponentsInChildren<TransparencyAnimator>(true))
                {
                    string prop = s_taProps[Mathf.Clamp((int)ta.Mode, 0, s_taProps.Length - 1)];
                    foreach (Renderer r in ta.GetComponentsInChildren<Renderer>(true))
                    {
                        Material m = r.sharedMaterial;
                        if (m == null || !m.HasProperty(prop)) continue;
                        Color c = m.GetColor(prop);
                        if (!s_glowMaterials.ContainsKey(m)) s_glowMaterials[m] = new KeyValuePair<string, Color>(prop, c);
                        m.SetColor(prop, new Color(RemoveGlowColor.r, RemoveGlowColor.g, RemoveGlowColor.b, c.a));
                    }
                }
                foreach (ParticleSystem ps in item.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (!s_glowParticles.ContainsKey(ps)) s_glowParticles[ps] = ps.startColor;
                    ps.startColor = new Color(RemoveGlowColor.r, RemoveGlowColor.g, RemoveGlowColor.b, ps.startColor.a);
                }
            }
        }

        static void RestoreGlows()
        {
            if (s_glowMaterials.Count == 0 && s_glowParticles.Count == 0) return;
            foreach (KeyValuePair<Material, KeyValuePair<string, Color>> pair in s_glowMaterials)
            {
                if (pair.Key == null) continue;
                Color now = pair.Key.GetColor(pair.Value.Key);
                pair.Key.SetColor(pair.Value.Key, new Color(pair.Value.Value.r, pair.Value.Value.g, pair.Value.Value.b, now.a));
            }
            foreach (KeyValuePair<ParticleSystem, Color> pair in s_glowParticles)
            {
                if (pair.Key == null) continue;
                Color now = pair.Key.startColor;
                pair.Key.startColor = new Color(pair.Value.r, pair.Value.g, pair.Value.b, now.a);
            }
            s_glowMaterials.Clear();
            s_glowParticles.Clear();
        }

        // Test oracle: the colour (RGB of the animator's property) of the first glow renderer of the first item.
        internal static bool GlowsAreRed()
        {
            if (s_items.Count == 0 || s_items[0] == null) return false;
            foreach (TransparencyAnimator ta in s_items[0].GetComponentsInChildren<TransparencyAnimator>(true))
            {
                string prop = s_taProps[Mathf.Clamp((int)ta.Mode, 0, s_taProps.Length - 1)];
                foreach (Renderer r in ta.GetComponentsInChildren<Renderer>(true))
                {
                    Material m = r.sharedMaterial;
                    if (m == null || !m.HasProperty(prop)) continue;
                    Color c = m.GetColor(prop);
                    return c.r > 0.8f && c.g < 0.3f && c.b < 0.3f;
                }
            }
            return false;
        }
    }
}
