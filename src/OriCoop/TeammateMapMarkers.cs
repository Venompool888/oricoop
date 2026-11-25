using System.Collections.Generic;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Each map owns up to one marker per teammate. Its fade group supplies the same layer,
    // camera and lifetime as the game's own marker, including the transition between maps.
    internal sealed class TeammateMapMarkers : MonoBehaviour
    {
        readonly TextMesh[] m_labels = new TextMesh[Players.MaxSeats + 1];
        readonly List<Material> m_materials = new List<Material>();
        static readonly bool s_freeze = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_MAP_PROJECTION") == "1";
        static readonly string[] s_fontNames = { "Arial", "Segoe UI Symbol", "Microsoft YaHei" };

        [HarmonyPatch(typeof(AreaMapUI), "FixedUpdate")]
        static class AreaMapUpdate
        {
            static void Postfix(AreaMapUI __instance)
            {
                if (GameMapUI.Instance != null && GameMapUI.Instance.IsVisible && __instance.PlayerPositionMarker != null)
                    Refresh(__instance.FadeOutGroup, __instance.PlayerPositionMarker.transform, __instance, null);
            }
        }

        [HarmonyPatch(typeof(WorldMapUI), "FixedUpdate")]
        static class WorldMapUpdate
        {
            static void Postfix(WorldMapUI __instance)
            {
                if (GameMapUI.Instance != null && GameMapUI.Instance.IsVisible && __instance.PlayerMarker != null && AreaMapUI.Instance != null)
                    Refresh(__instance.FadeOutGroup, __instance.PlayerMarker, AreaMapUI.Instance, __instance);
            }
        }

        static void Refresh(Transform group, Transform template, AreaMapUI area, WorldMapUI world)
        {
            TeammateMapMarkers view = group.GetComponent<TeammateMapMarkers>();
            if (view == null)
            {
                if (Players.Remotes.Count == 0 && !NetPlay.Connected) return;
                view = group.gameObject.AddComponent<TeammateMapMarkers>();
            }
            for (int seat = 1; seat <= Players.MaxSeats; seat++)
            {
                Vector3 position;
                TextMesh label = view.m_labels[seat];
                if (seat == Players.LocalSeat || !Position(seat, out position) || !Revealed(position, area, world != null))
                {
                    if (label != null && label.transform.parent.gameObject.activeSelf) label.transform.parent.gameObject.SetActive(false);
                    continue;
                }
                bool wasVisible = label != null && label.transform.parent.gameObject.activeSelf;
                if (label == null)
                {
                    float opacity = world == null ? area.FadeOutAnimator.FinalOpacity : world.CrossFade.FinalOpacity;
                    if (opacity < 0.02f) continue;
                    label = view.m_labels[seat] = view.Create(template, seat, opacity);
                }
                Transform marker = label.transform.parent;
                if (!wasVisible) marker.gameObject.SetActive(true);
                if (!s_freeze || !wasVisible)
                {
                    if (world != null) marker.position = world.WorldToUIPosition(position);
                    else marker.localPosition = area.Navigation.WorldToMapPosition(position + area.PlayerPositionOffset + Vector3.up);
                }
                string text = TeammateIdentity.Label(seat);
                if (label.text != text) label.text = text;
            }
        }

        static bool Position(int seat, out Vector3 position)
        {
            foreach (Puppet puppet in Players.Remotes)
                if (puppet.Seat == seat && puppet.IsLocal && puppet.Sein != null && puppet.Sein.Active)
                {
                    position = puppet.Sein.Position;
                    return true;
                }
            position = Vector3.zero;
            return NetPlay.Connected && NetPlay.RemoteInSession(seat) && !NetPlay.RemoteLoading(seat) && NetPlay.RemotePos(seat, out position);
        }

        static bool Revealed(Vector3 position, AreaMapUI area, bool world)
        {
            if (area.DebugNavigation.UndiscoveredMapVisible) return true;
            RuntimeGameWorldArea region = WorldMapLogic.Instance == null ? null : WorldMapLogic.Instance.FindAreaFromPosition(position);
            if (region == null) return false;
            if (world) return region.AreaDiscovered;
            return (!region.Area.VisitableCondition || region.Area.VisitableCondition.Validate(null)) && !region.IsHidden(position);
        }

        TextMesh Create(Transform template, int seat, float opacity)
        {
            Transform marker = Object.Instantiate(template);
            marker.name = "Co-op map P" + seat;
            marker.SetParent(transform, false);
            marker.localRotation = template.localRotation;
            marker.localScale = template.localScale;
            Color color = Roles.ColorOf(seat);
            int sortingOrder = 0;
            foreach (Renderer renderer in marker.GetComponentsInChildren<Renderer>(true))
            {
                sortingOrder = Mathf.Max(sortingOrder, renderer.sortingOrder);
                foreach (Material material in renderer.materials)
                {
                    if (material == null) continue;
                    m_materials.Add(material);
                    if (!material.HasProperty("_Color")) continue;
                    Color tint = color;
                    // The live template can be halfway through a crossfade when we copy it.
                    tint.a = Mathf.Clamp01(material.color.a / opacity);
                    material.color = tint;
                }
            }

            GameObject caption = new GameObject("Seat");
            caption.layer = template.gameObject.layer;
            caption.transform.SetParent(marker, false);
            TextMesh label = caption.AddComponent<TextMesh>();
            Font font = Ui.OsFonts.Shared(s_fontNames, 48) ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.font = font;
            label.fontSize = 48;
            label.richText = false;
            label.anchor = TextAnchor.MiddleLeft;
            label.color = color;
            // Both map roots use the GUI camera, but their native marker prefabs have different scales.
            float height = UI.Cameras.System.GUICamera.Camera.orthographicSize * 0.044f;
            label.characterSize = height / (label.fontSize * 0.12f * Mathf.Max(0.001f, Mathf.Abs(marker.lossyScale.y)));
            caption.transform.localPosition = Vector3.right * (height * 0.9f / Mathf.Max(0.001f, Mathf.Abs(marker.lossyScale.x)));
            MeshRenderer textRenderer = caption.GetComponent<MeshRenderer>();
            Material captionMaterial = new Material(font.material);
            textRenderer.sharedMaterial = captionMaterial;
            textRenderer.sortingOrder = sortingOrder + 1;
            m_materials.Add(captionMaterial);
            TransparencyAnimator.Register(marker);
            return label;
        }

        void OnDisable()
        {
            foreach (TextMesh label in m_labels)
                if (label != null) label.transform.parent.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            foreach (Material material in m_materials) if (material != null) Object.Destroy(material);
        }
    }
}
