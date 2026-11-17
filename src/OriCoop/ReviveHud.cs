using Game;
using UnityEngine;

namespace OriCoop
{
    // Skull marker, revive progress ring and "hold to revive" hint for a downed Ori. Drawn like P2's health pips:
    // by the game's HUD camera, visible and faded exactly when the game's own HUD is.
    public static class ReviveHud
    {
        class Label
        {
            public GameObject Go; public TextMesh Text;
            public Label(string name, int size, Color color)
            {
                Go = new GameObject(name);
                Object.DontDestroyOnLoad(Go);
                Text = Go.AddComponent<TextMesh>();
                Font font = null;
                try { font = Font.CreateDynamicFontFromOSFont(new string[] { "Segoe UI Symbol", "Microsoft YaHei", "Arial" }, size); } catch (System.Exception) { }
                if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                Text.font = font; Text.fontSize = size; Text.anchor = TextAnchor.MiddleCenter; Text.alignment = TextAlignment.Center;
                Text.fontStyle = FontStyle.Bold; Text.color = color;
                MeshRenderer r = Go.GetComponent<MeshRenderer>();
                r.sharedMaterial = font.material; r.sharedMaterial.renderQueue = 3000;
                Go.layer = P2Hud.HudLayer();
                Go.SetActive(false);
            }
            // Place at a world point (as seen by the gameplay camera), `screenFraction` of the screen height tall.
            public bool Place(Vector3 world, float screenFraction, float alpha)
            {
                Camera game = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null, gui = P2Hud.GuiCamera();
                if (game == null || gui == null || alpha <= 0.01f) { Go.SetActive(false); return false; }
                Vector3 vp = game.WorldToViewportPoint(world);
                if (vp.z < 0f || vp.x < -0.1f || vp.x > 1.1f || vp.y < -0.1f || vp.y > 1.1f) { Go.SetActive(false); return false; }
                Go.SetActive(true);
                Go.transform.position = gui.ViewportToWorldPoint(new Vector3(vp.x, vp.y, 8f));
                Go.transform.rotation = gui.transform.rotation;
                float worldH = gui.orthographic ? gui.orthographicSize * 2f * screenFraction : 0.3f;
                Text.characterSize = worldH / (Text.fontSize * 0.12f);
                Color c = Text.color; c.a = alpha; Text.color = c;
                return true;
            }
            public void Hide() { if (Go != null && Go.activeSelf) Go.SetActive(false); }
            public void Destroy() { if (Go != null) Object.Destroy(Go); Go = null; }
        }

        static readonly Label[] s_skull = new Label[Players.MaxSeats + 1], s_percent = new Label[Players.MaxSeats + 1], s_hint = new Label[Players.MaxSeats + 1];
        static readonly DeadMarker.Icon[] s_icon = new DeadMarker.Icon[Players.MaxSeats + 1];
        static readonly GlowRing[] s_ring = new GlowRing[Players.MaxSeats + 1];
        static readonly GameText[] s_gameHint = new GameText[Players.MaxSeats + 1], s_gameTag = new GameText[Players.MaxSeats + 1];
        public static string LastPercentText = "", LastHintText = "";
        // TODO(4p): phase 1 removes slot-based query; existing callers use local/puppet slots.
        public static bool IconShown(int player) { int seat = Roles.OfSlot(player); return s_icon[seat] != null && s_icon[seat].Go != null && s_icon[seat].Go.activeSelf; }
        // TODO(4p): phase 1 removes slot-based query; existing callers use local/puppet slots.
        public static bool SkullShown(int player) { int seat = Roles.OfSlot(player); return IconShown(player) || (s_skull[seat] != null && s_skull[seat].Go != null && s_skull[seat].Go.activeSelf); }

        public static void Update()
        {
            for (int seat = 1; seat <= Players.MaxSeats; seat++) if (Revive.SeinAt(seat) == null) Hide(seat);
            foreach (int seat in Revive.PresentSeats) UpdateFor(seat);
        }

