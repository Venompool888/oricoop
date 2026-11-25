using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using OriCoop.Saves;
using UnityEngine;

namespace OriCoop
{
    // Online downed / revive phase "R" for run-nettest.ps1 (called from NetTestHarness after E3). Both PCs run it;
    // each checks what its own game must show. Own numbered barriers on message 65.
    //   R1 guest dies -> guest downed (no reload); host sees the guest's skull.
    //   R2 host holds Spirit Flame at the skull -> host's health drains, guest sees the progress; at 100% the guest is
    //      back with the health the host gave.
    //   R3 host dies (downed), then guest dies -> host decides "both down" -> both games reload a checkpoint.
    public static class NetReviveTest
    {
        const byte MsgBarrier = 65;
        // T-043: barriers also carry the partner's room-save counter and checkpoint fingerprint (step "R save").
        sealed class SaveReport
        {
            public int Adoptions; public string Fingerprint;
            public bool HasPosition; public Vector3 Position;
        }
        static readonly Dictionary<int, SaveReport> s_partnerSteps = new Dictionary<int, SaveReport>();
        static void OnBarrier(BinaryReader r)
        {
            int step = r.ReadInt32();
            SaveReport report = new SaveReport { Adoptions = r.ReadInt32(), Fingerprint = r.ReadString(), HasPosition = r.ReadBoolean() };
            if (report.HasPosition) report.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s_partnerSteps[step] = report;
        }

        static SeinCharacter Own { get { return P2Context.P1; } }
        static SeinCharacter Puppet { get { return P2Context.P2; } }
        static string F(float f) { return f.ToString("F2"); }

