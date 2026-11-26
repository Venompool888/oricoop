namespace OriCoop.World
{
    // T-250 (#338) follow-up, the maintainer's choice (2026-11-25, after #444's first test): a shooter that waits for its
    // last projectile to be destroyed (ProjectileSpawner.WaitForProjectileToBeDestroyed, the Ginso Tree puzzle shooter)
    // fires when the host's fires. Its repeating TimedTrigger is paused while the projectile lives, and a projectile
    // lives a different time on each PC (it hits that PC's own Ori), so keeping the timer's phase in step (choice B,
    // WorldSync / TimerPhase) drifts apart again every shot (#444: 2-3 s). Instead the host sends each shot (message 75,
    // WorldObjSync kind 7) and a guest runs the same shot on its copy; its own timer's shots are dropped while the host's
    // keep coming. When none came for Window seconds (the host is elsewhere, or left the scene) the guest's shooter runs
    // on its own again. Every other shooter keeps the timer phase rule. Unity-free (unit tests:
    // src\Tests\World\ShooterFollowTests.cs); the game side is in WorldObjSync.
    public static class ShooterFollow
    {
        // Longer than two of the host's shots (the Ginso shooter fires every 5.3-6.1 s), so one lost message does not
        // let the guest's own timer fire in between.
        public const float Window = 15f;

        // Whether a shooter is followed: a repeating TimedTrigger whose spawner waits for its last projectile.
        public static bool Followed(bool repeat, bool spawnerWaits) { return repeat && spawnerWaits; }

        // Whether this PC drops a shot its own timer started. sinceHostShot: seconds since the host's last shot for this
        // shooter arrived, below 0 when none has.
        // - The host never drops its own: it sets the pace.
        // - applying: the shot is the host's, run by the message: it runs.
        // - A guest drops its own only while the host's keep coming.
        public static bool DropsOwnShot(bool isHost, bool applying, float sinceHostShot)
        {
            if (isHost || applying) return false;
            return sinceHostShot >= 0f && sinceHostShot < Window;
        }
    }
}
