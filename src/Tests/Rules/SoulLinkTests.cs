using System;

using OriCoop.Rules;

// T-041 (#15): death save, revive at the latest soul link (msg 69 RoomLink wire).
static class SoulLinkTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }

    static DownedRoster<DownedState> Roster(params int[] downed)
    {
        DownedRoster<DownedState> r = new DownedRoster<DownedState>(ReviveLogic.MaxSeats);
        foreach (int seat in downed) r.TryAdd(seat, new DownedState { Player = seat });
        return r;
    }

    internal static void Run(Action<string, Action> run)
    {
        run("death save halves the creator's health", delegate {
            Check(Near(SoulLinkRules.DeathSaveHealth(16f), 8f), "16 -> " + SoulLinkRules.DeathSaveHealth(16f));
            Check(Near(SoulLinkRules.DeathSaveHealth(10f), 5f), "10 -> " + SoulLinkRules.DeathSaveHealth(10f));
        });
        run("death save keeps at least one cell", delegate {
            Check(Near(SoulLinkRules.DeathSaveHealth(6f), 4f), "6 -> " + SoulLinkRules.DeathSaveHealth(6f));
            Check(Near(SoulLinkRules.DeathSaveHealth(4f), 4f), "4 -> " + SoulLinkRules.DeathSaveHealth(4f));
        });
        run("death save never raises health below a cell", delegate {
            Check(Near(SoulLinkRules.DeathSaveHealth(2f), 2f), "2 -> " + SoulLinkRules.DeathSaveHealth(2f));
        });
        run("soul link made while down: revive with the creator's health", delegate {
            DownedRoster<DownedState> r = Roster(2, 3);
            SoulLinkRules.NoteLink(r, 8f);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 8f), "P2 got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[3], 16f), 8f), "P3 got " + SoulLinkRules.SelfReviveHealth(r.Seats[3], 16f));
        });
        run("no soul link while down: revive with full health", delegate {
            DownedRoster<DownedState> r = Roster(2);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 16f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("soul link made before going down does not count", delegate {
            DownedRoster<DownedState> r = Roster(3);
            SoulLinkRules.NoteLink(r, 8f);          // P3 down at the time, P2 still alive
            r.TryAdd(2, new DownedState { Player = 2 });
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 16f), "P2 went down after the link but got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("a newer soul link replaces the older one's health", delegate {
            DownedRoster<DownedState> r = Roster(2);
            SoulLinkRules.NoteLink(r, 8f);
            SoulLinkRules.NoteLink(r, 5f);
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 5f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("revive health is at least 1 and at most max", delegate {
            DownedState d = new DownedState { LinkHealth = 0.4f };
            Check(Near(SoulLinkRules.SelfReviveHealth(d, 16f), 1f), "low link -> " + SoulLinkRules.SelfReviveHealth(d, 16f));
            d.LinkHealth = 20f;
            Check(Near(SoulLinkRules.SelfReviveHealth(d, 12f), 12f), "high link -> " + SoulLinkRules.SelfReviveHealth(d, 12f));
        });
        run("hold Y for 2 s revives; releasing starts over", delegate {
            DownedState d = new DownedState();
            Check(!SoulLinkRules.HoldSelf(d, true, false, 1.5f), "done after 1.5 s");
            Check(!SoulLinkRules.HoldSelf(d, false, false, 0.1f), "done on release");
            Check(!SoulLinkRules.HoldSelf(d, true, false, 1.5f), "release did not restart the hold");
            Check(SoulLinkRules.HoldSelf(d, true, false, 0.6f), "not done after 2.1 s");
        });
        run("hold Y does not count during loading or cutscenes", delegate {
            DownedState d = new DownedState();
            Check(!SoulLinkRules.HoldSelf(d, true, true, 3f), "revived while blocked");
            Check(Near(d.SelfHold, 0f), "blocked hold kept " + d.SelfHold);
        });
        run("self revive cancels a teammate's revive in progress", delegate {
            DownedState d = new DownedState();
            ReviveLogic.BeginHold(d, 3); d.Progress = 0.6f; d.Given = 4f;
            SoulLinkRules.CancelRescue(d);
            Check(!d.Holding && d.Progress == 0f && d.Given == 0f && d.Rescuer == 0, "rescue still running");
        });

        run("room flame: the latest accepted marker replaces the old position for either creator", delegate {
            ReviveWire.Marker before = Marker(10f), after = Marker(20f);
            SavePointState state = new SavePointState();
            state.NoteSave(new ReviveWire.Link { Seat = 1, MarkerKnown = true, Flame = before });
            state.NoteCheckpoint(new ReviveWire.Link { Seat = 2, MarkerKnown = true, Flame = after });
            Check(!SoulLinkRules.NativeMarkerChanged(state.Point, before), "a guest proposal moved the marker before adoption");
            state.NoteSave(state.Checkpoint);
            Check(SoulLinkRules.NativeMarkerChanged(state.Point, before), "the adopted P2 marker did not replace P1's");
            state.NoteSave(new ReviveWire.Link { Seat = 1, MarkerKnown = true, Flame = before });
            Check(SoulLinkRules.NativeMarkerChanged(state.Point, after), "the adopted P1 marker did not replace P2's");
        });
        run("room flame: a different scene at the same position still updates the marker", delegate {
            ReviveWire.Marker before = Marker(10f), after = before;
            after.Scene = Guid.Empty;
            Check(SoulLinkRules.NativeMarkerChanged(new ReviveWire.Link { MarkerKnown = true, Flame = after }, before), "scene change ignored");
        });
        run("room flame: a well save keeps the same native marker", delegate {
            ReviveWire.Marker marker = Marker(10f);
            ReviveWire.Link well = new ReviveWire.Link { X = 25f, MarkerKnown = true, Flame = marker };
            Check(!SoulLinkRules.NativeMarkerChanged(well, marker), "a well save moved the native marker to its own save point");
            Check(SoulLinkRules.NativeMarkerChanged(well, null), "missing native marker was not created");
        });
        run("room flame: an explicit empty marker clears the old flame, an absent snapshot does not", delegate {
            ReviveWire.Link point = At(2, 25f, 20f, 6f);
            ReviveWire.Marker marker = Marker(10f);
            Check(!SoulLinkRules.NativeMarkerChanged(point, marker), "an old-format RoomLink cleared the marker");
            point.MarkerKnown = true;
            Check(SoulLinkRules.NativeMarkerChanged(point, marker), "a checkpoint without a marker left the old one");
            Check(!SoulLinkRules.NativeMarkerChanged(point, null), "a checkpoint without a marker created one");
        });
        run("room flame wire: position and scene round trip separately from the save point", delegate {
            ReviveWire.Link point = At(2, 25f, 20f, 6f);
            point.MarkerKnown = true; point.Flame = Marker(10f);
            ReviveWire.Link decoded = RoundTrip(point);
            Check(decoded.Seat == 2 && decoded.X == 25f && decoded.Health == 6f, "lost the save point or creator health");
            Check(decoded.MarkerKnown && decoded.Flame.HasValue && decoded.Flame.Value.X == 10f && decoded.Flame.Value.Y == -4f
                && decoded.Flame.Value.Z == 0.5f && decoded.Flame.Value.Scene == point.Flame.Value.Scene, "lost the native marker or its scene");
            point.Flame = null; decoded = RoundTrip(point);
            Check(decoded.MarkerKnown && !decoded.Flame.HasValue, "lost the explicit empty marker");
            point.MarkerKnown = false; decoded = RoundTrip(point);
            Check(!decoded.MarkerKnown, "a legacy link was decoded as a marker snapshot");
        });
        run("room link wire: a rejected save is distinct from a zero-health checkpoint", delegate {
            ReviveWire.Link point = At(1, 10f, 20f, 0f);
            point.MarkerKnown = true; point.Flame = Marker(10f);
            Check(!RoundTrip(point).OfferRejected, "a normal checkpoint rejected an offer");
            point.OfferRejected = true;
            ReviveWire.Link rejected = RoundTrip(point);
            Check(rejected.OfferRejected && rejected.MarkerKnown && rejected.Flame.HasValue && rejected.Flame.Value.X == 10f,
                "the rejection lost the accepted flame");
            point.Flame = null;
            rejected = RoundTrip(point);
            Check(rejected.OfferRejected && rejected.MarkerKnown && !rejected.Flame.HasValue, "the rejection lost the empty marker");
            point.MarkerKnown = false;
            rejected = RoundTrip(point);
            Check(rejected.OfferRejected && !rejected.MarkerKnown, "the rejection requires a known marker");
        });

        // T-202 (#285): a downed player always has a save point to hold Y / Q for.
        run("T-202 no room save yet: the host's latest checkpoint is the save point", delegate {
            SavePointState p = new SavePointState();
            Check(!p.Have && !p.Resolve(true), "a save point without any checkpoint");
            p.NoteCheckpoint(At(1, 10f, 20f, 12f));
            Check(p.Resolve(true), "the checkpoint did not become the save point");
            Check(p.Have && p.FromCheckpoint && Near(p.Point.X, 10f) && Near(p.Point.Y, 20f) && p.Point.Seat == 1,
                "save point " + p.Point.X + "," + p.Point.Y + " seat " + p.Point.Seat);
            Check(!p.Resolve(true), "announced the same checkpoint twice");
        });
        run("T-202 a checkpoint save point gives full health", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(1, 10f, 20f, 7f));   // the Ori had 7 health when the checkpoint was made
            p.Resolve(true);
            Check(Near(p.Point.Health, 0f), "checkpoint save point carries health " + p.Point.Health);
            DownedRoster<DownedState> r = Roster(2);
            if (p.Point.Health > 0f) SoulLinkRules.NoteLink(r, p.Point.Health);   // what Revive.NoteSavePoint does
            Check(Near(SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f), 16f), "got " + SoulLinkRules.SelfReviveHealth(r.Seats[2], 16f));
        });
        run("T-202 the checkpoint save point follows the host's newer checkpoints", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(1, 10f, 20f, 12f)); p.Resolve(true);
            p.NoteCheckpoint(At(1, 30f, 40f, 12f));
            Check(p.Resolve(true) && Near(p.Point.X, 30f) && Near(p.Point.Y, 40f), "stayed at " + p.Point.X + "," + p.Point.Y);
        });
        run("T-202 a room save replaces the checkpoint save point", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(1, 10f, 20f, 12f)); p.Resolve(true);
            p.NoteSave(At(2, 50f, 60f, 6f));
            Check(p.Have && !p.FromCheckpoint && Near(p.Point.X, 50f) && Near(p.Point.Health, 6f) && p.Point.Seat == 2,
                "save point " + p.Point.X + " health " + p.Point.Health);
        });
        run("T-202 after a room save a checkpoint no longer moves the save point", delegate {
            SavePointState p = new SavePointState();
            p.NoteSave(At(2, 50f, 60f, 6f));
            p.NoteCheckpoint(At(1, 70f, 80f, 12f));
            Check(!p.Resolve(true) && Near(p.Point.X, 50f) && Near(p.Point.Health, 6f), "moved to " + p.Point.X);
        });
        run("T-202 a soul link's own checkpoint does not undo it", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(1, 10f, 20f, 12f)); p.Resolve(true);
            p.NoteCheckpoint(At(1, 30f, 40f, 6f));   // CastSoulFlame: checkpoint, then the save, in one frame
            p.NoteSave(At(1, 30f, 40f, 6f));
            Check(!p.Resolve(true) && !p.FromCheckpoint && Near(p.Point.Health, 6f), "the soul link's health was lost: " + p.Point.Health);
        });
        run("T-202 only the save authority decides", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(2, 10f, 20f, 12f));
            Check(!p.Resolve(false) && !p.Have, "a guest made its own checkpoint the room's save point");
            p.NoteSave(At(1, 50f, 60f, 0f));   // the host's word
            Check(p.Have && Near(p.Point.X, 50f), "the host's save point was not taken");
            Check(p.Resolve(true) == false, "became host: its checkpoint replaced the room's save point");
        });
        run("T-202 a new session starts from this PC's latest checkpoint", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(1, 10f, 20f, 12f)); p.Resolve(true);
            p.NoteSave(At(2, 50f, 60f, 6f));
            p.NoteCheckpoint(At(1, 70f, 80f, 12f));
            p.SessionEnded();
            Check(!p.Have && p.HaveCheckpoint, "session end: have=" + p.Have + " checkpoint=" + p.HaveCheckpoint);
            Check(p.Resolve(true) && Near(p.Point.X, 70f) && p.FromCheckpoint, "next session at " + p.Point.X);
        });
        run("T-202 back at the title the checkpoint is forgotten", delegate {
            SavePointState p = new SavePointState();
            p.NoteCheckpoint(At(1, 10f, 20f, 12f));
            p.ForgetCheckpoint(); p.SessionEnded();
            Check(!p.HaveCheckpoint && !p.Resolve(true) && !p.Have, "the last game's checkpoint is still the save point");
        });
    }

    static ReviveWire.Marker Marker(float x)
    {
        return new ReviveWire.Marker { X = x, Y = -4f, Z = 0.5f, Scene = new Guid("12345678-1234-1234-1234-123456789abc") };
    }

    static ReviveWire.Link RoundTrip(ReviveWire.Link point)
    {
        using (var stream = new System.IO.MemoryStream())
        {
            ReviveWire.WriteLink(new System.IO.BinaryWriter(stream), point);
            stream.Position = 0;
            return ReviveWire.ReadLink(new System.IO.BinaryReader(stream));
        }
    }

    static ReviveWire.Link At(int seat, float x, float y, float health)
    {
        return new ReviveWire.Link { Seat = seat, X = x, Y = y, Health = health };
    }
}
