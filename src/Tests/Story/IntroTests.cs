using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Story;

static partial class StoryTests
{
    sealed class IntroFake : IIntroHost
    {
        public bool Authority = true, Playing = true, Free, Ground = true, AtTitle;
        public bool PlacementReady = true;
        public int[] Seats = new[] { 1, 2, 3, 4 };
        public StoryPoint Origin = new StoryPoint(20, 5, 0);
        public IntroState Remote;
        public bool HostSession = true;
        public readonly Dictionary<int, bool> Held = new Dictionary<int, bool>();
        public readonly Dictionary<int, StoryPoint> Placed = new Dictionary<int, StoryPoint>();
        public int Placements, GroundChecks;
        public bool IntroAuthority { get { return Authority; } }
        public bool IntroPlaying { get { return Playing; } }
        public bool IntroP1Free { get { return Free; } }
        public bool IntroAtTitle { get { return AtTitle; } }
        public StoryPoint IntroP1Position { get { return Origin; } }
        public int[] IntroSeats { get { return Seats; } }
        public IntroState RemoteIntro { get { return Remote; } }
        public bool IntroHostSession { get { return HostSession; } }
        public bool TryIntroGround(StoryPoint candidate, out StoryPoint spot)
        { GroundChecks++; spot = new StoryPoint(candidate.X, 4, candidate.Z); return Ground; }
        public bool CanPlaceIntroSeat(int seat) { return PlacementReady; }
        public void SetIntroHeld(int seat, bool held) { Held[seat] = held; }
        public void PlaceIntroSeat(int seat, StoryPoint position) { Placed[seat] = position; Placements++; }
        public void PublishIntro(IntroState state) { Remote = state; }
    }
    static void IntroCases()
    {
        for (int n = 2; n <= 4; n++)
        {
            int count = n;
            Check("intro holds and hides all except P1 with " + n + " seats", delegate {
                IntroFake h = new IntroFake(); h.Seats = new int[count];
                for (int i = 0; i < count; i++) h.Seats[i] = i + 1;
                IntroRule rule = new IntroRule(); rule.Tick(h);
                Equal(false, h.Held[1]);
                for (int seat = 2; seat <= count; seat++) Equal(true, h.Held[seat]);
                Equal(0, h.Placements);
                h.Playing = false; rule.Tick(h); // SetToGame alone is not wake / free movement.
                Equal(true, h.Held[2]); Equal(0, h.Placements);
                h.Free = true; rule.Tick(h);
                Equal(count - 1, h.Placements); Equal(false, h.Held[2]);
                Equal(3, h.GroundChecks); // Layout is per seat, not per connected player count.
                float[] offsets = { 0, 3.5f, -3.5f, 7 };
                for (int a = 1; a <= count; a++)
                {
                    StoryPoint p = a == 1 ? h.Origin : h.Placed[a];
                    Equal(h.Origin.X + offsets[a - 1], p.X);
                    for (int b = 1; b < a; b++)
                    {
                        StoryPoint q = b == 1 ? h.Origin : h.Placed[b];
                        if (Math.Abs(p.X - q.X) < 3) throw new Exception("spacing below 3 units");
                    }
                }
                rule.Tick(h); Equal(count - 1, h.Placements);
            });
        }
        Check("intro missing seats retain their fixed spots on delayed spawn", delegate {
            IntroFake h = new IntroFake(); h.Seats = new[] { 1, 4 };
            IntroRule rule = new IntroRule(); rule.Tick(h);
            h.Playing = false; h.Free = true; rule.Tick(h);
            Equal(27f, h.Placed[4].X); Equal(1, h.Placements);
            h.Origin = new StoryPoint(999, 999, 0); h.Seats = new[] { 1, 2, 4 };
            rule.Tick(h); Equal(23.5f, h.Placed[2].X); Equal(2, h.Placements);
        });
        Check("intro guest restore cannot consume its wake placement before loading finishes", delegate {
            IntroFake h = new IntroFake(); h.PlacementReady = false;
            IntroRule rule = new IntroRule(); rule.Tick(h);
            h.Playing = false; h.Free = true; rule.Tick(h); Equal(0, h.Placements); Equal(true, h.Held[4]);
            h.PlacementReady = true; rule.Tick(h); Equal(3, h.Placements); Equal(27f, h.Placed[4].X);
            rule.Tick(h); Equal(3, h.Placements);
        });
        Check("intro invalid ground falls back to nonoverlapping P1 offsets", delegate {
            IntroFake h = new IntroFake(); h.Ground = false;
            IntroRule rule = new IntroRule(); rule.Tick(h); h.Playing = false; h.Free = true; rule.Tick(h);
            Equal(23.5f, h.Placed[2].X); Equal(16.5f, h.Placed[3].X); Equal(27f, h.Placed[4].X);
            Equal(5f, h.Placed[4].Y);
        });
        Check("intro remote waits for host, not guest's skipped prologue", delegate {
            IntroFake h = new IntroFake(); h.Authority = false; h.Playing = false; h.Free = true;
            IntroRule rule = new IntroRule(); rule.Tick(h); Equal(true, h.Held[2]);
            h.Remote = new IntroState { Active = true, Generation = 1 }; rule.Tick(h);
            Equal(true, h.Held[4]); Equal(0, h.Placements);
            h.Remote = new IntroState { Generation = 1, Woke = true, Spots = new[] {
                new StoryPoint(20, 5, 0), new StoryPoint(23.5f, 4, 0), new StoryPoint(16.5f, 4, 0), new StoryPoint(27, 4, 0) } };
            rule.Tick(h); Equal(false, h.Held[2]); Equal(27f, h.Placed[4].X);
            Equal(0, h.GroundChecks); // Host owns terrain decisions on every transport.
        });
        Check("logos ending at title does not relocate a later normal save load", delegate {
            IntroFake h = new IntroFake(); IntroRule rule = new IntroRule(); rule.Tick(h);
            h.Playing = false; h.AtTitle = true; rule.Tick(h); Equal(false, h.Held[2]);
            h.AtTitle = false; h.Free = true; rule.Tick(h); Equal(0, h.Placements);
        });
        Check("ordinary gameplay and later cutscenes do not trigger intro placement", delegate {
            IntroFake h = new IntroFake(); h.Playing = false; h.Free = true;
            IntroRule rule = new IntroRule(); rule.Tick(h); Equal(0, h.Placements); Equal(false, h.Held[2]);
            h.Free = false; rule.Tick(h); Equal(false, h.Held[2]); Equal(0, h.Placements);
        });
        Check("intro new cycle and reset release old holds", delegate {
            IntroFake h = new IntroFake(); IntroRule rule = new IntroRule(); rule.Tick(h);
            h.Playing = false; h.Free = true; rule.Tick(h); Equal(3, h.Placements);
            h.Playing = true; h.Free = false; rule.Tick(h); Equal(true, h.Held[4]);
            h.Playing = false; h.Free = true; rule.Tick(h); Equal(6, h.Placements);
            h.Playing = true; rule.Tick(h); rule.Reset(h); Equal(false, h.Held[4]); Equal(false, rule.Active);
        });
        Check("intro host snapshot wire retains exact grounded layout", delegate {
            IntroFake h = new IntroFake(); IntroRule rule = new IntroRule(); rule.Tick(h);
            h.Playing = false; h.Free = true; rule.Tick(h);
            MemoryStream stream = new MemoryStream(); IntroWire.Write(new BinaryWriter(stream), h.Remote); stream.Position = 0;
            IntroState copy = IntroWire.Read(new BinaryReader(stream));
            Equal(1u, copy.Generation); Equal(false, copy.Active); Equal(true, copy.Woke);
            Equal(27f, copy.Spots[3].X); Equal(4f, copy.Spots[3].Y);
            stream = new MemoryStream(); BinaryWriter w = new BinaryWriter(stream);
            w.Write(1u); w.Write(true); w.Write(true); stream.Position = 0;
            bool rejected = false; try { IntroWire.Read(new BinaryReader(stream)); } catch (InvalidDataException) { rejected = true; }
            Equal(true, rejected);
        });
        // T-105: the host stays "woke" with the spots until it returns to the title. A player who joins (or rejoins) long
        // after the wake was not part of it and must stay where its save puts it, on its own PC and on the host.
        Check("intro late joiner after the wake is neither held nor placed on the host", delegate {
            IntroFake h = new IntroFake(); h.Seats = new[] { 1, 2 };
            IntroRule rule = new IntroRule(); rule.Tick(h); h.Playing = false; h.Free = true; rule.Tick(h);
            Equal(1, h.Placements);
            rule.SeatJoined(3); h.Seats = new[] { 1, 2, 3 }; rule.Tick(h);
            Equal(false, h.Held[3]); Equal(false, h.Placed.ContainsKey(3)); Equal(1, h.Placements);
            rule.SeatLeft(2); rule.SeatJoined(2); rule.Tick(h); Equal(false, h.Held[2]); Equal(1, h.Placements);
        });
        Check("intro late joiner adopts the host's wake without being held or placed", delegate {
            IntroFake host = new IntroFake(); host.Seats = new[] { 1, 2 };
            IntroRule hostRule = new IntroRule(); hostRule.Tick(host); host.Playing = false; host.Free = true; hostRule.Tick(host);
            hostRule.SeatJoined(3); hostRule.Tick(host);
            MemoryStream stream = new MemoryStream(); IntroWire.Write(new BinaryWriter(stream), host.Remote); stream.Position = 0;
            IntroFake guest = new IntroFake(); guest.Authority = false; guest.Playing = false; guest.Free = true; guest.Seats = new[] { 3, 1, 2 };
            guest.Remote = IntroWire.Read(new BinaryReader(stream));
            IntroRule guestRule = new IntroRule(); guestRule.Tick(guest);
            Equal(false, guest.Held[3]); Equal(false, guest.Placed.ContainsKey(3));
            guestRule.Tick(guest); Equal(false, guest.Held[3]);
        });
        Check("intro participant leaving and rejoining after the wake is not placed again", delegate {
            IntroFake h = new IntroFake(); h.Seats = new[] { 1, 2, 3 }; h.PlacementReady = false;
            IntroRule rule = new IntroRule(); rule.Tick(h); h.Playing = false; h.Free = true; rule.Tick(h);
            Equal(0, h.Placements); Equal(true, h.Held[3]);   // seat 3 still restoring the save
            rule.SeatLeft(3); h.Seats = new[] { 1, 2 }; h.PlacementReady = true; rule.Tick(h); Equal(1, h.Placements);
            rule.SeatJoined(3); h.Seats = new[] { 1, 2, 3 }; rule.Tick(h);
            Equal(false, h.Held[3]); Equal(false, h.Placed.ContainsKey(3));
        });
        // T-106: the host dropped out. The guest's session ended (its intro state and the host snapshot were reset) and it
        // keeps retrying; its seat number fell back to 2. With no host to wait for it must stay visible and playable.
        Check("intro guest without a host session is not held", delegate {
            IntroFake h = new IntroFake(); h.Authority = false; h.Playing = false; h.Free = true; h.Seats = new[] { 2 };
            h.HostSession = false; h.Remote = null;
            IntroRule rule = new IntroRule(); rule.Tick(h); Equal(false, h.Held[2]); Equal(0, h.Placements);
            rule.Tick(h); Equal(false, h.Held[2]);
            h.HostSession = true; rule.Reset(h); rule.Tick(h); Equal(true, h.Held[2]);   // back in a session: wait for the host
            h.Remote = new IntroState { Generation = 3 }; rule.Tick(h); Equal(false, h.Held[2]);
            h.HostSession = false; h.Remote = null; rule.Reset(h); rule.Tick(h); Equal(false, h.Held[2]);
        });
        Check("intro empty and invalid seats never place P1 or phantom players", delegate {
            IntroFake h = new IntroFake(); h.Seats = new int[0];
            IntroRule rule = new IntroRule(); rule.Tick(h); h.Playing = false; h.Free = true; rule.Tick(h);
            h.Seats = new[] { 0, 1, 5 }; rule.Tick(h); Equal(0, h.Placements);
        });
    }
}
