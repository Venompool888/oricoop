# Which net phases cover which source file (T-078). Read by src\test-selection.ps1 (run-affected, the selfcheck, nightly).
# Phases (NetTestHarness.AllPhases): INTRO MOVE W E3 E5 R S4 P WO CAM CUT. Start-up and the closing G checks always run.
# Rules, in order: a file in Full -> every phase; a file in Files -> those phases; its module (docs\modules.md) in
# Modules -> those phases; any other file under src\OriCoop -> every phase (unknown = full). Changing THIS file -> every
# phase. src\test-selection-selfcheck.ps1 proves that every mutation switch below is caught by the phases selected for
# the file it lives in; add a row there whenever a new ORICOOP_MUTATE_* (or *_OFF) switch is added.
@{
    # Shared core: session, transport, seats, save handshake, the harness itself, the composition root.
    Full = @(
        'Plugin', 'NetPlay', 'ProtocolVersion', 'NetPeer', 'Transport', 'ClockSync', 'PeerHub', 'SteamTransport',
        'Players', 'Roles', 'P2Context', 'SaveSync', 'GuestJoin', 'WorldSync', 'Services', 'Seams',
        'NetRoute', 'INetRouteServer', 'CServerConfig', 'CServerClient', 'CServerLink', 'CsWire',
        'IAuthority', 'Arbiter', 'HostArbiter', 'AuthorityClient', 'AuthoritySource', 'DynamicLease',
        'NetTestHarness', 'TestHarness', 'TestClock', 'CoopTrace', 'CoopTraceHooks', 'CoopClock', 'VisualInvariants'
    )
    # Module (docs\modules.md) -> phases. M0/M1 are core (Full above lists their files); unknown members fall to full.
    Modules = @{
        'M0' = 'FULL'
        'M1' = 'FULL'
        'M2' = 'MOVE,CAM'
        'M3' = 'W,P,WO'
        'M4' = 'E3,E5'
        'M5' = 'INTRO,W,S4,CAM,CUT'
        'M6' = 'R,S4'
        'M7' = 'FULL'
        'M8' = ''
        'M9' = 'FULL'
    }
    # Single files (checked before the module): test phases and files whose reach is narrower or wider than their module.
    Files = @{
        'NetIntroTest' = 'INTRO'; 'NetReviveTest' = 'R'; 'NetStoryTest' = 'S4'; 'NetPhysTest' = 'P'; 'NetWorldObjTest' = 'WO'
        'NetCameraTest' = 'CAM'; 'NetCutsceneTest' = 'CUT'; 'EnemySyncTest' = 'E5'; 'NetKuroTest' = 'CUT'; 'NetStoryScan' = 'CUT'; 'NetStorySweep' = 'CUT'
        'WorldObjSync' = 'WO,R'; 'PushBlockSync' = 'P'
        'OnlineCamera' = 'CAM'; 'CoopCamera' = 'MOVE,CAM'; 'CutsceneGuard' = 'INTRO,S4,CAM,CUT'
        'PartnerFx' = 'R,S4,E3'; 'ReviveHud' = 'R'; 'NetHud' = 'R,S4'; 'CoopMenu' = 'S4'   # F8 online holds the player and leaves LockInput alone (T-132)
        'P2Input' = 'MOVE'; 'InputState' = 'MOVE'; 'P2Lifecycle' = 'MOVE,R'; 'PlatformFix' = ''; 'TitleReturn' = ''; 'P2Extras' = ''
        'NetLocalPlayersTest' = ''   # runs only in the -LocalPlayers suites (no 2-player phase)
    }
    # Every test-only mutation switch: where it lives, which suite and which net phase (2-player suites) must catch it.
    # Suite 'net' means the 2-player net suites (net, net-newgame), which take ORICOOP_PHASES.
    Mutations = @(
        @{ Switch = 'ORICOOP_MUTATE_GUARD_FIGHTS_SCENE'; File = 'src\OriCoop\CutsceneGuard.cs'; Suite = 'net'; Phase = 'CUT' }
        @{ Switch = 'ORICOOP_MUTATE_HOST_SEAT_ONE';  File = 'src\OriCoop\Authority\NetRoute.cs'; Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_FX_FORWARD';  File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net-3p';    Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_LATE_COMMIT'; File = 'src\OriCoop\Saves\CoopSaves.Lineage.cs'; Suite = 'net-3p'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_KEEP_STALE';     File = 'src\OriCoop\Authority\CServerLink.cs'; Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_CREATOR_HOST';   File = 'src\OriCoop\NetPlay.cs';      Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_PROMOTION';   File = 'src\OriCoop\Saves\CoopSaves.Lineage.cs'; Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_PUPPET_CAMERA';  File = 'src\OriCoop\PuppetCameraGuard.cs'; Suite = 'net';  Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_LIFECYCLE';   File = 'src\OriCoop\Plugin.cs';         Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_KEEP_NET';       File = 'src\OriCoop\TitleReturn.cs';    Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_PLATFORMFIX'; File = 'src\OriCoop\PlatformFix.cs';    Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_BOTTOMHUD_P1VALUES'; File = 'src\OriCoop\BottomHud.cs'; Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_BOTTOMHUD_LAYOUT'; File = 'src\OriCoop\BottomHud.cs'; Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_SIGNPOST';       File = 'src\OriCoop\P2Extras.cs';       Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_KEEP_MODAL';     File = 'src\OriCoop\NetHud.cs';         Suite = 'titlejoin'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_TOAST_NO_FALLBACK'; File = 'src\OriCoop\NetHud.cs';      Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_ROOM_NAME_HASH'; File = 'src\OriCoop\Rooms\RoomsPanel.cs'; Suite = 'rooms';   Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_MENU_STALE_LOCK'; File = 'src\OriCoop\CoopMenu.cs';      Suite = 'net';       Phase = 'S4' }
        @{ Switch = 'ORICOOP_MUTATE_ROOMS_NATIVE_OFF'; File = 'src\OriCoop\NativeRooms.cs';   Suite = 'rooms';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_OVERLAY_UNLOCK';  File = 'src\OriCoop\TitleMenu.cs';      Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_REMOVE_GLOW';    File = 'src\OriCoop\NativeGlowTint.cs'; Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_FONT_LEAK';       File = 'src\OriCoop\Ui\OsFonts.cs';     Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_MODE_FIXED_SIZE'; File = 'src\OriCoop\Ui\ModeSelectLayout.cs'; Suite = 'local';  Phase = '' }
        @{ Switch = 'ORICOOP_TEST_THROW';            File = 'src\OriCoop\Plugin.cs';         Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_P3_OFF';                File = 'src\OriCoop\CutsceneGuard.cs';  Suite = 'net';       Phase = 'CUT' }
        @{ Switch = 'ORICOOP_S4_NO_HOLD';            File = 'src\OriCoop\NetStoryTest.cs';   Suite = 'net';       Phase = 'S4' }
        @{ Switch = 'ORICOOP_PHYS_OFF';              File = 'src\OriCoop\NetPhysTest.cs';    Suite = 'net';       Phase = 'P' }
        @{ Switch = 'ORICOOP_PHYS_OFF';              File = 'src\OriCoop\PushBlockSync.cs';  Suite = 'net';       Phase = 'P'; Mention = 'public static bool Enabled' }
        @{ Switch = 'ORICOOP_WORLDOBJ_OFF';          File = 'src\OriCoop\WorldObjSync.cs';   Suite = 'net';       Phase = 'WO' }
        @{ Switch = 'ORICOOP_WOBJ_NO_DEFER';         File = 'src\OriCoop\WorldObjSync.cs';   Suite = 'net';       Phase = 'WO' }
        @{ Switch = 'ORICOOP_MUTATE_WOBJ_KEEP_FIRED'; File = 'src\OriCoop\WorldObjSync.cs'; Suite = 'net';       Phase = 'R' }   # T-144 R3 reload re-arms one-shots
        @{ Switch = 'ORICOOP_MUTATE_NO_COOP_FOLDER'; File = 'src\OriCoop\Saves\CoopSaveFolder.cs'; Suite = 'titlejoin'; Phase = '' }   # T-043 online saves stay in OriCoop-coop
        @{ Switch = 'ORICOOP_MUTATE_NO_SAVEOFFER';   File = 'src\OriCoop\SaveSync.cs'; Suite = 'net';       Phase = 'R' }   # T-043 a guest's save point becomes the room's save
        @{ Switch = 'ORICOOP_MUTATE_CAM_NO_MATES';   File = 'src\OriCoop\OnlineCamera.cs';   Suite = 'net';       Phase = 'CAM' }
        @{ Switch = 'ORICOOP_MUTATE_CAM_NO_UNPARK';  File = 'src\OriCoop\CutsceneGuard.cs';  Suite = 'net';       Phase = 'CAM' }
        @{ Switch = 'ORICOOP_NET_NO_FORWARD';        File = 'src\OriCoop\NetPlay.cs';        Suite = 'net-4p';    Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_WORLD_NO_REBROADCAST'; File = 'src\OriCoop\WorldSync.cs'; Suite = 'net-3p'; Phase = '' }   # T-141 3P guest world change
        @{ Switch = 'ORICOOP_MUTATE_WORLD_WHOLE_BLOB'; File = 'src\OriCoop\WorldSync.Delta.cs'; Suite = 'net-3p'; Phase = '' }   # T-168 same-RTT world events (net W5, net-4p and net-3p-cs check it too)
    )
}
