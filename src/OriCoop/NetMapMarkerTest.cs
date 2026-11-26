using System;
using System.Collections;
using UnityEngine;

namespace OriCoop
{
    // Reads the rendered marker, including its seat text, without consulting the marker service.
    internal static class NetMapMarkerTest
    {
        const string GuestName = "Map Finch";

        public static Action NameGuest()
        {
            Action restore = TestHarness.PreservePrimaryName();
            TeammateIdentity.SetLocalName(Players.LocalSeat, GuestName);
            return restore;
        }

        static TextMesh Label(Transform group, int seat = 2)
        {
            if (group == null) return null;
            string tag = Roles.Tag(seat);
            foreach (TextMesh label in group.GetComponentsInChildren<TextMesh>())
                if (label.text == tag || (label.text != null && label.text.StartsWith(tag + " ", StringComparison.Ordinal))) return label;
            return null;
        }

        public static void CheckPosition(TestHarness h, bool world)
        {
            AreaMapUI area = AreaMapUI.Instance;
            WorldMapUI overview = WorldMapUI.Instance;
            TextMesh label = Label(world ? (overview == null ? null : overview.FadeOutGroup) : area.FadeOutGroup);
            Vector3 pos;
            bool received = NetPlay.RemotePos(2, out pos);
            float error = float.PositiveInfinity;
            if (received && label != null)
            {
                Transform marker = label.transform.parent;
                error = world ? Vector3.Distance(marker.position, overview.WorldToUIPosition(pos))
                    : Vector3.Distance(marker.localPosition, area.Navigation.WorldToMapPosition(pos + area.PlayerPositionOffset + Vector3.up));
            }
            Color32 rendered = label == null ? Color.clear : label.color;
            Color32 expected = Roles.ColorOf(2);
            bool tinted = rendered.r == expected.r && rendered.g == expected.g && rendered.b == expected.b;
            bool captionVisible = label != null && label.GetComponent<Renderer>().enabled && rendered.a > 40;
            bool iconVisible = false;
            bool sameLayer = false;
            if (label != null)
                foreach (Renderer part in label.transform.parent.GetComponentsInChildren<Renderer>())
                {
                    if (!part.enabled || part.gameObject == label.gameObject || part.sharedMaterial == null || !part.sharedMaterial.HasProperty("_Color")) continue;
                    Color32 icon = part.sharedMaterial.color;
                    if (icon.r == expected.r && icon.g == expected.g && icon.b == expected.b && icon.a > 8) iconVisible = true;
                    if (part.sortingLayerID == label.GetComponent<Renderer>().sortingLayerID) sameLayer = true;
                }
            string view = world ? "world" : "area";
            h.Check(received && label != null && error < 0.06f,
                "S4 map: P2 has a marker at its native " + view + " map projection (received=" + received + ", label=" + (label != null) + ", error=" + error + ")");
            h.Check(tinted && captionVisible && iconVisible && sameLayer, "S4 map: P2's " + view + " map icon and caption show its seat colour on the native sorting layer (rgba="
                + rendered + ", caption visible=" + captionVisible + ", icon visible=" + iconVisible + ", matching layer=" + sameLayer + ")");
            h.Check(captionVisible && label.text == Roles.Tag(2) + " " + GuestName,
                "S4 map: the rendered " + (world ? "world" : "area") + " map shows P2's transmitted custom name");
        }

        public static IEnumerator WaitForSeat(TestHarness h, bool present)
        {
            bool matches = false;
            TestClock.Window window = new TestClock.Window(5f);
            while (window.Open && !matches)
            {
                Vector3 pos;
                matches = NetPlay.RemotePos(2, out pos) == present
                    && (Label(AreaMapUI.Instance.FadeOutGroup) != null) == present;
                if (!matches) yield return null;
            }
            h.Check(matches, present ? "S4 map: P2's marker returns after its position stream resumes"
                : "S4 map: P2's marker disappears when the peer leaves gameplay");
        }

        public static IEnumerator LocalSeat(TestHarness h)
        {
            if (!NetPlay.MultiSeatSupported || NetPlay.LocalSeats.Length != 1) yield break;
            bool added = NetPlay.AddLocalSeat();
            int extra = Array.Find(NetPlay.LocalSeats, seat => seat != Players.LocalSeat);
            h.Check(added && extra > 0, "S4 map: host admitted a couch teammate");
            if (!added || extra == 0) yield break;
            AreaMapUI area = AreaMapUI.Instance;
            try
            {
                TestClock.Window window = new TestClock.Window(4f);
                bool placed = false;
                while (window.Open && !placed)
                {
                    SeinCharacter ownExtra = Players.OriOf(extra);
                    TextMesh label = Label(area.FadeOutGroup, extra);
                    if (ownExtra != null && label != null)
                    {
                        Vector3 at = area.Navigation.WorldToMapPosition(ownExtra.Position + area.PlayerPositionOffset + Vector3.up);
                        placed = Vector3.Distance(label.transform.parent.localPosition, at) < 0.06f;
                    }
                    if (!placed) yield return null;
                }
                h.Check(placed, "S4 map: the couch teammate appears at its local Ori's projection");
            }
            finally { NetPlay.ReleaseLocalSeat(extra); }
            TestClock.Window leave = new TestClock.Window(4f);
            while (leave.Open && Label(area.FadeOutGroup, extra) != null) yield return null;
            h.Check(!Players.IsLocal(extra) && Label(area.FadeOutGroup, extra) == null,
                "S4 map: releasing the couch seat removes its visible marker");
        }
    }
}
