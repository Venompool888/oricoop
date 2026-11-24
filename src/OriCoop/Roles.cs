using UnityEngine;

namespace OriCoop
{
    // A player keeps their seat number when the room's authority changes. Internal character slots are separate.
    public static class Roles
    {
        // Same colours as the controller menu: P1 blue, P2 gold.
        public static readonly Color P1Color = new Color(0.498f, 0.816f, 1f);
        public static readonly Color P2Color = new Color(1f, 0.82f, 0.4f);
        public static readonly Color P3Color = new Color(0.55f, 0.9f, 0.55f);
        public static readonly Color P4Color = new Color(1f, 0.6f, 0.8f);

        public static int Mine { get { return Players.AssignedSeat != 0 ? Players.AssignedSeat : NetPlay.Enabled && !NetPlay.IsHost ? 2 : 1; } }
        public static int[] Others
        {
            get
            {
                int[] seats = NetPlay.Enabled ? NetPlay.RemoteSeats : new int[0];
                return seats.Length > 0 ? seats : new int[] { NetPlay.Enabled && !NetPlay.IsHost ? 1 : 2 };
            }
        }
        public static int Partner { get { return Others[0]; } }

        // Role of an internal slot (1 = this PC's Ori, 2 = the other Ori).
        public static int OfSlot(int slot) { return slot == 1 ? Mine : Partner; }

        public static string Tag(int role) { return L10n.F("roles.tag", role); }
        public static Color ColorOf(int role) { return role == 1 ? P1Color : role == 3 ? P3Color : role == 4 ? P4Color : P2Color; }
        public static string Hex(int role, float alpha)
        {
            return HexOf(ColorOf(role), alpha);
        }
        // #rrggbbaa for TextMesh rich text (a <color> tag replaces the mesh colour, so the fade goes into the tag).
        public static string HexOf(Color c, float alpha)
        {
            Color32 k = new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
            return "#" + k.r.ToString("x2") + k.g.ToString("x2") + k.b.ToString("x2") + k.a.ToString("x2");
        }

        public static string Word(int seat)
        {
            if (!NetPlay.Enabled) return L10n.T("roles.partner");
            bool server = NetPlay.UsesCServer;
            return L10n.T(L10n.RoleKey(server, seat == (server ? CsLobby.ManagerSeat : 1)));
        }

        // The other player's overhead label, plus the Steam name when there is one.
        public static string PartnerLabel()
        {
            int r = Partner;
            string name = NetPlay.SteamPartnerName;
            return string.IsNullOrEmpty(name) ? L10n.F("roles.label", Tag(r), Word(r)) : L10n.F("roles.named_label", Tag(r), Word(r), name);
        }
    }
}