        internal static bool NativeLinkAt(SeinCharacter s, Vector3 at)
        {
            return s != null && s.SoulFlame != null && s.SoulFlame.SoulFlameExists
                && Vector3.Distance(s.SoulFlame.SoulFlamePosition, at) < 0.1f;
        }
        static readonly FieldInfo s_markerObject = typeof(SeinSoulFlame).GetField("m_checkpointMarkerGameObject", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool OnlyNativeFlame(SeinCharacter own, out string state)
        {
            SeinSoulFlame flame = own != null ? own.SoulFlame : null;
            GameObject marker = flame != null ? (GameObject)s_markerObject.GetValue(flame) : null;
            SoulFlame[] active = UnityEngine.Object.FindObjectsOfType<SoulFlame>();
            int puppetFlames = 0;
            foreach (Puppet p in Players.Remotes)
                if (p.Sein != null && p.Sein.SoulFlame != null && p.Sein.SoulFlame.SoulFlameExists) puppetFlames++;
            bool primaryIsSoleFlame = marker != null && active.Length == 1 && active[0].gameObject == marker;
            state = "active=" + active.Length + ", primary=" + (marker == null ? "none" : marker.transform.position.ToString("F3"))
                + ", primary is sole flame=" + primaryIsSoleFlame + ", puppet flames=" + puppetFlames;
            return primaryIsSoleFlame && puppetFlames == 0;
        }

        internal static IEnumerator StandAtNativeLink(SeinCharacter s, Vector3 at)
        {
            Plugin.Teleport(s, at);
            yield return Wait(Time.fixedDeltaTime);
            TestClock.Window landing = new TestClock.Window(1f);
            while (!s.IsOnGround && landing.Open) yield return null;
            yield return Wait(Time.fixedDeltaTime);
        }

        static IEnumerator SoulLink(TestHarness h, bool host)
        {
            if (Own == null || Puppet == null) { h.Check(false, "R4 needs both Oris"); yield break; }
            Vector3 first = Vector3.zero;
            for (int round = 0; round < 2; round++)
            {
                bool cast = host == (round == 0);
                int step = 110 + 3 * round, creator = round + 1;
                if (cast && round == 1) { Plugin.Teleport(Own, first + Vector3.right * 3f); yield return Wait(0.25f); }
                int notices = PartnerFx.SoulLinksReceived;
                yield return Barrier(h, step);
                SaveReport spot = new SaveReport();
                if (cast)
                {
                    Own.Energy.RestoreAllEnergy();
                    int saves = Revive.DeathSaves; float hp = Own.Mortality.Health.Amount;
                    spot.HasPosition = true; spot.Position = Own.Position;
                    CastSoulLink(Own);
                    h.Check(Revive.DeathSaves == saves && Mathf.Abs(Own.Mortality.Health.Amount - hp) < 0.01f,
                        "R4 P" + creator + ": a soul link costs no health when nobody is down");
                }
                yield return Barrier(h, step + 1, "", spot);
                if (!cast) s_partnerSteps.TryGetValue(step + 1, out spot);
                string label = "R4 P" + creator + " cast: ";
                h.Check(spot != null && spot.HasPosition, label + "cast position reported");
                if (spot != null && spot.HasPosition)
                {
                    Vector3 at = spot.Position;
                    if (round == 0) first = at;
                    else h.Check(Vector3.Distance(at, first) > 1f, label + "the new link is away from the old one");
                    float end = Time.realtimeSinceStartup + 6f;
                    while ((Revive.RoomFlamePending || !NativeLinkAt(Own, at) || Revive.LinkSeat != creator) && Time.realtimeSinceStartup < end)
                        yield return null;
                    yield return Wait(Time.fixedDeltaTime);
                    string flameState;
                    bool onlyOne = OnlyNativeFlame(Own, out flameState);
                    h.Check(NativeLinkAt(Own, at), label + "this PC's native link is at the caster's exact spot (cast="
                        + at.ToString("F3") + ", " + flameState + ")");
                    h.Check(Revive.HaveLink && Revive.LinkSeat == creator && Vector3.Distance(Revive.LinkPosition, at) < 0.1f,
                        label + "this PC's respawn point is the same spot (seat=" + Revive.LinkSeat + ", point=" + Revive.LinkPosition.ToString("F3") + ")");
                    h.Check(onlyOne, label + "only one flame exists, none on the puppet (" + flameState + ")");
                    if (!cast) h.Check(PartnerFx.SoulLinksReceived == notices + 1, label + "the cast notice reached the other PC");
                    Vector3 back = Own.Position;
                    int level = Own.Level.Current;
                    try
                    {
                        yield return StandAtNativeLink(Own, at);
                        Own.Level.Current = Mathf.Max(level, 1);
                        SeinSoulFlame flame = Own.SoulFlame;
                        bool inside = flame.InsideCheckpointMarker;
                        h.Check(inside, label + "this player is in range of the native flame (Ori="
                            + Own.Position.ToString("F3") + ", cast=" + at.ToString("F3") + ")");
                        h.Check(inside && flame.AllowedToAccessSkillTree && !Own.Controller.IsBashing && !UI.Fader.IsFadingInOrStay(),
                            label + "a tap can open the native skill tree (inside=" + inside + ", safety=" + flame.IsSafeToCastSoulFlame + ")");
                    }
                    finally { Own.Level.Current = level; Plugin.Teleport(Own, back); }
                }
                yield return Barrier(h, step + 2);
            }
        }

        internal static void CastSoulLink(SeinCharacter s)
        {
            typeof(SeinSoulFlame).GetMethod("CastSoulFlame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(s.SoulFlame, null);
        }

        static float Parse(string text) { float v; return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : -1f; }
        static string Send(float v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        // Hold Y (test hook) until this PC's downed Ori is back, or `seconds` (game) pass.
        static IEnumerator HoldSelfUntilUp(float seconds)
        {
            Revive.TestHoldSelf = true;
            TestClock.Window hold = new TestClock.Window(seconds);
            while (Revive.DownedOf(1) != null && hold.Open) yield return null;
            Revive.TestHoldSelf = false;
        }

        // T-041 (#15) rules 2 and 3 on T-043 room saves.
        //   R5  the guest is down; the host makes a soul link (death save: half its health). The room's latest link is
        //       the host's; the guest holds Y: still down after 1 s, back after 2 s, alone at the link with the host's
        //       health after saving. Nobody's world reloads.
        //   R5b the host is down; the guest makes a soul link (its own death save), offered to the room (SaveOffer).
        //       The host adopts it, so the latest link is the guest's; the host opens the pause menu (it stays open, T-248),
        //       closes it, holds Y and comes back there.
        //   R5c the host goes down again: the latest link is older than this down, so holding Y gives full health.
        static IEnumerator DeathRules(TestHarness h, bool host)
        {
            Own.Mortality.Health.RestoreAllHealth();
            Own.Energy.Gain(Own.Energy.Max);
            yield return Wait(1.2f);
            yield return Barrier(h, 13);
            int restores0 = TestHarness.RestoreCheckpointCalls;
            int links0 = Revive.LinksNoted, self0 = Revive.SelfRevives, saves0 = Revive.DeathSaves, sent0 = Revive.RoomLinksSent;
            if (!host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null, "R5 guest: own Ori downed");
            }
            yield return Barrier(h, 14);
            float hostAfter = -1f;
            if (host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end) yield return null;
                float before = Own.Mortality.Health.Amount;
                CastSoulLink(Own);
                hostAfter = Own.Mortality.Health.Amount;
                float expected = Mathf.Max(before / 2f, Mathf.Min(before, 4f));
                h.Check(Revive.DeathSaves == saves0 + 1 && Mathf.Abs(hostAfter - expected) < 0.01f,
                    "R5 host: soul link with the guest down costs half its health (" + F(before) + " -> " + F(hostAfter) + ", expected " + F(expected) + ")");
                h.Check(Revive.HaveLink && Revive.LinkSeat == 1 && Vector3.Distance(Revive.LinkPosition, Own.Position) < 1f && Revive.RoomLinksSent > sent0,
                    "R5 host: its soul link is the room's latest link and was announced (seat " + Revive.LinkSeat + ", d="
                    + F(Vector3.Distance(Revive.LinkPosition, Own.Position)) + ", sent " + sent0 + " -> " + Revive.RoomLinksSent + ")");
            }
            yield return Barrier(h, 15, host ? Send(hostAfter) : "");
            if (!host)
            {
                float creator = s_partnerSteps.ContainsKey(15) ? Parse(s_partnerSteps[15].Fingerprint) : -1f;
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.LinksNoted == links0 && Time.realtimeSinceStartup < end) yield return null;
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(Revive.LinksNoted > links0 && Revive.LinkSeat == 1 && Vector3.Distance(Revive.LinkPosition, Puppet.Position) < 1.5f,
                    "R5 guest: told that the host's soul link is the latest (seat " + Revive.LinkSeat + ", d=" + F(Vector3.Distance(Revive.LinkPosition, Puppet.Position)) + ")");
                h.Check(d != null && Mathf.Abs(d.LinkHealth - creator) < 0.01f,
                    "R5 guest: the link counts for this down with the host's health after saving (" + (d != null ? F(d.LinkHealth) : "-") + " vs " + F(creator) + ")");
                h.Check(Revive.LastSelfHint != null && Revive.LastSelfHint.Contains("<icon>"), "R5 guest: hold-Y hint uses the game's button icon (" + Revive.LastSelfHint + ")");
                // T-169 hook: while an escape suppresses the give-up, holding Y does nothing.
                int suppressed0 = Revive.GiveUpsSuppressedFrames;
                System.Func<bool> hook = Revive.GiveUpSuppressed;
                Revive.GiveUpSuppressed = delegate { return true; };
                Revive.TestHoldSelf = true;
                yield return Wait(2.5f);
                Revive.TestHoldSelf = false;
                Revive.GiveUpSuppressed = hook;
                h.Check(Revive.DownedOf(1) != null && Revive.SelfRevives == self0 && Revive.GiveUpsSuppressedFrames > suppressed0,
                    "R5 guest: holding Y 2.5 s while GiveUpSuppressed returns true does not revive (frames " + (Revive.GiveUpsSuppressedFrames - suppressed0) + ")");
                Revive.TestHoldSelf = true;
                yield return Wait(1f);
                h.Check(Revive.DownedOf(1) != null, "R5 guest: still down after holding Y for 1 s");
                yield return HoldSelfUntilUp(3f);
                h.Check(Revive.SelfRevives == self0 + 1 && Revive.DownedOf(1) == null, "R5 guest: holding Y revives at the latest soul link");
                float atLink = Vector3.Distance(Own.Position, Revive.LinkPosition);
                h.Check(atLink < 1.5f, "R5 guest: back at the soul link, not at its skull (d=" + F(atLink) + ")");
                h.Check(Mathf.Abs(Own.Mortality.Health.Amount - creator) < 0.01f, "R5 guest: health equals the host's after saving ("
                    + F(Own.Mortality.Health.Amount) + " vs " + F(creator) + ")");
            }
            yield return Barrier(h, 16);
            yield return Wait(0.5f);
            if (host)
            {
                h.Check(Revive.DownedOf(2) == null && !ReviveHud.SkullShown(2), "R5 host: the guest's skull is gone after it revived at the link");
                h.Check(Vector3.Distance(Puppet.Position, Revive.LinkPosition) < 2f, "R5 host: the guest's Ori is shown at the link (d="
                    + F(Vector3.Distance(Puppet.Position, Revive.LinkPosition)) + ")");
            }
            h.Check(TestHarness.RestoreCheckpointCalls == restores0, "R5 " + (host ? "host" : "guest") + ": no checkpoint reload on this PC");

            // R5b: the guest's soul link (through its save offer) is the room's latest link.
            Own.Mortality.Health.RestoreAllHealth();
            Own.Energy.Gain(Own.Energy.Max);
            yield return Wait(1.6f);   // the revive's invincibility runs out
            yield return Barrier(h, 17);
            links0 = Revive.LinksNoted; self0 = Revive.SelfRevives; saves0 = Revive.DeathSaves;
            int adopted0 = SaveSync.HostSavesAdopted;
            if (host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null, "R5b host: own Ori downed");
            }
            yield return Barrier(h, 18);
            float guestAfter = -1f;
            if (!host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end) yield return null;
                float before = Own.Mortality.Health.Amount;
                CastSoulLink(Own);
                guestAfter = Own.Mortality.Health.Amount;
                h.Check(Revive.DeathSaves == saves0 + 1 && guestAfter < before - 0.5f,
                    "R5b guest: soul link with the host down costs half its health (" + F(before) + " -> " + F(guestAfter) + ")");
            }
            yield return Barrier(h, 19, host ? "" : Send(guestAfter));
            if (host)
            {
                float creator = s_partnerSteps.ContainsKey(19) ? Parse(s_partnerSteps[19].Fingerprint) : -1f;
                float end = Time.realtimeSinceStartup + 8f;
                while (Revive.LinkSeat != 2 && Time.realtimeSinceStartup < end) yield return null;
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(SaveSync.HostSavesAdopted > adopted0, "R5b host: adopted the guest's save offer as the room's save");
                h.Check(Revive.LinkSeat == 2 && Vector3.Distance(Revive.LinkPosition, Puppet.Position) < 1.5f,
                    "R5b host: the guest's offered save point is the latest soul link (seat " + Revive.LinkSeat + ", d="
                    + F(Vector3.Distance(Revive.LinkPosition, Puppet.Position)) + ")");
                h.Check(d != null && Mathf.Abs(d.LinkHealth - creator) < 0.01f,
                    "R5b host: revive health is the guest's after saving (" + (d != null ? F(d.LinkHealth) : "-") + " vs " + F(creator) + ")");
                // T-248 (#336): downed, after the guest's soul link, the host can still open the pause menu (settings,
                // quit); before, every screen of a downed Ori was closed again the next frame.
                UI.Menu.ShowMenuScreen();
                yield return Wait(0.5f);
                h.Check(Revive.DownedOf(1) != null && UI.MainMenuVisible && UI.Menu.CurrentScreen == MenuScreenManager.Screens.Pause,
                    "R5b host: downed after the guest's soul link, the pause menu opens and stays open (visible="
                    + UI.MainMenuVisible + ", screen " + UI.Menu.CurrentScreen + ")");
                UI.Menu.HideMenuScreen(true);
                yield return Wait(0.3f);
                yield return HoldSelfUntilUp(4f);
                h.Check(Revive.SelfRevives == self0 + 1 && Revive.DownedOf(1) == null, "R5b host: holding Y revives at the guest's soul link");
                h.Check(Vector3.Distance(Own.Position, Revive.LinkPosition) < 1.5f && Mathf.Abs(Own.Mortality.Health.Amount - creator) < 0.01f,
                    "R5b host: back at the guest's link with its health (d=" + F(Vector3.Distance(Own.Position, Revive.LinkPosition))
                    + ", health " + F(Own.Mortality.Health.Amount) + " vs " + F(creator) + ")");
            }
            yield return Barrier(h, 20);
            yield return Wait(0.5f);
            if (!host) h.Check(Revive.DownedOf(2) == null && !ReviveHud.SkullShown(2), "R5b guest: the host's skull is gone after it revived at the link");
            h.Check(TestHarness.RestoreCheckpointCalls == restores0, "R5b " + (host ? "host" : "guest") + ": no checkpoint reload on this PC");

