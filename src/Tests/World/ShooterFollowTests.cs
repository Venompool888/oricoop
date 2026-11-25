using System;
using OriCoop.World;

// T-250 follow-up (#444, the maintainer's choice): a shooter that waits for its last projectile fires with the host's.
static class ShooterFollowTests
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    public static void Register(Action<string, Action> run)
    {
        run("#444: only a repeating TimedTrigger whose spawner waits for its projectile is followed", delegate {
            Check(ShooterFollow.Followed(true, true), "Ginso shooter: followed");
            Check(!ShooterFollow.Followed(true, false), "Moon Grotto shooter (does not wait): timer phase rule");
            Check(!ShooterFollow.Followed(false, true) && !ShooterFollow.Followed(false, false), "one-shot: neither");
        });
        run("#444: the host never drops its own shot; a guest drops its own only while the host's keep coming", delegate {
            Check(!ShooterFollow.DropsOwnShot(true, false, 1f), "host keeps its own");
            Check(!ShooterFollow.DropsOwnShot(true, false, -1f), "host, none received");
            Check(!ShooterFollow.DropsOwnShot(false, true, 1f), "the host's shot, run by the message");
            Check(ShooterFollow.DropsOwnShot(false, false, 0f), "host's shot just arrived");
            Check(ShooterFollow.DropsOwnShot(false, false, ShooterFollow.Window - 0.1f), "within the window");
            Check(!ShooterFollow.DropsOwnShot(false, false, ShooterFollow.Window), "host quiet for the window: own again");
            Check(!ShooterFollow.DropsOwnShot(false, false, -1f), "none received yet: own (host elsewhere)");
        });
        run("#444: the window outlasts two of the Ginso shooter's shots (5.3-6.1 s apart)", delegate {
            Check(ShooterFollow.Window > 2f * 6.1f, "window " + ShooterFollow.Window);
        });
        run("#444: a guest that follows fires exactly when the host does (simulated 60 s, lossy link)", delegate {
            // Host: fires every 3 s of running + its projectile's life (2.8 s). Guest: own timer 1.5 s off, life 1.4-3 s.
            const float dt = 1f / 60f, latency = 0.08f;
            float hostTimer = 3f, hostPause = 0f, guestTimer = 1.5f, guestPause = 0f, lastHostShot = -1f, arriveAt = -1f;
            int hostShots = 0, guestShots = 0, dropped = 0, lost = 0; float worst = 0f, lastHostFire = -1f;
            System.Random rnd = new System.Random(444);
            for (int f = 0; f < 3600; f++)
            {
                float now = f * dt;
                if (hostPause > 0f) hostPause -= dt; else { hostTimer -= dt; if (hostTimer <= 0f) {
                    hostTimer = 3f; hostPause = 2.8f; hostShots++; lastHostFire = now;
                    if (hostShots % 5 == 0) lost++; else arriveAt = now + latency; } }
                if (arriveAt >= 0f && now >= arriveAt)
                {
                    arriveAt = -1f; lastHostShot = now;
                    if (!ShooterFollow.DropsOwnShot(false, true, 0f)) { guestShots++; guestPause = 1.4f + (float)rnd.NextDouble() * 1.6f;
                        worst = Math.Max(worst, now - lastHostFire); }
                }
                if (guestPause > 0f) guestPause -= dt; else { guestTimer -= dt; if (guestTimer <= 0f) {
                    guestTimer = 3f;
                    float since = lastHostShot < 0f ? -1f : now - lastHostShot;
                    if (ShooterFollow.DropsOwnShot(false, false, since)) dropped++;
                    else { guestShots++; guestPause = 1.4f + (float)rnd.NextDouble() * 1.6f; } } }
            }
            Check(hostShots >= 9, "host shots " + hostShots);
            Check(worst <= latency + dt + 0.001f, "a followed shot came " + worst + " s after the host's");
            Check(guestShots <= hostShots - lost + 1, "guest " + guestShots + " shots, host " + hostShots + " (lost " + lost + "): own timer fired in between");
            Check(dropped >= 1, "guest never dropped its own shot");
        });
    }
}
