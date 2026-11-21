using System.IO;
using HarmonyLib;
using UnityEngine;

namespace OriCoop.Saves
{
    static class CoopSaveCopyMenu
    {
        static bool s_open;
        static int[] s_slots;
        static string[] s_summaries;
        static int s_focus;
        static float s_unlockAt;
        static readonly Color TextColor = new Color(0.87f, 0.94f, 0.96f);

        static bool Visible
        {
            get
            {
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                return CoopSaveFolder.Active && NativeModeMenu.TitleChoice == NativeModeMenu.TitleOnline
                    && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.SaveSlots
                    && ui != null && ui.IsVisible && ui.Active && !ui.IsSuspended
                    && !ui.SelectingDifficulty && !ui.PromptIsOpen && !ui.IsCopying && !NativeModeMenu.IsOpen;
            }
        }

        static float Scale { get { return Mathf.Clamp(Screen.height / 1080f, 0.45f, 2f); } }
        static Rect ButtonRect
        {
            get
            {
                float s = Scale, w = Mathf.Min(940f * s, Screen.width - 40f * s);
                return new Rect(Screen.width - w - 20f * s, 24f * s, w, 72f * s);
            }
        }

        static void Toast(string key, params object[] args)
        {
            NetHud.Toast(L10n.F(key, args));
        }

        static void Open()
        {
            if (CoopSaveFolder.FirstEmptyCoopSlot() < 0) { Toast("saves.copy_full"); return; }
            s_slots = CoopSaveFolder.SinglePlayerSlotsWithSaves();
            if (s_slots.Length == 0) { Toast("saves.copy_empty"); return; }
            s_summaries = new string[s_slots.Length];
            for (int i = 0; i < s_slots.Length; i++)
                s_summaries[i] = (s_slots[i] + 1) + ": " + CoopSaves.Summary(File.ReadAllBytes(CoopSaveFolder.SinglePlayerPath(s_slots[i])));
            s_focus = 0;
            s_open = true;
            s_unlockAt = Time.realtimeSinceStartup + 0.2f;
        }

        static void Close()
        {
            s_open = false;
            s_unlockAt = Time.realtimeSinceStartup + 0.2f;
        }

        static void Copy(int index)
        {
            int target = CoopSaveFolder.FirstEmptyCoopSlot();
            if (target < 0) { Toast("saves.copy_full"); Close(); return; }
            bool copied = CoopSaveFolder.CopyFromSinglePlayer(s_slots[index], target);
            Close();
            if (copied)
            {
                SaveSlotsUI.Instance.SetCurrentItemAndScroll(target);
                Toast("saves.copy_done", target + 1);
            }
            else Toast("saves.copy_failed");
        }

        [HarmonyPatch(typeof(SaveSlotsUI), "FixedUpdate")]
        static class SlotInputPatch
        {
            static bool Prefix()
            {
                if (!Visible) { if (s_open) Close(); return true; }
                if (!GameController.IsFocused) return true;
                if (Time.realtimeSinceStartup < s_unlockAt) return false;
                if (!s_open)
                {
                    if (Core.Input.RightShoulder.OnPressedNotUsed)
                    {
                        Core.Input.RightShoulder.Used = true;
                        Open();
                        return false;
                    }
                    Vector3 mouse = UnityEngine.Input.mousePosition;
                    if (ButtonRect.Contains(new Vector2(mouse.x, Screen.height - mouse.y)) && Core.Input.LeftClick.OnPressed)
                        return false;
                    return true;
                }
                if (Core.Input.Cancel.OnPressedNotUsed)
                {
                    Core.Input.Cancel.Used = true;
                    Close();
                    return false;
                }
                if (Core.Input.Up.OnPressedNotUsed)
                {
                    Core.Input.Up.Used = true;
                    s_focus = (s_focus + s_slots.Length - 1) % s_slots.Length;
                }
                if (Core.Input.Down.OnPressedNotUsed)
                {
                    Core.Input.Down.Used = true;
                    s_focus = (s_focus + 1) % s_slots.Length;
                }
                if (Core.Input.ActionButtonA.OnPressedNotUsed)
                {
                    Core.Input.ActionButtonA.Used = true;
                    Copy(s_focus);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(SaveSlotUI), "FixedUpdate")]
        static class BackupInputPatch
        {
            static bool Prefix() { return !Visible || (!s_open && Time.realtimeSinceStartup >= s_unlockAt); }
        }

        [HarmonyPatch(typeof(NetHud), "Draw")]
        static class DrawPatch
        {
            static void Postfix() { Draw(); }
        }

        static void Draw()
        {
            if (!Visible) { if (s_open) Close(); return; }
            Event e = Event.current;
            if (!s_open && GameController.IsFocused && e.type == EventType.KeyDown && e.keyCode == KeyCode.F6)
            {
                Open();
                e.Use();
            }
            NativeGui.BeginPage("saves.copy");
            try
            {
                float s = Scale;
                if (!s_open)
                {
                    Rect r = ButtonRect;
                    if (NativeGui.Button("saves.copy.open", r, L10n.T("saves.copy_single"), 26f * s, false, TextColor)) Open();
                    NativeGui.Label("saves.copy.binding", new Rect(r.x, r.yMax, r.width, 28f * s), L10n.T("saves.copy_binding"), 20f * s, TextAnchor.MiddleRight, TextColor);
                    return;
                }
                NativeGui.Panel("saves.copy.scrim", new Rect(0, 0, Screen.width, Screen.height), new Color(0.008f, 0.031f, 0.055f, 0.88f));
                float w = Mathf.Min(1100f * s, Screen.width * 0.92f), h = (s_slots.Length * 54f + 160f) * s;
                float x = (Screen.width - w) * 0.5f, y = (Screen.height - h) * 0.5f;
                NativeGui.Label("saves.copy.title", new Rect(x, y, w, 52f * s), L10n.T("saves.copy_pick"), 34f * s, TextAnchor.MiddleCenter, TextColor);
                y += 64f * s;
                for (int i = 0; i < s_slots.Length; i++)
                {
                    Rect row = new Rect(x, y, w, 50f * s);
                    if (e.type == EventType.MouseMove && row.Contains(e.mousePosition)) s_focus = i;
                    if (NativeGui.Button("saves.copy.slot." + i, row, s_summaries[i], 26f * s, s_focus == i, TextColor)) { Copy(i); return; }
                    y += 54f * s;
                }
                if (NativeGui.Button("saves.copy.back", new Rect(x, y, w, 42f * s), L10n.T("common.back"), 24f * s, false, TextColor)) Close();
                NativeGui.Label("saves.copy.prompts", new Rect(x, y + 46f * s, w, 32f * s), L10n.T("saves.prompts"), 20f * s, TextAnchor.MiddleCenter, TextColor);
            }
            finally { NativeGui.EndPage(); }
        }
    }
}