            // R5c: a link made before this down gives full health.
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.6f);
            yield return Barrier(h, 21);
            if (host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(d != null && d.LinkHealth == 0f, "R5c host: downed again; the latest link is older than this down");
                yield return HoldSelfUntilUp(4f);
                h.Check(Revive.DownedOf(1) == null && Mathf.Abs(Own.Mortality.Health.Amount - Own.Mortality.Health.MaxHealth) < 0.01f,
                    "R5c host: revived at the link with full health (" + F(Own.Mortality.Health.Amount) + "/" + F(Own.Mortality.Health.MaxHealth) + ")");
            }
            yield return Barrier(h, 22);
            // Later phases reload the room's save and expect full health: the host saves again with nobody down.
            Own.Mortality.Health.RestoreAllHealth();
            if (host)
            {
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();
            }
            yield return Wait(0.5f);
            yield return Barrier(h, 23);
        }

        // T-202 (#285): a downed player comes back on its own, without a teammate and without a new soul link.
        //   R6  no room save in this session yet (what a real session is like until its first save: the host loaded its
        //       game before the guest arrived) and nobody has the energy for a soul link. The room's save point is the
        //       host's latest checkpoint. The guest goes down away from it, is told to hold Y, holds: still down after
        //       1 s, back after 2 s at that checkpoint with full health; energy untouched, nobody's world reloads and
        //       the host pays nothing.
        //   R6b the same for the host's own Ori.
        static IEnumerator SelfRespawn(TestHarness h, bool host)
        {
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.6f);   // the last revive's invincibility runs out
            yield return Barrier(h, 30);
            int restores0 = TestHarness.RestoreCheckpointCalls;
            int self0 = Revive.SelfRevives, saves0 = Revive.DeathSaves, links0 = Revive.LinksNoted;
            Own.Energy.SetCurrent(0f);
            h.Check(!Own.SoulFlame.CanAffordSoulFlame, "R6 " + (host ? "host" : "guest") + ": no energy for a soul link (energy " + F(Own.Energy.Current) + ")");
            if (!host)
            {
                // The guest forgets first, so the host's word after barrier 31 is news to it.
                Revive.TestForgetRoomSave();
                Plugin.Teleport(Own, Puppet.Position + new Vector3(1.5f, 0f, 0f));
                yield return Wait(0.8f);
                h.Check(!Revive.HaveLink, "R6 guest: no room save known (as before the session's first save)");
            }
            yield return Barrier(h, 31);
            SaveReport spot = new SaveReport();
            float hostHealth0 = Own.Mortality.Health.Amount;
            if (host)
            {
                int points0 = Revive.CheckpointPoints, sent0 = Revive.RoomLinksSent;
                Revive.TestForgetRoomSave();
                GameController.Instance.CreateCheckpoint();   // a plain checkpoint: no save, no soul link
                spot.Position = Own.Position; spot.HasPosition = true;
                float end = Time.realtimeSinceStartup + 3f;
                while (!Revive.HaveLink && Time.realtimeSinceStartup < end) yield return null;
                h.Check(Revive.HaveLink && Revive.LinkFromCheckpoint && Revive.CheckpointPoints > points0
                    && Vector3.Distance(Revive.LinkPosition, spot.Position) < 0.5f && Revive.RoomLinksSent > sent0,
                    "R6 host: with no room save, its latest checkpoint is the room's save point and was announced (have "
                    + Revive.HaveLink + ", from checkpoint " + Revive.LinkFromCheckpoint + ", d=" + F(Vector3.Distance(Revive.LinkPosition, spot.Position))
                    + ", sent " + sent0 + " -> " + Revive.RoomLinksSent + ")");
            }
            yield return Barrier(h, 32, "", spot);
            if (!host)
            {
                SaveReport told;
                Vector3 at = s_partnerSteps.TryGetValue(32, out told) && told.HasPosition ? told.Position : Puppet.Position;
                float end = Time.realtimeSinceStartup + 5f;
                while (!Revive.HaveLink && Time.realtimeSinceStartup < end) yield return null;
                h.Check(Revive.HaveLink && Revive.LinksNoted > links0 && Vector3.Distance(Revive.LinkPosition, at) < 0.5f,
                    "R6 guest: told the room's save point, the host's latest checkpoint (have " + Revive.HaveLink + ", d="
                    + F(Vector3.Distance(Revive.LinkPosition, at)) + ")");
                Revive.LastSelfHint = null;
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(d != null && d.LinkHealth == 0f, "R6 guest: own Ori downed; no soul link was made after it went down");
                float skull = d != null ? Vector3.Distance(d.Marker, at) : -1f;
                h.Check(skull > 1f, "R6 guest: went down away from the save point (skull d=" + F(skull) + ")");
                string hint = L10n.T("revive.self_hold");
                h.Check(Revive.LastSelfHint != null && Revive.LastSelfHint.Contains("<icon>")
                    && Revive.LastSelfHint.Contains(hint.Substring(hint.IndexOf("{0}") + 3)),
                    "R6 guest: the hint says to hold the button to come back at the last soul link or save point (" + Revive.LastSelfHint + ")");
                h.Check(Revive.SelfHintOnScreen(Players.LocalSeat), "R6 guest: the self-revive text and its button glyph render on screen");
                // T-255: the playtest showed this hint faint grey. A native hint beside it for the screenshot comparison.
                MessageBox native = null;
                try
                {
                    TranslatedMessageProvider p = ScriptableObject.CreateInstance<TranslatedMessageProvider>();
                    p.Messages.Add(new TranslatedMessageProvider.MessageItem { English = "Native hint [Bash] [SpiritFlame]", Chinese = "" });
                    native = Game.UI.Hints.Show(p, HintLayer.Gameplay, 4f);
                }
                catch (System.Exception e) { h.Note("R6 native hint failed: " + e.Message); }
                yield return Wait(1.5f);   // past the native hint's fade-in
                float selfAlpha = Revive.SelfHintAlpha(Players.LocalSeat);
                h.Note("R6 guest brightness: self-revive text a=" + F(selfAlpha) + " [" + Revive.SelfHintBrightness(Players.LocalSeat)
                    + "]; native text a=" + F(GameText.TextAlphaOf(native)));
                h.Check(selfAlpha >= 0.98f, "R6 guest: the self-revive hint draws its text at full alpha (" + F(selfAlpha) + "; the native hint beside it for the screenshot)");
                try { Application.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(h.ResultPath)), "revive-self-guest.png")); }
                catch (System.Exception e) { h.Note("R6 screenshot failed: " + e.Message); }
                yield return null;   // the screenshot is taken at the end of this frame
                try { if (native != null && native.Visibility != null) native.Visibility.HideMessageScreenImmediately(); }
                catch (System.Exception e) { h.Note("R6 native hint hide failed: " + e.Message); }
                Revive.TestHoldSelf = true;
                yield return Wait(1f);
                h.Check(Revive.DownedOf(1) != null, "R6 guest: still down after holding Y for 1 s");
                yield return HoldSelfUntilUp(3f);
                float back = Vector3.Distance(Own.Position, at);
                h.Check(Revive.SelfRevives == self0 + 1 && Revive.DownedOf(1) == null,
                    "R6 guest: holding Y revives it on its own: no teammate, no new soul link");
                h.Check(!Revive.SelfHintOnScreen(Players.LocalSeat), "R6 guest: the self-revive hint hides after revival");
                // The teleport puts it 0.5 above the spot; the skull is about 1.5 away.
                h.Check(back < 1f, "R6 guest: back at the last save point, not at its skull (d=" + F(back) + ", skull was " + F(skull) + " away)");
                h.Check(Mathf.Abs(Own.Mortality.Health.Amount - Own.Mortality.Health.MaxHealth) < 0.01f,
                    "R6 guest: back with full health (" + F(Own.Mortality.Health.Amount) + "/" + F(Own.Mortality.Health.MaxHealth) + ")");
                h.Check(Own.Energy.Current == 0f, "R6 guest: energy untouched (" + F(Own.Energy.Current) + ")");
            }
            yield return Barrier(h, 33);
            yield return Wait(0.5f);
            if (host)
            {
                h.Check(Revive.DownedOf(2) == null && !ReviveHud.SkullShown(2), "R6 host: the guest's skull is gone after it came back on its own");
                h.Check(Vector3.Distance(Puppet.Position, Revive.LinkPosition) < 2f, "R6 host: the guest's Ori is shown at the save point (d="
                    + F(Vector3.Distance(Puppet.Position, Revive.LinkPosition)) + ")");
                h.Check(Revive.DeathSaves == saves0 && Mathf.Abs(Own.Mortality.Health.Amount - hostHealth0) < 0.01f,
                    "R6 host: the guest's comeback cost the host nothing (health " + F(hostHealth0) + " -> " + F(Own.Mortality.Health.Amount) + ")");
            }
            h.Check(TestHarness.RestoreCheckpointCalls == restores0, "R6 " + (host ? "host" : "guest") + ": no checkpoint reload on this PC");

            // R6b: the host's own Ori, same rule.
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.6f);
            yield return Barrier(h, 34);
            self0 = Revive.SelfRevives;
            if (host)
            {
                Plugin.Teleport(Own, Revive.LinkPosition + new Vector3(1.5f, 0f, 0f));
                yield return Wait(0.8f);
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                Revive.Downed d = Revive.DownedOf(1);
                // Read once it is down: the save point follows the host's checkpoints (a checkpoint zone at the new
                // spot may have fired), and a downed Ori makes none.
                Vector3 at = Revive.LinkPosition;
                float skull = d != null ? Vector3.Distance(d.Marker, at) : -1f;
                h.Check(d != null && d.LinkHealth == 0f && Revive.LinkFromCheckpoint && skull > 1f,
                    "R6b host: own Ori downed away from the save point, still no room save (skull d=" + F(skull) + ")");
                h.Check(Revive.SelfHintOnScreen(Players.LocalSeat), "R6b host: the self-revive text and its button glyph render on screen");
                Revive.TestHoldSelf = true;
                yield return Wait(1f);
                h.Check(Revive.DownedOf(1) != null, "R6b host: still down after holding Y for 1 s");
                yield return HoldSelfUntilUp(3f);
                float back = Vector3.Distance(Own.Position, at);
                h.Check(Revive.SelfRevives == self0 + 1 && Revive.DownedOf(1) == null && back < 1f,
                    "R6b host: holding Y brings it back at its latest checkpoint (d=" + F(back) + ")");
                h.Check(Mathf.Abs(Own.Mortality.Health.Amount - Own.Mortality.Health.MaxHealth) < 0.01f && Own.Energy.Current == 0f,
                    "R6b host: back with full health, energy untouched (" + F(Own.Mortality.Health.Amount) + "/" + F(Own.Mortality.Health.MaxHealth)
                    + ", energy " + F(Own.Energy.Current) + ")");
            }
            yield return Barrier(h, 35);
            yield return Wait(0.5f);
            if (!host) h.Check(Revive.DownedOf(2) == null && !ReviveHud.SkullShown(2), "R6b guest: the host's skull is gone after it came back on its own");
            h.Check(TestHarness.RestoreCheckpointCalls == restores0, "R6b " + (host ? "host" : "guest") + ": no checkpoint reload on this PC");
            // Later phases expect energy, full health and a room save made with nobody down.
            Own.Mortality.Health.RestoreAllHealth();
            Own.Energy.RestoreAllEnergy();
            if (host)
            {
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();
                h.Check(Revive.HaveLink && !Revive.LinkFromCheckpoint, "R6 host: the next room save is the save point again");
            }
            yield return Wait(0.5f);
            yield return Barrier(h, 36);
        }

        static IEnumerator WaitReal(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        // T-215 (#301): downed / revive leftovers, one step each (the ticket's item number in brackets).
        //   R7a [4] killed right after relocating (door, teleport, reload), before it stood anywhere in the new place:
        //           the skull is where it died, not on the ground it left.
        //   R7c [7] a save made at low health with nobody down; the guest goes down later; the host tells the save point
        //           again (as when a player joins): the guest still comes back with full health.
        //   R7d [6] the guest saves twice; the second time no teammate's Ori counts on its PC (as while the host loads):
        //           the host announces the second save where it was made, not at the first one's spot.
        //   R7f [3] the work a teammate's escape finish still owes is dropped when the session ends.
        //   R7b [1] a teammate's "revived you" arrives after its Ori left this PC: the revive still completes.
        //   R7e [2] the host is down; the guest's "I'm down" is followed at once by its "I'm back": no reload.
        static IEnumerator Leftovers(TestHarness h, bool host)
        {
            string me = host ? "host" : "guest";
            int seat = Players.LocalSeat;
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.6f);   // the last revive's invincibility runs out
            yield return Barrier(h, 40);

            // R7a [4]
            if (!host)
            {
                yield return Wait(0.5f);   // looked at on its ground (every 0.2 s)
                Vector3 ground = Own.Position;
                Plugin.Teleport(Own, ground + new Vector3(0f, 14f, 0f));
                Vector3 died = Own.Position;
                Hurt(Own, 1000f);          // the same frame: it never stood in the new place
                yield return Wait(0.6f);
                Revive.Downed d = Revive.DownedOf(1);
                float atDeath = d != null ? Vector3.Distance(d.Marker, died) : -1f, atOld = d != null ? Vector3.Distance(d.Marker, ground) : -1f;
                h.Check(d != null && atDeath >= 0f && atDeath < 1f && atOld > SafeGroundDistance,
                    "R7a guest: killed right after relocating: the skull is where it died, not on the ground it left (skull "
                    + F(atDeath) + " from the death, " + F(atOld) + " from the old ground)");
                yield return HoldSelfUntilUp(4f);
                h.Check(Revive.DownedOf(1) == null, "R7a guest: back at the save point");
            }
            yield return Barrier(h, 41);

            // R7c [7]
            float low = -1f;
            if (host)
            {
                Own.Mortality.Health.SetAmount(6f);
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();   // nobody is down: an ordinary save, creator health 6
                low = Own.Mortality.Health.Amount;
                Own.Mortality.Health.RestoreAllHealth();
            }
            yield return Barrier(h, 42, host ? Send(low) : "");
            float made = s_partnerSteps.ContainsKey(42) ? Parse(s_partnerSteps[42].Fingerprint) : -1f;
            if (!host)
            {
                yield return Wait(1.6f);
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(d != null && d.LinkHealth == 0f && made > 0f && made < Own.Mortality.Health.MaxHealth - 1f,
                    "R7c guest: downed after the host's save at " + F(made) + " health: that save is older than this down");
            }
            yield return Barrier(h, 43);
            if (host) Revive.TestResendRoomLink();
            yield return Barrier(h, 44);
            if (!host)
            {
                yield return WaitReal(0.3f);
                Revive.Downed d = Revive.DownedOf(1);
                h.Check(d != null && d.LinkHealth == 0f,
                    "R7c guest: the save point told again is not a soul link made while it is down (counts with health "
                    + (d != null ? F(d.LinkHealth) : "-") + ", the save was made at " + F(made) + ")");
                yield return HoldSelfUntilUp(4f);
                h.Check(Revive.DownedOf(1) == null && Mathf.Abs(Own.Mortality.Health.Amount - Own.Mortality.Health.MaxHealth) < 0.01f,
                    "R7c guest: comes back with full health, not the old save's (" + F(Own.Mortality.Health.Amount) + "/" + F(Own.Mortality.Health.MaxHealth) + ")");
            }
            yield return Barrier(h, 45);

            // R7d [6]
            int adopted0 = SaveSync.HostSavesAdopted;
            yield return Barrier(h, 46);
            SaveReport spot = new SaveReport();
            if (!host)
            {
                int commits0 = CoopSaves.CommitsReceived;
                Vector3 first = Own.Position;
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();   // the first save, told with its spot
                float end = Time.realtimeSinceStartup + 10f;
                while (CoopSaves.CommitsReceived == commits0 && Time.realtimeSinceStartup < end) yield return null;
                h.Check(CoopSaves.CommitsReceived > commits0, "R7d guest: its first save became the room's save");
                Plugin.Teleport(Own, Puppet.Position + new Vector3(1.5f, 0f, 0f));
                yield return Wait(0.8f);
                commits0 = CoopSaves.CommitsReceived;
                // No teammate's Ori counts on this PC for this save (Revive.Online is false), as while the host loads.
                Revive.Enabled = false;
                try
                {
                    GameController.Instance.CreateCheckpoint();
                    GameController.Instance.SaveGameController.PerformSave();
                }
                finally { Revive.Enabled = true; }
                spot.Position = Own.Position; spot.HasPosition = true;
                end = Time.realtimeSinceStartup + 10f;
                while (CoopSaves.CommitsReceived == commits0 && Time.realtimeSinceStartup < end) yield return null;
                float apart = Vector3.Distance(first, spot.Position);
                h.Check(CoopSaves.CommitsReceived > commits0 && apart > 1f,
                    "R7d guest: its second save, " + F(apart) + " from the first, became the room's save");
            }
            yield return Barrier(h, 47, "", spot);
            if (host)
            {
                SaveReport told;
                bool have = s_partnerSteps.TryGetValue(47, out told) && told.HasPosition;
                float end = Time.realtimeSinceStartup + 8f;
                while (SaveSync.HostSavesAdopted < adopted0 + 2 && Time.realtimeSinceStartup < end) yield return null;
                float d = have ? Vector3.Distance(Revive.LinkPosition, told.Position) : -1f;
                h.Check(SaveSync.HostSavesAdopted == adopted0 + 2 && Revive.LinkSeat == 2 && d >= 0f && d < 0.5f,
                    "R7d host: the guest's second save is announced where it was made, not at its previous save (adopted "
                    + (SaveSync.HostSavesAdopted - adopted0) + ", seat " + Revive.LinkSeat + ", d=" + F(d) + ")");
            }
            yield return Barrier(h, 48);

            // R7f [3]
            if (!host)
            {
                int fallbacks0 = Revive.EscapeFallbacks;
                Vector3 before = Own.Position;
                Revive.TestOweFallback(1, before + new Vector3(0f, 8f, 0f), 0.4f);   // the host finished an escape 8 units away
                bool owed = Revive.EscapeWorkOwed;
                Revive.TestEscapeSessionEnded();
                yield return WaitReal(1.2f);
                float moved = Vector3.Distance(Own.Position, before);
                h.Check(owed && !Revive.EscapeWorkOwed && Revive.EscapeFallbacks == fallbacks0 && moved < 1f,
                    "R7f guest: after the session's end an escape finish no longer pulls it to the finisher (owed before " + owed
                    + ", after " + Revive.EscapeWorkOwed + ", placements " + (Revive.EscapeFallbacks - fallbacks0) + ", moved " + F(moved) + ")");
            }
            yield return Barrier(h, 49);

            // R7b [1]
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.6f);
            yield return Barrier(h, 50);
            if (!host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                Revive.Downed d = Revive.DownedOf(1);
                Vector3 skull = d != null ? d.Marker : Own.Position;
                h.Check(d != null && Own.Mortality.Health.Amount <= 0f, "R7b guest: own Ori downed at 0 health");
                bool done = Revive.TestReviveWithoutRescuer(seat, 5f);   // "revived you, giving 5" from a rescuer whose Ori is gone
                yield return null;
                float hp = Own.Mortality.Health.Amount, at = Vector3.Distance(Own.Position, skull);
                h.Check(done && Revive.DownedOf(1) == null && Mathf.Abs(hp - 5f) < 0.01f,
                    "R7b guest: revived although the rescuer's Ori is gone: it has the health it was given, not 0 (health " + F(hp) + ")");
                h.Check(at < 1.5f, "R7b guest: it stands at its skull (d=" + F(at) + ")");
            }
            yield return Barrier(h, 51);
            yield return Wait(0.5f);
            if (host) h.Check(Revive.DownedOf(2) == null && !ReviveHud.SkullShown(2), "R7b host: the guest's skull is gone after that revive");

            // R7e [2]
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.6f);
            yield return Barrier(h, 52);
            int wipes0 = Revive.NetBothDown, restores0 = TestHarness.RestoreCheckpointCalls, held0 = Revive.WipesHeldBack;
            if (host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null, "R7e host: own Ori downed first");
            }
            yield return Barrier(h, 53);
            if (!host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end) yield return null;
                Hurt(Own, 1000f);   // its "I'm down" reaches the host: for a moment everyone looks down
                bool down = Revive.DownedOf(1) != null;
                yield return WaitReal(0.15f);
                bool back = Revive.TestSelfReviveNow(seat);   // and its "I'm back" right behind
                h.Check(down && back && Revive.DownedOf(1) == null, "R7e guest: down while the host is down, and up again 0.15 s later (down " + down + ", back " + back + ")");
            }
            yield return Barrier(h, 54);
            yield return WaitReal(2f);   // well past the host's grace
            h.Check(Revive.NetBothDown == wipes0 && TestHarness.RestoreCheckpointCalls == restores0,
                "R7e " + me + ": no 'everyone is down' reload, the guest was already up (declared " + (Revive.NetBothDown - wipes0)
                + ", reloads " + (TestHarness.RestoreCheckpointCalls - restores0) + ")");
            if (host)
            {
                h.Note("R7e host: reload held back " + (Revive.WipesHeldBack - held0) + " time(s)");
                h.Check(Revive.DownedOf(1) != null && Revive.DownedOf(2) == null, "R7e host: still down itself, the guest is up");
                yield return HoldSelfUntilUp(4f);
                h.Check(Revive.DownedOf(1) == null, "R7e host: back at the save point");
            }
            yield return Barrier(h, 55);
            // Later phases expect full health and a room save made with nobody down.
            Own.Mortality.Health.RestoreAllHealth();
            if (host)
            {
                yield return Wait(0.3f);
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();
            }
            yield return Wait(0.5f);
            yield return Barrier(h, 56);
        }
        const float SafeGroundDistance = 12f;   // Rules.SafeGroundRules.RelocatedDistance, written out: the expected value is the ticket's, not the code's

        // Game seconds (T-071): every wait here is for something the game times (hurt flash, invincibility, death, the
        // revive hold and glow); real seconds failed whenever a busy PC ran the game slower than real time.
        static IEnumerator Wait(float s) { return TestClock.WaitGame(s); }

        static IEnumerator Barrier(TestHarness h, int step, string fingerprint = "", SaveReport save = null)
        {
            NetPlay.Send(MsgBarrier, true, delegate(BinaryWriter w)
            {
                w.Write(step); w.Write(SaveSync.HostSavesAdopted); w.Write(fingerprint ?? "");
                bool hasPosition = save != null && save.HasPosition;
                w.Write(hasPosition);
                if (hasPosition) { w.Write(save.Position.x); w.Write(save.Position.y); w.Write(save.Position.z); }
            });
            float end = Time.realtimeSinceStartup + 30f;
            while (NetPlay.Connected && !s_partnerSteps.ContainsKey(step) && Time.realtimeSinceStartup < end) yield return null;
            h.Check(s_partnerSteps.ContainsKey(step), "R barrier " + step + " reached by both");
        }

        static string CheckpointFingerprint()
        {
            using (MemoryStream stream = new MemoryStream())
            {
                Game.Checkpoint.SaveGameData.SaveToWriter(new BinaryWriter(stream));
                return CoopSaves.Hash(stream.ToArray());
            }
        }

        // T-043 "R save": the guest touches a save point. The host (the room's save authority) names it the saver,
        // takes its save as the room's checkpoint and commits the file to the guest's local copy. Both checkpoints are
        // then identical, and the both-down reload in R3 puts the host at the guest's save point.
        static IEnumerator GuestSave(TestHarness h, SaveReport save)
        {
            bool host = NetPlay.IsHost;
            if (host)
            {
                save.HasPosition = SaveSync.HaveCheckpointPosition;
                save.Position = SaveSync.HostCheckpointPosition;
                if (!save.HasPosition) h.Note("R save: host's previous checkpoint position unavailable before guest save");
            }
            h.Check(NetPlay.LocalInGameplay() && Own != null && Puppet != null && SaveSync.Ready,
                "R save: both Oris in gameplay and SaveSync ready before the guest's save point");
            int adopted0 = SaveSync.HostSavesAdopted, commits0 = CoopSaves.CommitsReceived, offered0 = SaveSync.GuestSavesOffered;
            yield return Barrier(h, 200);
            if (!s_partnerSteps.ContainsKey(200)) yield break;
            if (!host)
            {
                // Saved at full health: the R3 reload restores this save on both PCs, and later phases (S4) expect full
                // health as after the host's save.
                Own.Mortality.Health.RestoreAllHealth();
                save.Position = Own.Position; save.HasPosition = true;
                GameController.Instance.CreateCheckpoint();
                GameController.Instance.SaveGameController.PerformSave();
                h.Check(SaveSync.GuestSavesOffered == offered0 + 1, "R save guest: save point offered to the room ("
                    + offered0 + " -> " + SaveSync.GuestSavesOffered + ")");
            }
            float end = Time.realtimeSinceStartup + 10f;
            while ((host ? SaveSync.HostSavesAdopted == adopted0 : CoopSaves.CommitsReceived == commits0)
                && Time.realtimeSinceStartup < end) yield return null;
            if (!host)
                h.Check(CoopSaves.CommitsReceived > commits0, "R save guest: the room's new save came back as a commit ("
                    + commits0 + " -> " + CoopSaves.CommitsReceived + ")");
            string fingerprint = CheckpointFingerprint();
            yield return Barrier(h, 201, fingerprint, save);
            if (!s_partnerSteps.ContainsKey(201)) yield break;
            int before = host ? adopted0 : s_partnerSteps[200].Adoptions;
            int after = host ? SaveSync.HostSavesAdopted : s_partnerSteps[201].Adoptions;
            h.Check(after == before + 1, "R save: the host named the guest the saver once (adoptions " + before + " -> " + after
                + ", last verdict " + SaveSync.LastOfferVerdict + ")");
            h.Check(fingerprint == s_partnerSteps[201].Fingerprint,
                "R save: checkpoint fingerprints equal (local SaveGameData SHA1=" + fingerprint
                + ", partner SaveGameData SHA1=" + s_partnerSteps[201].Fingerprint + ")");
            if (host)
                h.Check(CoopSaves.Timeline.Length > 0 && CoopSaves.Timeline == CoopSaves.LastCommitHash,
                    "R save host: the guest's save is the room's committed timeline (" + CoopSaves.Timeline + ")");
        }

        static void Hurt(SeinCharacter s, float amount)
        {
            s.Mortality.DamageReciever.OnRecieveDamage(new Damage(amount, Vector2.zero, s.Position, DamageType.Spikes, null));
        }

        static void Shot(TestHarness h, string name)
        {
            try
            {
                string p = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(h.ResultPath)), name + ".png");
                Application.CaptureScreenshot(p);
                h.Note("screenshot " + p);
            }
            catch (System.Exception e) { h.Note("screenshot failed: " + e.Message); }
        }

        static void HoldSpiritFlame(bool on) { NetTestHarness.BotButtons = on ? P2Input.BTN_X : 0; NetTestHarness.BotLX = 0f; }

        public static IEnumerator Run(TestHarness h)
        {
            s_partnerSteps.Clear();
            NetPlay.On(MsgBarrier, OnBarrier);
            bool host = NetPlay.IsHost;
            h.Note("--- phase R: online downed / revive (" + (host ? "host" : "guest") + ")");
            if (DifficultyController.Instance.Difficulty == DifficultyMode.OneLife) { h.Note("R skipped on One Life difficulty"); yield break; }
            if (Own == null || Puppet == null) { h.Check(false, "R needs both Oris"); yield break; }
            int restores0 = TestHarness.RestoreCheckpointCalls;
            Own.Mortality.Health.RestoreAllHealth();
            yield return Barrier(h, 1);
            // T-202: the way a real session starts. The host loaded its game alone; when the guest's Ori first appeared
            // the room already had a save point (the host's checkpoint) and the guest was told. Before T-202 there was
            // none until the first save made with a teammate present, so a downed player had nothing to hold Y for.
            if (host)
                h.Check(Revive.TeammateArrivals > 0 && Revive.FirstArrivalHadPoint,
                    "R6 host: the room had a save point when the guest's Ori first appeared, before any save in co-op (arrivals "
                    + Revive.TeammateArrivals + ", from the host's checkpoint: " + Revive.FirstArrivalFromCheckpoint + ")");
            else
                h.Check(Revive.HaveLink, "R6 guest: knows the room's save point before anyone made a soul link for it");

            // R0: partner hurt visuals (P4). Guest is hurt first (host watches), then the host (guest watches).
            for (int round = 0; round < 2; round++)
            {
                bool iAmHurt = (round == 0) != host;
                int sent0 = PartnerFx.HurtSent, played0 = PartnerFx.HurtPlayed;
                PartnerFx.LastHurtStateOn = PartnerFx.LastHurtAnimOn = false;
                if (!iAmHurt) Plugin.Teleport(Own, Puppet.Position + new Vector3(1.5f, 0f, 0f)); // watch from next to it
                yield return Barrier(h, 100 + round * 2);
                yield return Wait(0.4f);
                if (iAmHurt)
                {
                    float hp0 = Own.Mortality.Health.Amount;
                    Hurt(Own, 4f);
                    h.Check(Own.Mortality.Health.Amount < hp0 && PartnerFx.HurtSent == sent0 + 1, "R0." + round + " " + (host ? "host" : "guest") + ": own hurt accepted and sent (" + F(hp0) + " -> " + F(Own.Mortality.Health.Amount) + ")");
                    yield return Wait(1.2f);
                }
                else
                {
                    bool sawRed = false, sawHurtState = false, ownRed = false;
                    Renderer spr = Puppet.PlatformBehaviour.Visuals.SpriteRenderer;
                    Renderer ownSpr = Own.PlatformBehaviour.Visuals.SpriteRenderer;
                    bool shot = false;
                    float end = Time.realtimeSinceStartup + 4f;
                    while (Time.realtimeSinceStartup < end && !(sawRed && sawHurtState && PartnerFx.HurtPlayed > played0 && shot))
                    {
                        if (spr != null && spr.sharedMaterial.GetColor(ShaderProperties.TintColor).r > 0.5f) sawRed = true;
                        if (ownSpr != null && ownSpr.sharedMaterial.GetColor(ShaderProperties.TintColor).r > 0.5f) ownRed = true;
                        if (PartnerFx.HurtPlayed > played0 && PartnerFx.LastHurtStateOn && PartnerFx.LastHurtAnimOn) sawHurtState = true;
                        if (sawRed && !shot) { Shot(h, "fx-hurt-" + (host ? "host" : "guest") + "-sees-partner"); shot = true; }
                        yield return null;
                    }
                    h.Check(PartnerFx.HurtPlayed > played0, "R0." + round + " " + (host ? "host" : "guest") + ": partner's hurt received and replayed on the puppet");
                    h.Check(sawRed, "R0." + round + " " + (host ? "host" : "guest") + ": puppet flashes red (game's FlashSprite)");
                    h.Check(sawHurtState, "R0." + round + " " + (host ? "host" : "guest") + ": puppet enters the hurt state (animation + knockback)");
                    h.Check(!ownRed, "R0." + round + " " + (host ? "host" : "guest") + ": own Ori does not flash with it");
                }
                yield return Barrier(h, 101 + round * 2);
            }
            Own.Mortality.Health.RestoreAllHealth();
            yield return Wait(1.2f); // invincibility from R0 runs out

            // R1: the guest goes down.
            if (!host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null && TestHarness.RestoreCheckpointCalls == restores0, "R1 guest: own Ori downed, no reload");
                h.Check(Revive.SpectateActive && Revive.SpectateTarget == 1 && UI.Cameras.Current != null
                    && UI.Cameras.Current.Target == Puppet.Transform && CutsceneGuard.IsPlayerCameraTarget(Puppet.Transform),
                    "R1 guest: camera follows host puppet while downed");
                h.Check(Revive.NetDownedSent >= 1, "R1 guest: 'downed' sent to the host");
                h.Check(PartnerFx.LocalDownPlayed >= 1 && PartnerFx.DownSent >= 1, "R1 guest: own death burst played and 'down' visuals sent");
            }
            yield return Barrier(h, 2);
            if (host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end) yield return null;
                yield return Wait(0.3f);
                Revive.Downed d = Revive.DownedOf(2);
                h.Check(d != null && d.Remote, "R1 host: the guest's Ori is shown downed");
                h.Check(PartnerFx.DownPlayed >= 1, "R1 host: the guest's death burst played on the puppet");
                // (The skull label is only drawn while it is on this PC's screen; checked in R2 once the host is there.)
                if (d != null)
                {
                    int vis = 0; foreach (Renderer r in Puppet.GetComponentsInChildren<Renderer>()) if (r.enabled) vis++;
                    h.Check(vis == 0, "R1 host: the guest's puppet is hidden while downed (" + vis + " renderers on)");
                    h.Check(Vector3.Distance(d.Marker, Puppet.Position) < 3f, "R1 host: skull at the guest's spot (d=" + F(Vector3.Distance(d.Marker, Puppet.Position)) + ")");
                }
            }
            yield return Barrier(h, 3);

            // R2: the host revives the guest.
            float h0 = 0f;
            if (host && Revive.DownedOf(2) != null)
            {
                Revive.Downed d = Revive.DownedOf(2);
                Plugin.Teleport(Own, d.Marker + new Vector3(0.8f, 0f, 0f));
                yield return Wait(0.8f);
                h.Check(ReviveHud.SkullShown(2), "R2 host: skull shown for the guest once it is on screen");
                h.Check(ReviveHud.LastHintText.Length > 0, "R2 host: hold-to-revive hint shown at the guest's skull");
                h0 = Own.Mortality.Health.Amount;
                HoldSpiritFlame(true);
                yield return Wait(0.75f);
                HoldSpiritFlame(false);
                yield return Wait(0.1f);
                float p = d.Progress, drained = h0 - Own.Mortality.Health.Amount;
                h.Note("R2 host: after 0.75 s (game) hold progress " + F(p * 100f) + "%, health " + F(h0) + " -> " + F(Own.Mortality.Health.Amount));
                h.Check(p > 0.35f && p < 0.65f, "R2 host: revive progress advances while holding Spirit Flame");
                h.Check(Mathf.Abs(drained - h0 / 2f * p) < 0.6f, "R2 host: own health drains in step with progress");
            }
            yield return Barrier(h, 4);
            if (!host)
            {
                yield return Wait(0.4f);
                Revive.Downed d1 = Revive.DownedOf(1);
                h.Check(d1 != null && d1.Progress > 0.3f, "R2 guest: sees the host's revive progress on its own skull (" + (d1 != null ? F(d1.Progress * 100f) : "-") + "%)");
            }
            yield return Barrier(h, 5);
            int fx0 = PartnerFx.RevivesPlayed, fxEff0 = PartnerFx.ReviveEffects, fxSnd0 = PartnerFx.ReviveSounds;
            // Screenshot this PC's view 0.2 s into the glow, whenever it starts.
            PartnerFx.ReviveHook = s => PartnerFx.RunLater(0.2f, () => Shot(h, "fx-revive-" + (host ? "host" : "guest") + "-glow"));
            if (host && Revive.DownedOf(2) != null)
            {
                HoldSpiritFlame(true);
                TestClock.Window hold = new TestClock.Window(1.2f);
                while (Revive.DownedOf(2) != null && hold.Open) yield return null;
                HoldSpiritFlame(false);
                yield return Wait(0.15f);
                int visGlow = 0; foreach (Renderer r in Puppet.GetComponentsInChildren<Renderer>()) if (r.enabled) visGlow++;
                h.Check(PartnerFx.RevivesPlayed == fx0 + 1 && PartnerFx.ReviveEffects == fxEff0 + 1, "R2 host: revive glow played once (" + PartnerFx.LastReviveEffect + ")");
                h.Check(PartnerFx.ReviveSounds == fxSnd0 + 1, "R2 host: revive sound played (" + PartnerFx.LastReviveSound + ")");
                h.Check(visGlow == 0, "R2 host: guest still hidden during the glow (" + visGlow + " renderers on)");
                yield return Wait(0.6f);
                Shot(h, "fx-revive-host-after");
                h.Check(Revive.DownedOf(2) == null && Revive.NetRevivesSent >= 1, "R2 host: revive completes and is sent");
                h.Note("R2 host: health after revive " + F(Own.Mortality.Health.Amount) + " (started " + F(h0) + ")");
                h.Check(Mathf.Abs(Own.Mortality.Health.Amount - h0 / 2f) < 0.6f, "R2 host: paid half its health");
            }
            yield return Barrier(h, 6);
            if (!host)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(1) != null && Time.realtimeSinceStartup < end) yield return null;
                h.Check(PartnerFx.RevivesPlayed == fx0 + 1 && PartnerFx.ReviveEffects == fxEff0 + 1 && PartnerFx.ReviveSounds == fxSnd0 + 1,
                    "R2 guest: revive glow + sound played on own Ori (" + PartnerFx.LastReviveEffect + ", " + PartnerFx.LastReviveSound + ")");
                yield return Wait(PartnerFx.GlowSeconds + PartnerFx.FadeSeconds + 0.2f);
                h.Check(Revive.DownedOf(1) == null && Revive.NetRevivesApplied >= 1, "R2 guest: revived by the host");
                h.Check(!Revive.SpectateActive && UI.Cameras.Current != null && (UI.Cameras.Current.Target == Own.Transform || OriCoop.Story.StoryHost.Current.IsPluginCameraTarget(UI.Cameras.Current.Target)),
                    "R2 guest: camera returns to own Ori after revive");
                int vis = 0; foreach (Renderer r in Own.GetComponentsInChildren<Renderer>()) if (r.enabled) vis++;
                h.Note("R2 guest: health after revive " + F(Own.Mortality.Health.Amount));
                h.Check(vis > 0 && !Own.Controller.IsSuspended, "R2 guest: own Ori visible and not frozen again");
                h.Check(Own.Mortality.Health.Amount >= 1f && Own.Mortality.Health.Amount <= Own.Mortality.Health.MaxHealth / 2f + 0.6f, "R2 guest: came back with the health the host gave (" + F(Own.Mortality.Health.Amount) + ")");
                h.Check(Vector3.Distance(Own.Position, Puppet.Position) < 3f, "R2 guest: back next to the host");
            }
            yield return Barrier(h, 7);
            if (host)
            {
                yield return Wait(0.5f);
                h.Check(Revive.DownedOf(2) == null, "R2 host: guest's skull gone");
                int vis = 0; foreach (Renderer r in Puppet.GetComponentsInChildren<Renderer>()) if (r.enabled) vis++;
                h.Check(vis > 0, "R2 host: guest's puppet visible again");
            }

            // Make the saver's checkpoint tree different even when running R on its own.
            string tree = ProgressSync.OwnTreeBits.ToString("X");
            yield return Barrier(h, 202, tree);
            SaveReport peer;
            if (s_partnerSteps.TryGetValue(202, out peer) && peer.Fingerprint == tree)
            {
                if (host) Own.Level.SkillPoints += 20;
                else yield return NetTestHarness.BuyCheckpointSkill(h, "R skills setup", 1);
            }
            yield return Barrier(h, 203);
            int records0 = SeatSkillSync.RecordsReceived;
            SaveReport save = new SaveReport();
            yield return GuestSave(h, save);

            float recordBy = Time.realtimeSinceStartup + 5f;
            while ((SeatSkillSync.Current == null || SeatSkillSync.Current.Count != 2
                || !host && SeatSkillSync.RecordsReceived == records0) && Time.realtimeSinceStartup < recordBy) yield return null;
            h.Check(SeatSkillSync.Current != null && SeatSkillSync.Current.Count == 2 && (host || SeatSkillSync.RecordsReceived > records0),
                "R skills: the room record has both seats");
            ulong checkpointTree = ProgressSync.OwnTreeBits;
            int checkpointPoints = Own.Level.SkillPoints;
            yield return Barrier(h, 204);
            if (host) Own.Level.SkillPoints += 20;
            yield return NetTestHarness.BuyCheckpointSkill(h, "R skills", host ? 1 : 2);
            // An auto checkpoint on the guest after its purchase: its own native reload would now keep the skill, only
            // the room record rolls it back. (On the host a checkpoint IS the room's: it would record the purchase.)
            if (!host) GameController.Instance.CreateCheckpoint();
            int loads0 = SaveSync.GuestLoads;

            // R3: both down -> the host decides, both reload.
            Own.Mortality.Health.RestoreAllHealth();
            yield return Barrier(h, 8);
            restores0 = TestHarness.RestoreCheckpointCalls;
            int bothDown0 = Revive.NetBothDown;
            // T-144: a one-shot hazard that fired before the wipe must be re-armed by the reload on this PC.
            const int oneShotId = 0x54313434;
            WorldObjSync.TestMarkFired(oneShotId);
            if (host)
            {
                Hurt(Own, 1000f);
                yield return Wait(0.6f);
                h.Check(Revive.DownedOf(1) != null, "R3 host: own Ori downed first");
            }
            yield return Barrier(h, 9);
            if (!host)
            {
                float end0 = Time.realtimeSinceStartup + 5f;
                while (Revive.DownedOf(2) == null && Time.realtimeSinceStartup < end0) yield return null;
                h.Check(Revive.DownedOf(2) != null, "R3 guest: sees the host's skull");
                Hurt(Own, 1000f);
            }
            // T-026: once "both down" is decided the partner's skull must go at once (0.3 s grace) and stay gone through
            // this PC's death, the reload and the partner's return; it used to stay frozen on the HUD until then.
            float bothAt = -1f, lingerFirst = -1f; int lingerFrames = 0;
            System.Action sampleSkull = () =>
            {
                float now = Time.realtimeSinceStartup;
                if (bothAt < 0f && Revive.NetBothDown > bothDown0) bothAt = now;
                if (bothAt >= 0f && now > bothAt + 0.3f && ReviveHud.SkullShown(2)) { lingerFrames++; if (lingerFirst < 0f) lingerFirst = now - bothAt; }
            };
            float endR = Time.realtimeSinceStartup + 30f;
            while (TestHarness.RestoreCheckpointCalls == restores0 && Time.realtimeSinceStartup < endR) { sampleSkull(); yield return null; }
            h.Check(TestHarness.RestoreCheckpointCalls > restores0, "R3 both down -> checkpoint reload on this PC");
            endR = Time.realtimeSinceStartup + 30f;
            while ((Characters.Sein == null || !Characters.Sein.gameObject.activeInHierarchy || GameController.Instance.InputLocked) && Time.realtimeSinceStartup < endR) { sampleSkull(); yield return null; }
            float endSettle = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < endSettle) { sampleSkull(); yield return null; }
            h.Check(bothAt >= 0f, "R3 'both down' reached this PC");
            h.Check(lingerFrames == 0, "R3 partner's skull hidden within 0.3 s of 'both down' and through the reload (T-026): "
                + lingerFrames + " frames still shown, first at +" + lingerFirst.ToString("F2") + " s");
            h.Check(!Revive.AnyDowned, "R3 nobody downed after the reload");
            h.Check(!WorldObjSync.TestHasFired(oneShotId), "R3 the reload forgets one-shot hazards that fired before it (T-144, resets=" + WorldObjSync.OneShotResets + ")");
            h.Check(Revive.NetBothDown >= 1, "R3 'both down' was decided once (host) and applied here");
            if (host)
            {
                SaveReport guestSave;
                if (!save.HasPosition)
                    h.Note("R save: reload source undecidable: host's previous checkpoint position unavailable before guest save");
                else if (!s_partnerSteps.TryGetValue(201, out guestSave) || !guestSave.HasPosition)
                    h.Note("R save: reload source undecidable: guest save position not received");
                else
                {
                    float previousDistance = Vector3.Distance(guestSave.Position, save.Position);
                    if (previousDistance <= 3f)
                        h.Note("R save: reload source undecidable: guest save position was " + F(previousDistance)
                            + " units from host's previous checkpoint (<= 3 units)");
                    else
                    {
                        float distance = Characters.Sein == null ? float.PositiveInfinity
                            : Vector3.Distance(Characters.Sein.Position, guestSave.Position);
                        h.Check(distance <= 3f,
                            "R save: after both-down reload the host respawns at the guest's save (distance to guest save position="
                            + F(distance) + " <= 3 units; was " + F(previousDistance) + " to the host's previous checkpoint)");
                    }
                }
            }
            yield return NetTestHarness.CheckCheckpointSkills(h, "R skills: back to the checkpoint tree and points",
                checkpointTree, checkpointPoints, loads0);
            // Step 201 captured the saver's position before CreateCheckpoint. Compare the final playable
            // position on each PC, after the guest has acknowledged the authoritative checkpoint transfer.
            SaveReport checkpoint = save;
            if (host) s_partnerSteps.TryGetValue(201, out checkpoint);
            Vector3 settled = Own != null ? Own.Position : Vector3.zero;
            h.Check(Own != null && checkpoint != null && checkpoint.HasPosition
                && Mathf.Abs(settled.x - checkpoint.Position.x) <= 0.25f
                && Mathf.Abs(settled.y - checkpoint.Position.y) <= 0.5f,
                "R3 team wipe respawns this seat at the saved checkpoint (T-243; final=" + settled.ToString("F2")
                + ", expected=" + (checkpoint == null ? "missing" : checkpoint.Position.ToString("F2")) + ")");
            yield return Barrier(h, 10);
            yield return SoulLink(h, host);
            yield return DeathRules(h, host);
            yield return SelfRespawn(h, host);
            yield return Leftovers(h, host);
            HoldSpiritFlame(false);
            PartnerFx.ReviveHook = null;
        }
    }
}
