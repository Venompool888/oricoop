# Nightly full regression (layer 3 of docs\testing-rules.md): every game suite in normal windowed mode, fail-fast OFF
# (full diagnostics), DLL built from a clean worktree of HEAD, suites spread over the test copies by testcopy*.lock.
# Exits at once (code 0, "skipped") if the owner's game is running; a suite that sees the owner start playing stops
# itself (exit 3) and is reported as STOPPED. One summary file: C:\OriCoop\logs\nightly\<yyyyMMdd-HHmm>.txt.
# Do not schedule it on a PC where someone plays: it starts real game clients.
param([int]$Parallel = 5, [switch]$NoMutations)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path -Parent $src
. (Join-Path $src 'test-launch.ps1')
$owner = @(Get-OwnerGames)
if ($owner.Count -gt 0) { 'NIGHTLY skipped: the owner is playing (' + (Format-Games $owner) + ')'; exit 0 }
# Logs live in the main checkout (shared by every worktree), like run-affected's history.
$logs = Join-Path (Split-Path -Parent (git -C $root rev-parse --path-format=absolute --git-common-dir).Trim()) 'logs'
$dir = Join-Path $logs 'nightly'
New-Item -ItemType Directory -Force $dir | Out-Null
$prevNightly = Get-ChildItem $dir -Filter '*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$summary = Join-Path $dir ([DateTime]::Now.ToString('yyyyMMdd-HHmm') + '.txt')
$sha = (git -C $root rev-parse --short HEAD).Trim()
$env:ORICOOP_SESSION = 'nightly ' + $sha
$env:ORICOOP_FAIL_FAST = '0'
$env:ORICOOP_TIMESCALE = ''
$env:ORICOOP_TEST_COPY = ''
$env:ORICOOP_TEST_DLL = (& (Join-Path $src 'build-commit.ps1') $sha | Select-Object -Last 1)
& (Join-Path $src 'run-affected.ps1') -All -Parallel $Parallel -Summary $summary
$code = $LASTEXITCODE
# T-078: the selection selfcheck, and what the merge-time runs since the last nightly actually ran (they may have run
# only some net phases; this full run is the safety net for anything the phase map got wrong).
$selfcheck = @(& (Join-Path $src 'test-selection-selfcheck.ps1') -Root $root | Select-Object -Last 1)
if ($LASTEXITCODE -ne 0 -and $code -eq 0) { $code = 1 }
$since = $(if ($prevNightly) { $prevNightly.LastWriteTime } else { [DateTime]::Now.AddDays(-1) })
$hist = Join-Path $logs 'affected-history.tsv'
$runs = @()
if (Test-Path $hist) {
    foreach ($l in Get-Content $hist) {
        $c = $l -split "`t"; $when = [DateTime]::MinValue
        if ($c.Count -ge 7 -and [DateTime]::TryParse($c[0], [ref]$when) -and $when -gt $since) {
            $runs += ('  ' + $c[0] + ' ' + $c[1] + ' ' + $c[2] + ': suites ' + $(if ($c[3]) { $c[3] } else { '-' }) + '; net phases ' + $c[4] + '; ' + $c[5] + ' in ' + $c[6] + ' s')
        }
    }
}
if (Test-Path $summary) {
    Add-Content -LiteralPath $summary -Value (@($selfcheck) + @('--- merge-time runs since the last nightly (' + $since.ToString('s') + '): ' + $runs.Count + ' (each ran only the suites / net phases listed)') + $runs)
}