        static void UpdateFor(int player)
        {
            Revive.Downed d = Revive.DownedAtSeat(player);
            if (d == null) { Hide(player); return; }
            if (s_skull[player] == null)
            {
                s_skull[player] = new Label("OriCoop_Skull_P" + player, 72, new Color(0.95f, 0.95f, 1f));
                s_percent[player] = new Label("OriCoop_RevivePct_P" + player, 56, new Color(0.75f, 0.95f, 1f));
                s_hint[player] = new Label("OriCoop_ReviveHint_P" + player, 40, new Color(1f, 0.85f, 0.4f));
            }
            float a = P2Hud.GameHudOpacity();
            if (DeadMarker.Available)
            {
                // The map's Ori head with X eyes, and its role ("P1"/"P2", see Roles) under it.
                if (s_icon[player] == null) s_icon[player] = new DeadMarker.Icon("OriCoop_DeadIcon_P" + player);
                s_icon[player].Place(d.Marker + new Vector3(0f, 0.9f, 0f), 0.085f, a);
                if (s_gameTag[player] == null) s_gameTag[player] = new GameText("OriCoop_ReviveTag_P" + player) { NoBacking = true };
                if (s_gameTag[player].Ok)
                {
                    s_gameTag[player].SetText(Roles.Tag(player));
                    s_gameTag[player].Place(d.Marker + new Vector3(0f, -0.15f, 0f), 0.024f, 0.2f, a);
                    s_skull[player].Hide();
                }
                else
                {
                    s_skull[player].Text.text = Roles.Tag(player);
                    s_skull[player].Place(d.Marker + new Vector3(0f, -0.15f, 0f), 0.022f, a * 0.8f);
                }
            }
            else
            {
                if (s_icon[player] != null) s_icon[player].Hide();
                if (s_gameTag[player] != null) s_gameTag[player].Hide();
                s_skull[player].Text.text = "☠ " + Roles.Tag(player);
                s_skull[player].Place(d.Marker + new Vector3(0f, 0.6f, 0f), 0.05f, a);
            }

            // Progress: a glowing ring that fills around the Ori head, like the game's own charge rings. The plain
            // percentage is only the fallback when the ring can't be drawn.
            // The head picture sits in the lower part of its quad, so the ring is centred below the quad's centre.
            Vector3 head = d.Marker + new Vector3(0f, 0.4f, 0f);
            if (s_ring[player] == null) s_ring[player] = new GlowRing("OriCoop_ReviveRing_P" + player);
            LastPercentText = d.Progress > 0f ? Mathf.FloorToInt(d.Progress * 100f) + "%" : "";
            if (s_ring[player].Ok && DeadMarker.Available)
            {
                s_ring[player].Place(head, 0.12f, d.Progress, a);
                s_percent[player].Hide();
            }
            else if (d.Progress > 0f)
            {
                s_percent[player].Text.text = LastPercentText;
                s_percent[player].Place(d.Marker + new Vector3(0f, 1.6f, 0f), 0.04f, a);
            }
            else s_percent[player].Hide();

            SeinCharacter saver = Revive.Rescuer(d, false);
            if (Revive.MultiRevive && P2Context.P1 != null && !Revive.IsDowned(P2Context.P1)
                && Revive.SaverNear(P2Context.P1, d)) saver = P2Context.P1;
            // Online, the partner's puppet is the saver on our own skull: the hint is for the player at the other PC.
            bool remoteSaver = NetPlay.Enabled && Players.OfSein(saver) != null;
            if (saver != null && !remoteSaver && Revive.SaverNear(saver, d) && !d.Holding)
            {
                // The game's own hint box: its font, and the real button icon. [SpiritFlame] follows the game's
                // current control scheme, which is P1's; P2 always plays on a pad, so P2 gets "X" unless the game is
                // showing pad icons anyway.
                bool p1Saver = saver == P2Context.P1;
                bool padIcons = GameSettings.Instance != null && GameSettings.Instance.CurrentControlScheme == ControlScheme.Controller;
                string button = p1Saver || padIcons ? "[SpiritFlame]" : "X";
                string text = d.NeedRelease ? L10n.F("revive.again", button)
                    : L10n.F("revive.hold", button);
                if (s_gameHint[player] == null) s_gameHint[player] = new GameText("OriCoop_ReviveHint_P" + player);
                if (s_gameHint[player].Ok)
                {
                    s_gameHint[player].SetText(text);
                    s_gameHint[player].Place(d.Marker + new Vector3(0f, -0.9f, 0f), 0.032f, 0.4f, a);
                    s_hint[player].Hide();
                    LastHintText = text;
                }
                else
                {
                    bool keyboardSaver = p1Saver && KeyboardOnlyP1.P1Pad < 0;
                    string fallbackButton = keyboardSaver ? L10n.T("revive.mouse") : "X";
                    LastHintText = d.NeedRelease ? L10n.F("revive.again", fallbackButton)
                        : L10n.F("revive.hold", fallbackButton);
                    s_hint[player].Text.text = LastHintText;
                    s_hint[player].Place(d.Marker + new Vector3(0f, -0.8f, 0f), 0.03f, a);
                }
            }
            else { s_hint[player].Hide(); if (s_gameHint[player] != null) s_gameHint[player].Hide(); }
        }

        static void Hide(int player)
        {
            if (s_skull[player] != null) { s_skull[player].Hide(); s_percent[player].Hide(); s_hint[player].Hide(); }
            if (s_icon[player] != null) s_icon[player].Hide();
            if (s_ring[player] != null) s_ring[player].Hide();
            if (s_gameHint[player] != null) s_gameHint[player].Hide();
            if (s_gameTag[player] != null) s_gameTag[player].Hide();
        }

        public static void DestroyAll()
        {
            for (int p = 1; p <= Players.MaxSeats; p++)
            {
                if (s_skull[p] != null) { s_skull[p].Destroy(); s_percent[p].Destroy(); s_hint[p].Destroy(); }
                s_skull[p] = s_percent[p] = s_hint[p] = null;
                if (s_icon[p] != null) s_icon[p].Destroy();
                s_icon[p] = null;
                if (s_ring[p] != null) s_ring[p].Destroy();
                s_ring[p] = null;
                if (s_gameHint[p] != null) s_gameHint[p].Destroy();
                s_gameHint[p] = null;
                if (s_gameTag[p] != null) s_gameTag[p].Destroy();
                s_gameTag[p] = null;
            }
        }
    }
}
