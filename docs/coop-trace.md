# Co-op trace: live, structured logs (playtest P6, bug #17)

## Files
- `BepInEx\OriCoop-trace-<yyyyMMdd-HHmmss>-<pid>.log`, one per game process, next to `LogOutput.log`.
  Every line goes to the OS as soon as it is written (`AutoFlush`), so the file survives a crash or a force quit.
  The newest 12 files are kept (`[Trace] KeepFiles`). Above `[Trace] MaxFileMB` (64) only level-1 events are written;
  at twice that the file stops.
- `BepInEx\LogOutput.log` (BepInEx's own): BepInEx flushes it on a 2 s thread-pool timer; the mod now also flushes it
  every 0.5 s from the game loop.
  Note: Windows Explorer can show "0 KB" for a file that is still open even when it holds data. Open the file, or use
  `Get-Content`, to see what is really there.
  **UTC stamps (T-067):** every line this mod writes there starts with its UTC time, e.g.
  `[Info   :Ori Local Co-op] 2026-11-18T10:44:35.123Z net: ...` (BepInEx / HarmonyX / Unity lines are unchanged).
  At start: `clock: utc_offset=+09:00 local=... utc=...Z`. After a partner connects, both ends measure the wall-clock
  difference (message 0x52 ClockSync, NTP style, best of 8 round trips, again every 5 min) and log
  `clock: offset vs P2 = +12.3 ms (P2 clock minus ours; rtt 114.0 ms, 8 samples)`, also as trace event
  `net clock.offset seat= offset_ms= rtt_ms= n=`. A recording made on either PC can then be placed on UTC: note that
  PC's `utc_offset`, and the other PC's error from the offset line. Accuracy is about rtt/2 plus one frame per side.
- (The relay server and its players log were removed in T-159.)

## Line format
```
# OriCoop trace format=1 mod=0.2.0 proto=0.2.0-net2 build=<dll mtime>/<md5 prefix> pid=... started_local=... started_utc=... utc_offset_min=60
00:41:02.123 2026-11-15T14:41:02.123Z f=1234 ff=2000 t=56.78 sid=1a2b3c4d role=P1 enemy death id=Frog#123 hp=0 msg="text with spaces"
```
local time, UTC (ms), `f` frame, `ff` fixed-update count, `t` game time (s), `sid` session id, `role`, subsystem,
event, then `key=value` fields (values with spaces are quoted with `\"` escapes).
- `sid` is the NetPeer handshake session id: the same value on both ends of one connection, `-` when offline.
  `net session.start` (transport, host?, versions, scene; no relay field since T-159) and `net session.end` mark it.
- `role` = `P1`..`P4` (`Roles.Mine`), or `local` when not online.
- IPv4 addresses (except 127.x) are masked as `ip#NNNN`, never the address.
- Every BepInEx log line (mod, BepInEx, HarmonyX) is mirrored as `log <level> src=... msg=...`, so old messages get the
  same stamps. Unity errors/exceptions become `log unity.exception msg=... at=<top stack frame>`.

## Levels (BepInEx config `local.oricoop.cfg`, section `[Trace]`)
- `Level` = 0 off, **1 (default)** events (state changes), 2 + periodic samples, 3 + verbose. Before #423 the default was 2;
  a config still holding that old 2 is moved to 1 once (`DefaultLowered`). Set 2 again to get the periodic samples.
- `SubsystemLevels` = e.g. `phys=3,enemy=1`. `SampleFrames` = 30 (samples every N frames).
- Env overrides for tests: `ORICOOP_TRACE_LEVEL`, `ORICOOP_TRACE_DIR`.

## API (`src\OriCoop\CoopTrace.cs`) for other subsystems
```csharp
CoopTrace.Event("enemy", "death", "id", id, "owner", owner, "pos", pos);   // level 1: something changed
if (CoopTrace.SampleDue("phys")) CoopTrace.Sample("phys", "obj", ...);    // level 2: periodic
CoopTrace.Verb("net", "packet", ...);                                     // level 3
if (CoopTrace.Changed("cam.lock", locked.ToString())) CoopTrace.Event("cam", locked ? "lock" : "unlock");
if (CoopTrace.Every("enemy.warn." + id, 1f)) ...                          // rate limit (real seconds)
if (CoopTrace.On("phys", CoopTrace.Samples)) { /* build expensive fields only then */ }
```
Fields are name/value pairs; `Vector3`, `float`, `bool`, Unity objects (`name#instanceId`) are formatted for you.
Thread-safe (stamps come from a clock cached on the main thread). Subsystems in use:
`player phys enemy story cam hud save scene net audio log`.
**When you fix a bug, emit the events that would have explained it** (e.g. enemy-sync: `enemy owner.change`,
`enemy snapshot.apply`; world-physics: `phys owner.change`, `phys push.start who=`; story-cutscene: `story skip
reason=`). Use the object's net id in `id=` on both ends, so the merge tool can pair them.

## Built-in hooks (`src\OriCoop\CoopTraceHooks.cs`, Harmony, observe only)
Patched one by one; a missing target is logged once (`trace hooks: could not patch ...`) and skipped.
- player: `damage` (amount in/out, source, hp before/after, difficulty), `ability`, ground/air, death, revive;
  sample every 30 frames (max 4/s) per local Ori and puppet: who, pos, vel, grounded, state, animation, hp, spirit.
- phys: push/pull start/end (who, block), stand on/leave, ground contact collider; every ~0.5 s the 12 nearest push
  blocks / ropes / rigidbodies / moving platforms within 15 units (pos, vel, kinematic, layer, layer-collision with
  the puppet, pusher, standing player), plus the players' own colliders.
- enemy: spawn (awake / pool), damage (amount, attacker, hp, death action), death, disable, disappear, destroy;
  sample of enemies within 20 units (pos, vel, state machine state, animation, kinematic/sleeping, hp, owner if public).
- story / cam / hud: action sequence request/start/end, ability cutscene start/end, cutscene state changes, ability
  pedestal, camera lock/unlock and scripted takeover/release, HUD show/hide (alpha, letterbox), dialog/subtitle
  show/hide, Sein spirit follow target.
- phys, T-250 probe (`src\OriCoop\ShooterProbe.cs`): level mechanisms that shoot (non-story sequences with a
  `SpawnProjectileAction`, or named `shootProjectile`). First 4 starts per shooter per scene visit, 300 per process:
  `shooter.start` (id, scene, path, guid, actions, caller = the game frames that started it, ori_dist),
  `shooter.action` / `shooter.holder` / `shooter.projectile` (fields read by reflection, once each), `shooter.holders`
  (count), `shooter.spawn` (each projectile's pos, vel, gravity, life, angle to own Ori / to the partner).
- world, T-250 timer phase (`src\OriCoop\WorldSync.cs`, rules in `World\TimerPhase.cs`): the host sends the phase of
  every `OnTimerPerformAction` / `TimedTrigger` in its loaded scenes every 0.7-1.3 s (world message 12). A guest takes it
  only when its own is more than 0.25 s off and neither side is within 0.25 s of a restart: `timer.phase` (key, type,
  path, local, host, verdict), first 200 per process. The 5 s `net: world bytes/sec` log line adds
  `timers sent= taken= in_step= near_restart=`.
- save / scene: save, load, pause/resume, difficulty (with the enemy health multiplier actually in effect),
  scene load/unload, current scene.
- net: every 1 s RTT, loss, reliable pending/resends, remote scene; connect/disconnect with reason.
  Steam (T-253): on every disconnect `net steam.handshake reason= host= step=`, where `step` is how far the Steam side
  got (e.g. `session request held: the guest is not in this PC's lobby member list yet`, `session accepted, no packet
  received from the guest`, `packets sent to the host, nothing received from it`, `packets both ways`), and the log line
  `net: Steam: link at disconnect:` with the counts. The handshake itself logs `net: peer: connect request (hello) sent`,
  `accept (hello answer) received ... after N ms` or `no answer after 10 s: N connect request(s) sent, M packet(s)
  received`; the Steam side logs `P2P session request from`, `held` / `accepted` / `refused`, and the first packet sent /
  received with its channel.

- audio, T-252 (`src\OriCoop\CoopTraceAudio.cs`, rules in `Diagnostics\AudioWatch.cs`), observe only, checked once a
  second. Level 1: `watch.init` (which game volume settings were found), `listener` (which AudioListener, enabled,
  how many, its distance `to_view` from the camera's target), `pause` / `volume` (AudioListener), `settings` (the
  game's own volume settings), `focus`, `device.changed` (Windows changed the output device or format), and
  `snapshot why=` at spectate start / end / another teammate, own player down / up, a new scene and a device change.
  **`audio lost.suspect reasons=`** when the sound looks lost, with the full state (listener and its position, pause,
  volumes, focus, time scale, scene, camera target, playing / quiet / out-of-range sources, the 4 playing sounds
  nearest to the view as `name@dist/volume/maxDistance/rolloff`, every player's position and downed state); the same
  moment as a warning `audio: sound may be lost (...)` in `LogOutput.log`. Then `lost.change`, `lost.still` (every
  30 s) and `lost.clear for_s=`. Reasons: `no_listener`, `many_listeners`, `listener_paused`, `listener_volume`,
  `settings_zero`, `listener_off_view` (listener more than 15 units from what the camera shows),
  `listener_far_from_watched` (while down, from the teammate you watch), `view_sounds_quiet` (2+ sounds next to the
  view play but none can be heard), `silent` (nothing plays for 5 s in a running level), `dsp_stalled` (the audio
  clock stopped). Also `heartbeat` every 120 s. Level 2: `sample` (the full state) every 5 s. At most 40 audio lines a
  minute (`budget.dropped lines=`), 100 suspect episodes per process.

## Merge tool: `tools\merge-coop-logs.py`
```
python -I tools\merge-coop-logs.py P1.log@P1 P2.log@P2                 # one timeline (local time; --utc for UTC)
python -I tools\merge-coop-logs.py a.log b.log --subsys enemy,phys --from 00:41:00 --to 00:43:30
python -I tools\merge-coop-logs.py a.log b.log --diff --window 2        # ONLY P1 / ONLY P2 / DELTA per (subsys,event,id)
python -I tools\merge-coop-logs.py a.log b.log --summary                # format, line count, time range, sid, subsystems
python -I tools\merge-coop-logs.py --selftest
```
Also `--grep REGEX`, `--sid`, `--role P1,P2`. Works on both trace files and old `LogOutput.log` files. Old logs have
no time: lines are placed by line order, aligned across the two ends on shared events (connect, version handshake,
checkpoint send/receive, partner appears, enemy GUIDs); estimated times carry `~`. Pin them with
`--anchor FILE:LINE=HH:MM:SS` or `--start FILE=HH:MM:SS` / `--end FILE=HH:MM:SS` (`--date`, `--utc-offset`).
All output masks IPv4 addresses.
LogOutput.log files with UTC stamps (format `stamped`) are placed by their own stamps; unstamped lines between two
stamped ones are interpolated (`~`). Times are then moved onto P1's clock with the measured `clock: offset vs ...`
line of either end (`--summary` shows `utc_offset=` and `clock_fix=` per file; `--no-clock-fix` turns it off).

## Test
`src\run-tracetest.ps1` (single client): reads the trace and `LogOutput.log` **while the game runs** and requires the
header, the mirrored "loaded" line, a scene line, P1 player samples, the newest line less than 2 s old, and no IP.
`-Net` runs `run-nettest.ps1` with the trace redirected and requires both ends to share one session id, and each end's
own `LogOutput.log` to have every OriCoop line UTC stamped, one `clock: utc_offset=` header and a `clock: offset vs`
line. Unit tests for the formats and the offset math: `src
un-unittests.ps1 -Only Clock`.