# Mutation checks: every test-only "break the fix" switch must make ITS OWN check fail (Expect: a regex on the failing
# check's name). Fail-fast is OFF here: with it on, any baseline failure earlier in the suite (e.g. E3) stopped the run
# before the mutated check and was counted as "caught". Verdicts: caught (an Expect check failed), MISSED (it passed),
# BLOCKED (the suite died or never reached it; other failures listed), ERROR (the script failed, see <log>.err).
# A MISSED mutation makes the nightly fail. MUTATE_KEEP_MODAL runs leave mode "host" (T-072): with "drop" the guest picks 回到标题, which closes the dialog by itself, so the auto-close this switch turns off never ran. ORICOOP_NET_ONLY (P3 = CUT, WO) skips the earlier net phases.
$Mutations = @(
    @{ Name = 'MUTATE_MAP_PROJECTION'; Env = @{ ORICOOP_MUTATE_MAP_PROJECTION = '1'; ORICOOP_PHASES = 'S4' }; Script = @('run-nettest.ps1'); Expect = '^S4 map: P2 has a marker at its native (area|world) map projection' }
    @{ Name = 'MUTATE_NO_LIFECYCLE';    Env = @{ ORICOOP_MUTATE_NO_LIFECYCLE = '1' };    Script = @('run-test.ps1', '-Scenarios', 'title');    Expect = '^title: ' }
    @{ Name = 'MUTATE_KEEP_NET';        Env = @{ ORICOOP_MUTATE_KEEP_NET = '1' };        Script = @('run-test.ps1', '-Scenarios', 'title');    Expect = '^title: ' }
    @{ Name = 'MUTATE_NO_PLATFORMFIX';  Env = @{ ORICOOP_MUTATE_NO_PLATFORMFIX = '1' };  Script = @('run-test.ps1', '-Scenarios', 'platform'); Expect = '^platform: ' }
    @{ Name = 'MUTATE_BOTTOMHUD_P1VALUES'; Env = @{ ORICOOP_MUTATE_BOTTOMHUD_P1VALUES = '1' }; Script = @('run-test.ps1', '-Scenarios', 'bottomhud'); Expect = '^bottomhud: ' }
    @{ Name = 'MUTATE_SIGNPOST';        Env = @{ ORICOOP_MUTATE_SIGNPOST = '1'; ORICOOP_FORCE_ZH = '1' }; Script = @('run-test.ps1', '-Scenarios', 'signpost'); Expect = '^signpost: ' }
    @{ Name = 'MUTATE_NO_COMPANION_RETRY'; Env = @{ ORICOOP_MUTATE_NO_COMPANION_RETRY = '1' }; Script = @('run-test.ps1', '-Scenarios', 'companion'); Expect = '^companion: a return that threw' }   # T-213
    @{ Name = 'MUTATE_NO_POST_SESSION_FADE'; Env = @{ ORICOOP_MUTATE_NO_POST_SESSION_FADE = '1' }; Script = @('run-test.ps1', '-Scenarios', 'afterblack'); Expect = '^afterblack: a screen left black' }   # T-212 (1)
    @{ Name = 'MUTATE_KEEP_MODAL';      Env = @{ ORICOOP_MUTATE_KEEP_MODAL = '1'; ORICOOP_TITLEJOIN_LEAVE = 'host' }; Script = @('run-titlejoin.ps1'); Expect = 'no disconnect dialog left on the title|doesn''t restart the game' }
    @{ Name = 'MUTATE_HIDE_ICONS';      Env = @{ ORICOOP_MUTATE_HIDE_ICONS = '1' }; Script = @('run-titlejoin.ps1', '-Transport', 'cserver'); Expect = "shows the game's own button icon" }   # T-173
    @{ Name = 'MUTATE_ORB_MENUS';       Env = @{ ORICOOP_MUTATE_ORB_MENUS = '1' }; Script = @('run-titlejoin.ps1', '-Transport', 'cserver'); Expect = 'room page orb shows only the save' }   # T-173
    @{ Name = 'MUTATE_PAUSE_ROOM_ANY';  Env = @{ ORICOOP_MUTATE_PAUSE_ROOM_ANY = '1' }; Script = @('run-titlejoin.ps1'); Expect = '^(host|join|guest): LAN: the pause menu has no room entry' }   # T-189
    @{ Name = 'MUTATE_NO_SHARED_PROLOGUE'; Env = @{ ORICOOP_MUTATE_NO_SHARED_PROLOGUE = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'skip'); Expect = "^guest: plays the host's prologue along" }   # T-203
    @{ Name = 'MUTATE_PROLOGUE_GUEST_CONTROL'; Env = @{ ORICOOP_MUTATE_PROLOGUE_GUEST_CONTROL = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'skip'); Expect = '^guest: its own right \+ jump keys do not move Naru' }   # T-203
    @{ Name = 'MUTATE_PROLOGUE_NO_CATCHUP'; Env = @{ ORICOOP_MUTATE_PROLOGUE_NO_CATCHUP = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'late'); Expect = '^late guest \(cinematic\): (catches up with the host|shows the same moment)' }   # T-205
    @{ Name = 'MUTATE_PROLOGUE_OWN_GATES'; Env = @{ ORICOOP_MUTATE_PROLOGUE_OWN_GATES = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'skip'); Expect = "^guest: a button of this PC is held .* does not answer the story's" }   # T-205
    @{ Name = 'MUTATE_PROLOGUE_REPLAY_BLIND'; Env = @{ ORICOOP_MUTATE_PROLOGUE_REPLAY_BLIND = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'skip'); Expect = "^guest: a trigger of the host's log counts as done only when the game took it" }   # T-205 review
    @{ Name = 'MUTATE_PROLOGUE_MENU_UNDER_COVER'; Env = @{ ORICOOP_MUTATE_PROLOGUE_MENU_UNDER_COVER = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'late'); Expect = '^late guest \(cinematic\): the pause key does nothing while the cover is up' }   # T-205 review
    @{ Name = 'MUTATE_PROLOGUE_INTRO_HOLD'; Env = @{ ORICOOP_MUTATE_PROLOGUE_INTRO_HOLD = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'deep'); Expect = "^deep guest: (shows the same moment as the host|the story's character is on screen)" }   # T-205
    @{ Name = 'MUTATE_PROLOGUE_ANY_DEVICE'; Env = @{ ORICOOP_MUTATE_PROLOGUE_ANY_DEVICE = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'skip'); Expect = '^host: P1 is on a controller .* does not move Naru' }   # T-209 (1)
    @{ Name = 'MUTATE_PROLOGUE_GUEST_STAYS_ONLINE'; Env = @{ ORICOOP_MUTATE_PROLOGUE_GUEST_STAYS_ONLINE = '1' }; Script = @('run-titlejoin.ps1', '-Prologue', 'hostquit', '-GuestSlot'); Expect = '^guest after the host left its prologue: the online session ended at the title' }   # T-209 (2)
    @{ Name = 'MUTATE_NO_COOP_FOLDER';  Env = @{ ORICOOP_MUTATE_NO_COOP_FOLDER = '1' }; Script = @('run-titlejoin.ps1'); Expect = 'inside OriCoop-coop|single-player save' }   # T-043
    @{ Name = 'MUTATE_ROOMS_NATIVE_OFF'; Env = @{ ORICOOP_MUTATE_ROOMS_NATIVE_OFF = '1' }; Script = @('run-rooms.ps1'); Expect = '^fresh \S+ menu is exactly' }   # T-173 rewrote run-rooms: check 7 (the fresh my-rooms menu rows) is the first to see the page's rows
    @{ Name = 'MUTATE_ROOM_NAME_HASH';  Env = @{ ORICOOP_MUTATE_ROOM_NAME_HASH = '1' };  Script = @('run-rooms.ps1'); Expect = '^room name (is two English|has no MessageBox markup)' }   # T-173's create flow: run-rooms checks 18 / 19
    @{ Name = 'MUTATE_TOAST_NO_FALLBACK'; Env = @{ ORICOOP_MUTATE_TOAST_NO_FALLBACK = '1' }; Script = @('run-test.ps1', '-Scenarios', 'move'); Expect = '^toast: English falls back' }
    @{ Name = 'MUTATE_MENU_STALE_LOCK (S4)'; Env = @{ ORICOOP_MUTATE_MENU_STALE_LOCK = '1'; ORICOOP_PHASES = 'S4' }; Script = @('run-nettest.ps1'); Expect = '^S4 F8 online: a story lock' }
    @{ Name = 'MUTATE_OVERLAY_UNLOCK';  Env = @{ ORICOOP_MUTATE_OVERLAY_UNLOCK = '1' };  Script = @('run-test.ps1'); Expect = '^title menu: overlay lobby: (starting|ending) online play keeps' }
    @{ Name = 'MUTATE_REMOVE_GLOW';     Env = @{ ORICOOP_MUTATE_REMOVE_GLOW = '1' };     Script = @('run-test.ps1'); Expect = '^title menu: remove page glow is red' }
    @{ Name = 'MUTATE_FONT_LEAK';       Env = @{ ORICOOP_MUTATE_FONT_LEAK = '1' };       Script = @('run-test.ps1'); Expect = '^title menu: menu resizes and recreated HUD text leave no extra fonts' }
    @{ Name = 'MUTATE_MODE_FIXED_SIZE'; Env = @{ ORICOOP_MUTATE_MODE_FIXED_SIZE = '1' }; Script = @('run-test.ps1'); Expect = '^title menu: how-to-play chooser scales' }
    @{ Name = 'TEST_THROW';             Env = @{ ORICOOP_TEST_THROW = 'mutation' };      Script = @('run-test.ps1', '-Scenarios', 'move');     Expect = '^move: |TestThrow' }
    @{ Name = 'P3_OFF (CUT)';           Env = @{ ORICOOP_P3_OFF = '1'; ORICOOP_NET_ONLY = 'P3' }; Script = @('run-nettest.ps1');               Expect = '^CUT ' }
    @{ Name = 'MUTATE_GATE_KEEP_FIRED_ON_RESTORE (CUT reload)'; Env = @{ ORICOOP_MUTATE_GATE_KEEP_FIRED_ON_RESTORE = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^CUT reload: a gate that fired before the reload' }   # T-212 (6)
    @{ Name = 'MUTATE_END_LOST_IN_WINDOW (CUT)'; Env = @{ ORICOOP_MUTATE_END_LOST_IN_WINDOW = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^LEFT 4 the story is over on this PC' }   # T-212 (4)
    @{ Name = 'MUTATE_NO_LATE_START (CUT)'; Env = @{ ORICOOP_MUTATE_NO_LATE_START = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^LEFT 5 a short story whose end arrived' }   # T-212 (5)
    @{ Name = 'MUTATE_KEEP_DROPPED_MIRROR (CUT)'; Env = @{ ORICOOP_MUTATE_KEEP_DROPPED_MIRROR = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^LEFT 3 the dropped teammate' }   # T-212 (3)
    @{ Name = 'MUTATE_KEEP_PLAYED_ON_LOAD (CUT reload)'; Env = @{ ORICOOP_MUTATE_KEEP_PLAYED_ON_LOAD = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^CUT reload: a pedestal cutscene played before the reload' }   # T-212 (7)
    @{ Name = 'MUTATE_WINDOW_DROPS_STARTS (CUT)'; Env = @{ ORICOOP_MUTATE_WINDOW_DROPS_STARTS = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^LEFT 2 a partner''s unrelated story' }   # T-207 (2)
    @{ Name = 'MUTATE_LEVER_DROP_HOLD (CUT lever)'; Env = @{ ORICOOP_MUTATE_LEVER_DROP_HOLD = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^T-194 guest pulls: the player who pulled walks away from the lever' }   # T-194 (the real lever grab closes CUT)
    @{ Name = 'MUTATE_STONE_RAW_APPLY (CUT map stone)'; Env = @{ ORICOOP_MUTATE_STONE_RAW_APPLY = '1'; ORICOOP_PHASES = 'CUT' }; Script = @('run-nettest.ps1'); Expect = '^T-204 guest activates, story stays local: spirit flame is not locked for the partner' }   # T-204
    @{ Name = 'MUTATE_MAP_DOWNGRADE (W map)'; Env = @{ ORICOOP_MUTATE_MAP_DOWNGRADE = '1'; ORICOOP_PHASES = 'W' }; Script = @('run-nettest.ps1'); Expect = '^W map: a cell explored while a full snapshot was on its way is still explored' }   # T-214
    @{ Name = 'MUTATE_PICKUP_GUID_ONLY (W pickups)'; Env = @{ ORICOOP_MUTATE_PICKUP_GUID_ONLY = '1'; ORICOOP_PHASES = 'W' }; Script = @('run-nettest.ps1'); Expect = '^W pickups: two copies of one prefab \(same GUID, two spots\) both count' }   # T-214
    @{ Name = 'MUTATE_CELL_REFILLS_HOST (W cells)'; Env = @{ ORICOOP_MUTATE_CELL_REFILLS_HOST = '1'; ORICOOP_PHASES = 'W' }; Script = @('run-nettest.ps1'); Expect = '^W cells: the guest is refilled by the cells it took' }   # T-214
    @{ Name = 'S4_NO_HOLD (S4 skew)';   Env = @{ ORICOOP_S4_NO_HOLD = '1'; ORICOOP_S4_SKEW = '0.3' }; Script = @('run-nettest.ps1');          Expect = '^S4 ' }
    @{ Name = 'PHYS_OFF (P)';           Env = @{ ORICOOP_PHYS_OFF = '1' };               Script = @('run-nettest.ps1');                        Expect = '^P\w* ' }
    @{ Name = 'MUTATE_NO_FACING (FACE)'; Env = @{ ORICOOP_MUTATE_NO_FACING = '1'; ORICOOP_PHASES = 'MOVE' }; Script = @('run-nettest.ps1'); Expect = '^FACE after each wall jump' }   # T-199
    @{ Name = 'MUTATE_STANDIN_SLIDES (MOVE WALL)'; Env = @{ ORICOOP_MUTATE_STANDIN_SLIDES = '1'; ORICOOP_PHASES = 'MOVE' }; Script = @('run-nettest.ps1'); Expect = '^WALL a stand-in pull through the floor' }   # T-251
    @{ Name = 'MUTATE_FAIL_KEEPS_REMOTES (G)'; Env = @{ ORICOOP_MUTATE_FAIL_KEEPS_REMOTES = '1'; ORICOOP_PHASES = 'MOVE' }; Script = @('run-nettest.ps1'); Expect = '^G a failed transport (removes|ends)' }   # T-213
    @{ Name = 'MUTATE_NO_STREAM_RESTART (G)'; Env = @{ ORICOOP_MUTATE_NO_STREAM_RESTART = '1'; ORICOOP_PHASES = 'MOVE' }; Script = @('run-nettest.ps1'); Expect = '^G a stale frame far ahead' }   # T-213
    @{ Name = 'MUTATE_PUPPET_STONE_AIR (PH)'; Env = @{ ORICOOP_MUTATE_PUPPET_STONE_AIR = '1'; ORICOOP_PHASES = 'P' }; Script = @('run-nettest.ps1'); Expect = '^PH the partner (standing|riding)' }   # T-198
    @{ Name = 'MUTATE_STONE_DROP_HELPER (PE)'; Env = @{ ORICOOP_MUTATE_STONE_DROP_HELPER = '1'; ORICOOP_PHASES = 'P' }; Script = @('run-nettest.ps1'); Expect = '^PE ' }   # T-197
    @{ Name = 'MUTATE_STONE_STALE_STATE (PF)'; Env = @{ ORICOOP_MUTATE_STONE_STALE_STATE = '1'; ORICOOP_PHASES = 'P' }; Script = @('run-nettest.ps1'); Expect = '^PF (push \d|a session reset)' }   # T-197
    @{ Name = 'MUTATE_STONE_KEEP_DOWNED_GRAB (PG)'; Env = @{ ORICOOP_MUTATE_STONE_KEEP_DOWNED_GRAB = '1'; ORICOOP_PHASES = 'P' }; Script = @('run-nettest.ps1'); Expect = '^PG (a downed player|the downed partner)' }   # T-197
    @{ Name = 'WORLDOBJ_OFF (WO)';      Env = @{ ORICOOP_WORLDOBJ_OFF = '1'; ORICOOP_NET_ONLY = 'WO' }; Script = @('run-nettest.ps1');        Expect = '^WO' }
    @{ Name = 'WOBJ_NO_DEFER (WO5D)';   Env = @{ ORICOOP_WOBJ_NO_DEFER = '1'; ORICOOP_NET_ONLY = 'WO' }; Script = @('run-nettest.ps1');       Expect = '^WO5D ' }
    @{ Name = 'MUTATE_WOBJ_KEEP_FIRED (R3)'; Env = @{ ORICOOP_MUTATE_WOBJ_KEEP_FIRED = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R3 the reload forgets one-shot' }   # T-144
    @{ Name = 'MUTATE_NO_SAVEOFFER (R save)'; Env = @{ ORICOOP_MUTATE_NO_SAVEOFFER = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R save' }   # T-043
    @{ Name = 'MUTATE_NO_SEAT_SKILLS (R)'; Env = @{ ORICOOP_MUTATE_NO_SEAT_SKILLS = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R skills|4P skills' }   # T-043
    @{ Name = 'MUTATE_NO_DEATH_SAVE (R5)'; Env = @{ ORICOOP_MUTATE_NO_DEATH_SAVE = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R5 host: soul link with the guest down costs half' }   # T-041
    @{ Name = 'MUTATE_SELF_REVIVE_IN_PLACE (R5)'; Env = @{ ORICOOP_MUTATE_SELF_REVIVE_IN_PLACE = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R5 guest: (back at the soul link|health equals)' }   # T-041
    @{ Name = 'MUTATE_LINK_HOST_ONLY (R5b)'; Env = @{ ORICOOP_MUTATE_LINK_HOST_ONLY = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R5b host: the guest''s offered save point' }   # T-041
    @{ Name = 'MUTATE_LINK_HOST_ONLY (4P)'; Env = @{ ORICOOP_MUTATE_LINK_HOST_ONLY = '1' }; Script = @('run-nettest.ps1', '-Clients', '4'); Expect = '^4P P\d learns that P4''s offered save point' }   # T-041
    @{ Name = 'MUTATE_NO_CHECKPOINT_POINT (R6)'; Env = @{ ORICOOP_MUTATE_NO_CHECKPOINT_POINT = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R6 (host: (the room had a save point|with no room save)|guest: (told the room|holding Y revives))' }   # T-202
    @{ Name = 'MUTATE_REVIVE_NEEDS_RESCUER (R7b)'; Env = @{ ORICOOP_MUTATE_REVIVE_NEEDS_RESCUER = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R7b guest: revived although the rescuer''s Ori is gone' }   # T-215 #1
    @{ Name = 'MUTATE_WIPE_NO_GRACE (R7e)'; Env = @{ ORICOOP_MUTATE_WIPE_NO_GRACE = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R7e (host|guest): no ''everyone is down'' reload' }   # T-215 #2
    @{ Name = 'MUTATE_ESCAPE_KEEP_PENDING (R7f)'; Env = @{ ORICOOP_MUTATE_ESCAPE_KEEP_PENDING = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R7f guest: after the session''s end' }   # T-215 #3
    @{ Name = 'MUTATE_STALE_SKULL (R7a)'; Env = @{ ORICOOP_MUTATE_STALE_SKULL = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R7a guest: killed right after relocating' }   # T-215 #4
    @{ Name = 'MUTATE_STALE_OFFER_SPOT (R7d)'; Env = @{ ORICOOP_MUTATE_STALE_OFFER_SPOT = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R7d host: the guest''s second save is announced where it was made' }   # T-215 #6
    @{ Name = 'MUTATE_RESEND_AS_NEW (R7c)'; Env = @{ ORICOOP_MUTATE_RESEND_AS_NEW = '1'; ORICOOP_PHASES = 'R' }; Script = @('run-nettest.ps1'); Expect = '^R7c guest: (the save point told again|comes back with full health)' }   # T-215 #7
    @{ Name = 'MUTATE_NO_SEAT_SKILLS (4P)'; Env = @{ ORICOOP_MUTATE_NO_SEAT_SKILLS = '1' }; Script = @('run-nettest.ps1', '-Clients', '4'); Expect = '^R skills|4P skills' }   # T-043
    @{ Name = 'MUTATE_WORLD_NO_REBROADCAST (3P)'; Env = @{ ORICOOP_MUTATE_WORLD_NO_REBROADCAST = '1' }; Script = @('run-nettest.ps1', '-Clients', '3'); Expect = '^3P P\d has guest P2''s world object change' }   # T-141, T-192: whole-blob fixture (world events go via the arbiter since T-168)
    @{ Name = 'MUTATE_WORLD_WHOLE_BLOB (3P)'; Env = @{ ORICOOP_MUTATE_WORLD_WHOLE_BLOB = '1' }; Script = @('run-nettest.ps1', '-Clients', '3'); Expect = '^3P P\d keeps every seat''s same-RTT world event' }   # T-168
    @{ Name = 'MUTATE_SHARE_TRIGGERS (KURO)'; Env = @{ ORICOOP_MUTATE_SHARE_TRIGGERS = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_KURO = '1' }; Script = @('run-nettest.ps1'); Expect = '^KURO chain: (stories ended|no letterbox|own Ori walks|own Ori back at the spirit tree|03 and 04 each ran once)' }   # T-183
    @{ Name = 'MUTATE_MIRROR_TRIGGER_RUNS (KURO)'; Env = @{ ORICOOP_MUTATE_MIRROR_TRIGGER_RUNS = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_KURO = '1' }; Script = @('run-nettest.ps1'); Expect = '^KURO chain: 03 and 04 each ran once' }   # T-183
    @{ Name = 'MUTATE_SWARM_LOCAL (E5s)'; Env = @{ ORICOOP_MUTATE_SWARM_LOCAL = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5L?s a Swarm is tracked by enemy sync' }   # T-195
    @{ Name = 'MUTATE_E3_NO_LATE_LIFE (E5k 1)'; Env = @{ ORICOOP_MUTATE_E3_NO_LATE_LIFE = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(1\) a piece its former simulator killed' }   # T-208
    @{ Name = 'MUTATE_SWARM_KEEP_COPIES (E5k 2)'; Env = @{ ORICOOP_MUTATE_SWARM_KEEP_COPIES = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(2\) the end of a session removes' }   # T-208
    @{ Name = 'MUTATE_SWARM_FOLLOW_SCENE (E5k 3)'; Env = @{ ORICOOP_MUTATE_SWARM_FOLLOW_SCENE = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(3\) ' }   # T-208
    @{ Name = 'MUTATE_E3_BLANK_GRANT (E5k 5)'; Env = @{ ORICOOP_MUTATE_E3_BLANK_GRANT = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(5\) an enemy the host granted' }   # T-208
    @{ Name = 'MUTATE_E3_IDS_FROM_ONE (E5k 6)'; Env = @{ ORICOOP_MUTATE_E3_IDS_FROM_ONE = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(6\) a hit made after the id counter' }   # T-208
    @{ Name = 'MUTATE_E3_FREEZE_ALL (E5k 7)'; Env = @{ ORICOOP_MUTATE_E3_FREEZE_ALL = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(7\) an ownership freeze leaves' }   # T-208
    @{ Name = 'MUTATE_E3_SIGHT_NO_PLAYER (E5k 8)'; Env = @{ ORICOOP_MUTATE_E3_SIGHT_NO_PLAYER = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(8\) an enemy looking for a player' }   # T-208
    @{ Name = 'MUTATE_E3_TAKEOVER_NO_LIMIT (E5k 9)'; Env = @{ ORICOOP_MUTATE_E3_TAKEOVER_NO_LIMIT = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(9\) a handoff that got lost' }   # T-208
    @{ Name = 'MUTATE_E3_CANCEL_RESETS (E5k review 2)'; Env = @{ ORICOOP_MUTATE_E3_CANCEL_RESETS = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(9\) a death just before the freeze stands' }   # T-208
    @{ Name = 'MUTATE_E3_LATE_DEATH_NEEDS_ORI (E5k review 3)'; Env = @{ ORICOOP_MUTATE_E3_LATE_DEATH_NEEDS_ORI = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(1\) (and the two pieces it split into|no body of that piece)' }   # T-208
    @{ Name = 'MUTATE_SWARM_DROP_FROZEN_OWN (E5k review 4)'; Env = @{ ORICOOP_MUTATE_SWARM_DROP_FROZEN_OWN = '1'; ORICOOP_PHASES = 'E5' }; Script = @('run-nettest.ps1'); Expect = '^E5k \(2\) the end of a session keeps the pieces of a simulator frozen' }   # T-208
    @{ Name = 'MUTATE_STREAM_IN_STORY (KURO)'; Env = @{ ORICOOP_MUTATE_STREAM_IN_STORY = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_KURO = '1' }; Script = @('run-nettest.ps1'); Expect = '^KURO chain: (03 and 04 each ran once|own Ori back at the spirit tree|no letterbox)' }   # T-183 (KURO=1 holds the host's 03 2 s so it is behind)
    @{ Name = 'MUTATE_GATE_FIRE_BUTTON (SEIN)'; Env = @{ ORICOOP_MUTATE_GATE_FIRE_BUTTON = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_SEIN = '1' }; Script = @('run-nettest.ps1'); Expect = '^SEIN 2 the pickup story started' }   # T-196
    @{ Name = 'MUTATE_GATE_NO_RELEASE (SEIN)'; Env = @{ ORICOOP_MUTATE_GATE_NO_RELEASE = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_SEIN = '1' }; Script = @('run-nettest.ps1'); Expect = '^SEIN 0 a fire the trigger refused frees the area' }   # T-207 (3)
    @{ Name = 'MUTATE_GATE_SCAN_EVERY_FRAME (SEIN)'; Env = @{ ORICOOP_MUTATE_GATE_SCAN_EVERY_FRAME = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_SEIN = '1' }; Script = @('run-nettest.ps1'); Expect = '^SEIN 5 an area whose trigger is not loaded here' }   # T-207 (4)
    @{ Name = 'MUTATE_GATE_NO_ADOPT (SEIN)'; Env = @{ ORICOOP_MUTATE_GATE_NO_ADOPT = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_SEIN = '1' }; Script = @('run-nettest.ps1'); Expect = '^SEIN 2 the pickup story started' }   # T-196
    @{ Name = 'MUTATE_STORY_ENDS_ON_GAP (SEIN)'; Env = @{ ORICOOP_MUTATE_STORY_ENDS_ON_GAP = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_SEIN = '1' }; Script = @('run-nettest.ps1'); Expect = '^SEIN 3 (a story was announced|the script ran through to its last wait|no guard repair)' }   # T-196
    @{ Name = 'MUTATE_SEQ_PROGRESS_SHARED (SEIN)'; Env = @{ ORICOOP_MUTATE_SEQ_PROGRESS_SHARED = '1'; ORICOOP_MUTATE_STORY_ENDS_ON_GAP = '1'; ORICOOP_NET_ONLY = 'P3'; ORICOOP_P3_SEIN = '1' }; Script = @('run-nettest.ps1'); Expect = '^SEIN 4 (input is free|own Ori walks)' }   # T-196 (the playtest chain: early end, then the guest's step replaces the host's and the host stays in the character animation)
)
if (-not $NoMutations) {
    $mdir = Join-Path $env:TEMP ('oricoop-mutations-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force $mdir | Out-Null
    $queue = New-Object System.Collections.Queue; foreach ($m in $Mutations) { $queue.Enqueue($m) }
    $running = @(); $mres = @()
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt $Parallel) {
            $m = $queue.Dequeue(); $log = Join-Path $mdir (($m.Name -replace '[^\w]+', '_') + '.txt')
            $saved = @{}; foreach ($k in $m.Env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $m.Env[$k]) }
            $env:ORICOOP_SESSION = 'nightly ' + $sha + ' ' + $m.Name
            $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $src $m.Script[0])) + @($m.Script | Select-Object -Skip 1)
            $p = Start-Process powershell -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError ($log + '.err')
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
            $running += [pscustomobject]@{ M = $m; P = $p; Log = $log }
            'MUTATION START ' + $m.Name
            Start-Sleep -Seconds 5
        }
        Start-Sleep -Seconds 5
        foreach ($r in @($running)) {
            if (-not $r.P.HasExited) { continue }
            $text = $(if (Test-Path $r.Log) { Get-Content $r.Log -Raw } else { '' })
            $err = $(if (Test-Path ($r.Log + '.err')) { (Get-Content ($r.Log + '.err') -Raw) } else { '' })
            $fails = @([regex]::Matches($text, '(?m)(?:\]|log:) FAIL (.*)$') | ForEach-Object { $_.Groups[1].Value.Trim() } | Select-Object -Unique)
            $fails += @([regex]::Matches($text, '(?m)ROOMS FAIL \d+ (.*)$') | ForEach-Object { $_.Groups[1].Value.Trim() } | Select-Object -Unique)
            $hit = @($fails | Where-Object { $_ -match $r.M.Expect })
            $other = @($fails | Where-Object { $_ -notmatch $r.M.Expect })
            $complete = $text -match '(?m)(\] RESULT |NETTEST (PASS|FAIL)|TITLEJOIN \w+ (PASS|FAIL)|ROOMS RESULT (PASS|FAIL))'
            $status = $(if ($text -cmatch 'OWNER IS PLAYING') { 'STOPPED (owner playing)' } elseif ($hit.Count -gt 0) { 'caught' }
                elseif ($err -match '\S' -and -not $complete) { 'ERROR' } elseif (-not $complete -or $text -match 'no result file') { 'BLOCKED' } else { 'MISSED' })
            $detail = $(if ($hit.Count -gt 0) { $hit[0] } elseif ($status -eq 'ERROR') { ($err -split "`n")[0].Trim() } elseif ($other.Count -gt 0) { 'other failures: ' + (($other | Select-Object -First 2) -join ' | ') } else { '' })
            if ($detail.Length -gt 160) { $detail = $detail.Substring(0, 160) + '...' }
            $line = $r.M.Name + ': ' + $status + $(if ($detail) { ' (' + $detail + ')' } else { '' })
            $mres += $line; 'MUTATION ' + $line
            $running = @($running | Where-Object { $_ -ne $r })
        }
    }
    $env:ORICOOP_FAIL_FAST = '0'
    if (Test-Path $summary) { Add-Content -LiteralPath $summary -Value (@('--- mutation checks (each must FAIL), logs ' + $mdir) + $mres) }
    if (@($mres | Where-Object { $_ -match ': MISSED' }).Count -gt 0 -and $code -eq 0) { $code = 1 }
}
if (Test-Path $summary) { Add-Content -LiteralPath $summary -Value ('commit ' + $sha + ', exit ' + $code) }
'NIGHTLY summary: ' + $summary
exit $code
