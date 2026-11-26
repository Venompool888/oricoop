using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    public static partial class CoopMenu
    {
        static int s_nameSeat;
        static InputDevice s_nameInput;
        static string s_nameDraft = "";
        static bool s_focusName;
        static GUIStyle s_nameField;
        internal static bool EditingName { get { return s_nameSeat != 0; } }

        static void BeginNameEdit(int seat)
        {
            if (!IsOpen || Page != PageSettings || !Players.IsLocal(seat)) return;
            s_nameSeat = seat;
            s_nameInput = s_seats.InputOf(seat);
            s_nameDraft = TeammateIdentity.GetLocalName(seat);
            s_focusName = true;
            ImeGuard.NameEditing(true);
        }

        static bool SameNameInput()
        {
            if (!Players.IsLocal(s_nameSeat)) return false;
            InputDevice input = s_seats.InputOf(s_nameSeat);
            return input.Kind == s_nameInput.Kind && input.Pad == s_nameInput.Pad;
        }

        static void EndNameEdit(bool save)
        {
            if (!EditingName) return;
            if (save && SameNameInput()) TeammateIdentity.SetLocalName(s_nameSeat, s_nameDraft);
            s_nameSeat = 0;
            s_nameDraft = "";
            ImeGuard.NameEditing(false);
            s_ignoreNavUntil = Time.unscaledTime + 0.2f;
        }

        static void TickNameEdit()
        {
            if (!SameNameInput() || AnyPadPressed(B)) { EndNameEdit(false); return; }
            if (Input.GetKeyDown(KeyCode.F8)) { Close(); return; }
            if (AnyPadPressed(A)) EndNameEdit(true);
        }

        static void DrawNameEdit()
        {
            float w = Mathf.Min(Screen.width - 32f, 580f), inner = w - 32f;
            float row = s_text.fontSize * 2f;
            string hint = L10n.T("coop.name_visibility");
            float h = 44f + row * 3 + LineH(s_small, hint, inner);
            Rect r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            NativeGui.Panel("coop.name.background", r, new Color(0.05f, 0.07f, 0.12f, 0.94f));
            GUILayout.BeginArea(new Rect(r.x + 16f, r.y + 12f, inner, h - 24f));
            NativeGui.LayoutLabel("coop.name.title", L10n.F("coop.player_name", Roles.Tag(s_nameSeat)), s_title);
            NativeGui.LayoutLabel("coop.name.hint", hint, s_small);
            Event e = Event.current;
            bool save = false, cancel = false;
            if (e.type == EventType.KeyDown && string.IsNullOrEmpty(Input.compositionString))
            {
                save = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
                cancel = e.keyCode == KeyCode.Escape;
                if (save || cancel) e.Use();
            }
            GUI.SetNextControlName("OriCoopPlayerName");
            if (s_nameField == null || s_nameField.fontSize != s_text.fontSize)
                s_nameField = new GUIStyle(GUI.skin.textField) { font = s_text.font, fontSize = s_text.fontSize, richText = false };
            s_nameDraft = GUILayout.TextField(s_nameDraft, 64, s_nameField, GUILayout.Height(row));
            if (s_focusName && e.type == EventType.Repaint)
            { GUI.FocusControl("OriCoopPlayerName"); s_focusName = false; }
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            save |= RowButton("coop.name.save", L10n.T("coop.name_save"), false, row);
            cancel |= RowButton("coop.name.cancel", L10n.T("coop.name_cancel"), false, row);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            if (cancel || save) EndNameEdit(save && !cancel);
        }
    }
}
