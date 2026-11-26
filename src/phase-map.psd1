# Which net phases cover which source file (T-078). Read by src\test-selection.ps1 (run-affected, the selfcheck, nightly).
# Phases (NetTestHarness.AllPhases): INTRO MOVE W E3 E5 R S4 P WO CAM CUT ESC. Start-up and the closing G checks always run.
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
        'RoomFailureTest' = ''
        'NetIntroTest' = 'INTRO'; 'NetReviveTest' = 'R'; 'NetStoryTest' = 'S4'; 'NetPhysTest' = 'P'; 'NetWorldObjTest' = 'WO'
        'WorldSync.Bodies' = 'W,WO'
        'NetPickupCheckpointTest' = 'W'; 'SavedPickupCopies' = 'W'; 'NetCellAuthorityTest' = 'W'
        'NetSkillFxTest' = 'E3'; 'NetFacingTest' = 'MOVE'   # T-199 partner facing after wall jumps (scenario FACE)
        'NetPhysTest.SoloRejoin' = 'P'; 'NetCoPushReportTest' = 'P'; 'NetPhysDepartedHolderTest' = 'P'
        'CoPushReport' = 'P'; 'StoneSeed' = 'P'
        'NetStandInTest' = 'MOVE'; 'StandInCorrection' = 'MOVE'   # T-251 the stand-in jumps instead of sliding through walls (MOVE WALL; rules: NetCore unit tests)
        'NetCameraTest' = 'CAM'; 'NetCutsceneTest' = 'CUT'; 'EnemySyncTest' = 'E5'; 'NetKuroTest' = 'CUT'; 'NetSeinTest' = 'CUT'; 'NetStoryLeftoverTest' = 'CUT'; 'NetStoryScan' = 'CUT'; 'NetStorySweep' = 'CUT'
        'WorldObjSync' = 'WO,R'; 'PushBlockSync' = 'P'; 'ProgressSync' = 'INTRO,W,R,S4,CAM,CUT'
        'OnlineCamera' = 'CAM'; 'CoopCamera' = 'MOVE,CAM'; 'CutsceneGuard' = 'INTRO,S4,CAM,CUT'
        'KeystoneRecovery' = 'W'; 'NetKeystoneRecoveryTest' = 'W'
        'EnemySync.Swarm' = 'E3,E5'; 'SwarmFamily' = 'E5'   # T-195: the splitting Swarm (E5s checks; ids: unit tests)
        'EnemySync.Leftovers' = 'E3,E5'; 'EnemySyncTest.Leftovers' = 'E5'; 'HitIds' = 'E3,E5'   # T-208: enemy-sync leftovers (E5k checks; rules: unit tests)
        'PartnerFx' = 'R,S4,E3'; 'ReviveHud' = 'R'; 'NetHud' = 'R,S4'; 'CoopMenu' = 'S4,W'; 'MenuSoulFlameGuard' = 'S4'; 'MenuSoulFlameTest' = 'S4'   # Online screens leave LockInput alone
        'P2Input' = 'MOVE'; 'InputState' = 'MOVE'; 'P2Lifecycle' = 'MOVE,R'; 'PlatformFix' = ''; 'TitleReturn' = ''; 'P2Extras' = 'INTRO'
        'NetSaveHandoverTest' = ''; 'MovementEpochs' = 'MOVE,S4'
        'NetLocalPlayersTest' = ''   # runs only in the -LocalPlayers suites (no 2-player phase)
        'StoryLeftoverTest' = ''   # T-207 / T-212 scenarios of the local suite (run-test.ps1)
        'TitleRoomExitTest' = ''
        'TitleRoomOverlayExitTest' = ''
        'TitleWaitCancelTest' = ''
        'TitleLobbyRecoveryTest' = ''
        'SharedPrologue' = 'INTRO,MOVE,W'; 'PrologueCore' = ''; 'TitlePrologueTest' = ''; 'TitlePrologueProbe' = ''   # T-203 / T-205: its own checks run in the titlejoin-prologue suites; the input hook and the join wait touch INTRO / MOVE / W
        'VirtualPads' = 'MOVE'; 'VirtualPadPatch' = 'MOVE'; 'PsHid' = 'MOVE'; 'PadDetector' = ''; 'PadGlyphs' = ''; 'GlyphProbe' = ''   # T-178 (PsReport/MirrorJudge/PadTypes: unit tests only)
        'PsReport' = ''; 'MirrorJudge' = ''; 'PadTypes' = ''
        'NetEscapeTest' = 'ESC'; 'EscapeRules' = 'ESC'; 'Revive.Escape' = 'R,ESC'; 'EscapeProbe' = ''   # T-169 escapes (3P part: net-3p suites)
        'NetMapMarkerTest' = 'S4'; 'TeammateMapMarkers' = 'S4'
        'EnemySync' = 'E3,E5,CUT'   # T-183: partner scene streaming is paused during stories (KURO, phase CUT)
        'CoopTraceAudio' = ''; 'AudioWatch' = ''   # T-252: audio trace lines only, nothing in play changes (rules: AudioWatch unit tests)
        'GameText' = 'R,CUT'   # T-255: full-alpha text is checked in GATE G1 (CUT, ORICOOP_P3_GATE=1) and R6 (the self-revive hint); its module M8 selects no net phase
    }
    # Every test-only mutation switch: where it lives, which suite and which net phase (2-player suites) must catch it.
    # Suite 'net' means the 2-player net suites (net, net-newgame), which take ORICOOP_PHASES.
    Mutations = @(
        @{ Switch = 'ORICOOP_MUTATE_MAP_PROJECTION'; File = 'src\OriCoop\TeammateMapMarkers.cs'; Suite = 'net'; Phase = 'S4' }
        @{ Switch = 'ORICOOP_MUTATE_GUARD_FIGHTS_SCENE'; File = 'src\OriCoop\CutsceneGuard.cs'; Suite = 'net'; Phase = 'CUT' }
        @{ Switch = 'ORICOOP_MUTATE_LEVER_DROP_HOLD'; File = 'src\OriCoop\CutsceneGuard.cs'; Suite = 'net'; Phase = 'CUT' }   # T-194 a partner's world blob does not drop this PC's hold on a lever (CUT real grab)
        @{ Switch = 'ORICOOP_MUTATE_STONE_RAW_APPLY'; File = 'src\OriCoop\CutsceneGuard.cs'; Suite = 'net'; Phase = 'CUT' }   # T-204 a partner activating a map stone releases this PC's highlight (CUT map stone, story stays local)
        @{ Switch = 'ORICOOP_MUTATE_MAP_DOWNGRADE'; File = 'src\OriCoop\World\WorldItems.cs'; Suite = 'net'; Phase = 'W' }   # T-214 map exploration only grows (W map: a cell explored while a snapshot is on its way)
        @{ Switch = 'ORICOOP_MUTATE_PICKUP_CLONE_ALIAS'; File = 'src\OriCoop\SavedPickupCopies.cs'; Suite = 'net'; Phase = 'W' }
        @{ Switch = 'ORICOOP_MUTATE_PICKUP_CLONE_FORGOTTEN'; File = 'src\OriCoop\SavedPickupCopies.cs'; Suite = 'net'; Phase = 'W' }
        @{ Switch = 'ORICOOP_MUTATE_PICKUP_GUID_ONLY'; File = 'src\OriCoop\ProgressSync.cs'; Suite = 'net'; Phase = 'W' }   # T-214 a pickup is its GUID plus scene and spot (W pickups: two copies of one prefab; the host cannot take the guest's pickup again)
        @{ Switch = 'ORICOOP_MUTATE_CELL_REFILLS_HOST'; File = 'src\OriCoop\ProgressSync.cs'; Suite = 'net'; Phase = 'W' }   # T-214 a health / energy cell refills the player who took it (W cells)
        @{ Switch = 'ORICOOP_MUTATE_CELL_REWARD_LOSER'; File = 'src\OriCoop\ProgressSync.cs'; Suite = 'net'; Phase = 'W' }
        @{ Switch = 'ORICOOP_MUTATE_HOST_SEAT_ONE';  File = 'src\OriCoop\Authority\NetRoute.cs'; Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_FX_FORWARD';  File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net-3p';    Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_LATE_COMMIT'; File = 'src\OriCoop\Saves\CoopSaves.Lineage.cs'; Suite = 'net-3p'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_DROP_PENDING_LINK'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net-3p';    Phase = '' }   # A room link received while loading updates the native flame afterwards
        @{ Switch = 'ORICOOP_MUTATE_KEEP_STALE';     File = 'src\OriCoop\Authority\CServerLink.cs'; Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_CREATOR_HOST';   File = 'src\OriCoop\NetPlay.cs';      Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_PROMOTION';   File = 'src\OriCoop\Saves\CoopSaves.Lineage.cs'; Suite = 'net-3p-cs'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_PUPPET_CAMERA';  File = 'src\OriCoop\PuppetCameraGuard.cs'; Suite = 'net';  Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_FACING';      File = 'src\OriCoop\NetPlay.cs';      Suite = 'net';       Phase = 'MOVE' }   # T-199 the partner's facing comes from the network (FACE)
        @{ Switch = 'ORICOOP_MUTATE_STANDIN_SLIDES'; File = 'src\OriCoop\NetPlay.cs';      Suite = 'net';       Phase = 'MOVE' }   # T-251 a stand-in pull that crosses a wall jumps (MOVE WALL; NetCore unit tests)
        @{ Switch = 'ORICOOP_MUTATE_FAIL_KEEPS_REMOTES'; File = 'src\OriCoop\NetPlay.cs';  Suite = 'net';       Phase = '' }   # T-213 a failed transport ends the session like a goodbye (closing G checks, every run)
        @{ Switch = 'ORICOOP_MUTATE_PRIMARY_SEAT_ONE'; File = 'src\OriCoop\NetPlay.cs';    Suite = 'net-3p-cs'; Phase = '' }   # T-213 after the host changed, a guest's host is the arbiter's seat
        @{ Switch = 'ORICOOP_MUTATE_NO_STREAM_RESTART'; File = 'src\OriCoop\NetPlay.cs';   Suite = 'net';       Phase = '' }   # T-213 a stale frame far ahead does not freeze the partner's Ori (closing G checks; NetCore unit tests)
        @{ Switch = 'ORICOOP_MUTATE_EXTRA_KEEPS_HISTORY'; File = 'src\OriCoop\NetPlay.cs'; Suite = 'net-lp2';   Phase = '' }   # T-213 an extra local player's frames from before a load are not sent again
        @{ Switch = 'ORICOOP_MUTATE_EXTRA_KEEPS_STREAM'; File = 'src\OriCoop\NetPlay.cs';  Suite = 'net-lp2';   Phase = '' }   # T-213 an extra local player's old frames and position are dropped while its PC loads
        @{ Switch = 'ORICOOP_MUTATE_NO_LIFECYCLE';   File = 'src\OriCoop\Plugin.cs';         Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_KEEP_NET';       File = 'src\OriCoop\TitleReturn.cs';    Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_PLATFORMFIX'; File = 'src\OriCoop\PlatformFix.cs';    Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_BOTTOMHUD_P1VALUES'; File = 'src\OriCoop\BottomHud.cs'; Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_BOTTOMHUD_LAYOUT'; File = 'src\OriCoop\BottomHud.cs'; Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_SIGNPOST';       File = 'src\OriCoop\P2Extras.cs';       Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_STEAM_NO_ACCEPT'; File = 'src\OriCoop\SteamTransport.cs'; Suite = 'titlejoin-steam'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_STEAM_KEEP_PENDING'; File = 'src\OriCoop\SteamTransport.cs'; Suite = 'titlejoin-steam-retry'; Phase = '' }   # T-216 a given-up lobby operation blocks every retry
        @{ Switch = 'ORICOOP_MUTATE_STEAM_KEEP_PROBLEM'; File = 'src\OriCoop\NetPlay.cs'; Suite = 'titlejoin-steam-retry'; Phase = '' }   # T-216 the host's Problem stays after its lobby is up again
        @{ Switch = 'ORICOOP_MUTATE_LOBBY_RECOVERY'; File = 'src\OriCoop\NativeLobby.cs'; Suite = 'titlejoin-recovery'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_COMPANION_RETRY'; File = 'src\OriCoop\P2Extras.cs';   Suite = 'local';     Phase = '' }   # T-213 a companion return that threw is tried again (scenario companion)
        @{ Switch = 'ORICOOP_MUTATE_COMPANION_RETURN_INACTIVE'; File = 'src\OriCoop\P2Extras.cs'; Suite = 'net'; Phase = 'INTRO' }
        @{ Switch = 'ORICOOP_MUTATE_HUB_LEAVE_TRACE'; File = 'src\OriCoop\NetPlay.cs'; Suite = 'titlejoin-room-exit'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_ROOM_CANCEL_NAV'; File = 'src\OriCoop\OnlineRoomScreen.cs'; Suite = 'titlejoin-room-exit'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_DOUBLE_BACKSPACE'; File = 'src\OriCoop\OnlineLobby.cs'; Suite = 'titlejoin-room-exit'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_KEEP_MODAL';     File = 'src\OriCoop\NetHud.cs';         Suite = 'titlejoin'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_ORB_MENUS';      File = 'src\OriCoop\CsRoomPage.cs';     Suite = 'titlejoin-cs'; Phase = '' }   # T-173 room page orb copy keeps the difficulty picker
        @{ Switch = 'ORICOOP_MUTATE_HIDE_ICONS';     File = 'src\OriCoop\NativeGui.cs';      Suite = 'titlejoin-cs'; Phase = '' }   # T-173 game button icons in native labels stay hidden
        @{ Switch = 'ORICOOP_MUTATE_PAUSE_ROOM_ANY'; File = 'src\OriCoop\PauseMenuEntry.cs'; Suite = 'titlejoin';  Phase = '' }   # T-189 pause menu room entry on LAN too
        @{ Switch = 'ORICOOP_MUTATE_NO_SHARED_PROLOGUE'; File = 'src\OriCoop\SharedPrologue.cs'; Suite = 'titlejoin-prologue'; Phase = '' }   # T-203 the guest never plays the prologue along (the old waiting screen)
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_GUEST_CONTROL'; File = 'src\OriCoop\SharedPrologue.cs'; Suite = 'titlejoin-prologue'; Phase = '' }   # T-203 the guest's own input and skip are not replaced
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_NO_CATCHUP'; File = 'src\OriCoop\SharedPrologue.cs'; Suite = 'titlejoin-prologue-late'; Phase = '' }   # T-205 a late guest's copy never changes its speed: it plays from the start
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_OWN_GATES'; File = 'src\OriCoop\SharedPrologue.cs'; Suite = 'titlejoin-prologue'; Phase = '' }   # T-205 the guest's own buttons / character pass the story's player points
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_REPLAY_BLIND'; File = 'src\OriCoop\SharedPrologue.cs'; Suite = 'titlejoin-prologue'; Phase = '' }   # T-205 a trigger of the host's log counts as done although the game did not take it
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_MENU_UNDER_COVER'; File = 'src\OriCoop\SharedPrologue.cs'; Suite = 'titlejoin-prologue-late'; Phase = '' }   # T-205 the pause key opens the pause menu under the catch-up cover
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_INTRO_HOLD'; File = 'src\OriCoop\StorySync.cs'; Suite = 'titlejoin-prologue-deep'; Phase = '' }   # T-205 the intro hold pins and hides the last scene's Ori on a guest
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_ANY_DEVICE'; File = 'src\OriCoop\KeyboardOnlyP1.cs'; Suite = 'titlejoin-prologue'; Phase = '' }   # T-209 (1) every local device reaches the game during the prologue
        @{ Switch = 'ORICOOP_MUTATE_PROLOGUE_GUEST_STAYS_ONLINE'; File = 'src\OriCoop\GuestJoin.cs'; Suite = 'titlejoin-prologue-hostquit-slot'; Phase = '' }   # T-209 (2) a slot-path guest stays online after its host left the prologue
        @{ Switch = 'ORICOOP_MUTATE_TOAST_NO_FALLBACK'; File = 'src\OriCoop\NetHud.cs';      Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_ROOM_NAME_HASH'; File = 'src\OriCoop\NativeRooms.cs';     Suite = 'rooms';   Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_ROOM_FAILURES'; File = 'src\OriCoop\Rooms\RoomSession.cs'; Suite = 'roomfailures'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_MENU_STALE_LOCK'; File = 'src\OriCoop\CoopMenu.cs';      Suite = 'net';       Phase = 'S4' }
        @{ Switch = 'ORICOOP_MUTATE_ROOMS_NATIVE_OFF'; File = 'src\OriCoop\NativeRooms.cs';   Suite = 'rooms';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_OVERLAY_UNLOCK';  File = 'src\OriCoop\TitleMenu.cs';      Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_REMOVE_GLOW';    File = 'src\OriCoop\NativeGlowTint.cs'; Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_FONT_LEAK';       File = 'src\OriCoop\Ui\OsFonts.cs';     Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_MODE_FIXED_SIZE'; File = 'src\OriCoop\Ui\ModeSelectLayout.cs'; Suite = 'local';  Phase = '' }
        @{ Switch = 'ORICOOP_TEST_THROW';            File = 'src\OriCoop\Plugin.cs';         Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_P3_OFF';                File = 'src\OriCoop\CutsceneGuard.cs';  Suite = 'net';       Phase = 'CUT' }
        @{ Switch = 'ORICOOP_MUTATE_MENU_SOULFLAME'; File = 'src\OriCoop\MenuSoulFlameGuard.cs'; Suite = 'net'; Phase = 'S4' }
        @{ Switch = 'ORICOOP_MUTATE_TITLE_STUCK';    File = 'src\OriCoop\TitleAnyKey.cs';    Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_IME_ON';         File = 'src\OriCoop\ImeGuard.cs';       Suite = 'local';     Phase = '' }
        @{ Switch = 'ORICOOP_S4_NO_HOLD';            File = 'src\OriCoop\NetStoryTest.cs';   Suite = 'net';       Phase = 'S4' }
        @{ Switch = 'ORICOOP_PHYS_OFF';              File = 'src\OriCoop\NetPhysTest.cs';    Suite = 'net';       Phase = 'P' }
        @{ Switch = 'ORICOOP_PHYS_OFF';              File = 'src\OriCoop\PushBlockSync.cs';  Suite = 'net';       Phase = 'P'; Mention = 'public static bool Enabled' }
        @{ Switch = 'ORICOOP_MUTATE_PUPPET_STONE_AIR'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net';      Phase = 'P' }   # T-198 a partner standing on a stone is on ground here too (PH)
        @{ Switch = 'ORICOOP_MUTATE_STONE_DROP_HELPER'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net';     Phase = 'P' }   # T-197 the second pusher keeps hold of the stone (PE)
        @{ Switch = 'ORICOOP_MUTATE_STONE_STALE_STATE'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net';     Phase = 'P' }   # T-197 no jump to a state of an older lease epoch (PF)
        @{ Switch = 'ORICOOP_MUTATE_STONE_KEEP_DOWNED_GRAB'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net'; Phase = 'P' }  # T-197 a downed player lets go of the stone (PG)
        @{ Switch = 'ORICOOP_MUTATE_STONE_EXPIRE_OWNER_PUSH'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net-stones'; Phase = 'P' }
        @{ Switch = 'ORICOOP_MUTATE_COPUSH_DEVICE_POSE'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net-stones'; Phase = 'P' }
        @{ Switch = 'ORICOOP_MUTATE_COPUSH_ARRIVAL_ORDER'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net-stones'; Phase = 'P' }
        @{ Switch = 'ORICOOP_MUTATE_STONE_KEEP_DEPARTED_GRIP'; File = 'src\OriCoop\PushBlockSync.cs'; Suite = 'net-stones'; Phase = 'P' }
        @{ Switch = 'ORICOOP_MUTATE_STONE_RESEED_UNPUBLISHED'; File = 'src\OriCoop\Authority\StoneSeed.cs'; Suite = 'net-stones-cs'; Phase = 'P' }
        @{ Switch = 'ORICOOP_WORLDOBJ_OFF';          File = 'src\OriCoop\WorldObjSync.cs';   Suite = 'net';       Phase = 'WO' }
        @{ Switch = 'ORICOOP_WOBJ_NO_DEFER';         File = 'src\OriCoop\WorldObjSync.cs';   Suite = 'net';       Phase = 'WO' }
        @{ Switch = 'ORICOOP_MUTATE_WOBJ_KEEP_FIRED'; File = 'src\OriCoop\WorldObjSync.cs'; Suite = 'net';       Phase = 'R' }   # T-144 R3 reload re-arms one-shots
        @{ Switch = 'ORICOOP_MUTATE_STALE_CS_ROOMS'; File = 'src\OriCoop\Authority\CServerRoomList.cs'; Suite = 'titlejoin-wait-cancel'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_SAVE_WAIT_CANCEL'; File = 'src\OriCoop\SessionScreens.cs'; Suite = 'titlejoin-wait-cancel'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_COOP_FOLDER'; File = 'src\OriCoop\Saves\CoopSaveFolder.cs'; Suite = 'titlejoin'; Phase = '' }   # T-043 online saves stay in OriCoop-coop
        @{ Switch = 'ORICOOP_MUTATE_NO_SAVEOFFER';   File = 'src\OriCoop\SaveSync.cs'; Suite = 'net';       Phase = 'R' }   # T-043 a guest's save point becomes the room's save
        @{ Switch = 'ORICOOP_MUTATE_PREVIOUS_MOVEMENT'; File = 'src\OriCoop\NetPlay.cs'; Suite = 'net'; Phase = 'S4' }
        @{ Switch = 'ORICOOP_MUTATE_QUIT_HOLDS_SEAT'; File = 'src\OriCoop\NetPlay.cs'; Suite = 'quitseat'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_SKIP_EARLY_SAVE_HELLO'; File = 'src\OriCoop\Saves\SeatReadiness.cs'; Suite = 'net-save-handover'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_PROMOTE_ALL_READY'; File = 'src\OriCoop\SaveSync.cs'; Suite = 'net-3p-cs'; Phase = '' }   # T-213 a new host sends its save to a guest still waiting for its first one
        @{ Switch = 'ORICOOP_MUTATE_NO_SEAT_SKILLS'; File = 'src\OriCoop\ProgressSync.cs'; Suite = 'net';     Phase = 'R' }   # T-043 checkpoint skill rollback
        @{ Switch = 'ORICOOP_MUTATE_NO_SEAT_SKILLS'; File = 'src\OriCoop\ProgressSync.cs'; Suite = 'net-4p';  Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_DEATH_SAVE';  File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net'; Phase = 'R' }   # T-041 a soul link while someone is down costs half the creator's health
        @{ Switch = 'ORICOOP_MUTATE_SELF_REVIVE_IN_PLACE'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net'; Phase = 'R' }   # T-041 hold Y revives at the latest soul link
        @{ Switch = 'ORICOOP_MUTATE_LINK_HOST_ONLY'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net'; Phase = 'R' }   # T-041 a guest's adopted save offer is the latest soul link
        @{ Switch = 'ORICOOP_MUTATE_LINK_HOST_ONLY'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net-4p'; Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_NO_CHECKPOINT_POINT'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net'; Phase = 'R' }   # T-202 no room save yet: hold Y comes back at the host's latest checkpoint
        @{ Switch = 'ORICOOP_MUTATE_REVIVE_NEEDS_RESCUER'; File = 'src\OriCoop\Revive.cs'; Suite = 'net'; Phase = 'R' }   # T-215 #1 a revive whose rescuer's Ori is gone still completes (R7b)
        @{ Switch = 'ORICOOP_MUTATE_WIPE_NO_GRACE'; File = 'src\OriCoop\ReviveNet.cs'; Suite = 'net'; Phase = 'R' }   # T-215 #2 the host waits before declaring everyone down (R7e)
        @{ Switch = 'ORICOOP_MUTATE_ESCAPE_KEEP_PENDING'; File = 'src\OriCoop\Revive.Escape.cs'; Suite = 'net'; Phase = 'R' }   # T-215 #3 the session's end drops the work owed after an escape finish (R7f)
        @{ Switch = 'ORICOOP_MUTATE_STALE_SKULL'; File = 'src\OriCoop\Revive.cs'; Suite = 'net'; Phase = 'R' }   # T-215 #4 the skull is where the Ori died once it left its last ground (R7a)
        @{ Switch = 'ORICOOP_MUTATE_STALE_OFFER_SPOT'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net'; Phase = 'R' }   # T-215 #6 every save offer carries its own spot (R7d)
        @{ Switch = 'ORICOOP_MUTATE_RESEND_AS_NEW'; File = 'src\OriCoop\Revive.SoulLink.cs'; Suite = 'net'; Phase = 'R' }   # T-215 #7 the save point told again is not a new soul link (R7c)
        @{ Switch = 'ORICOOP_MUTATE_CAM_NO_MATES';   File = 'src\OriCoop\OnlineCamera.cs';   Suite = 'net';       Phase = 'CAM' }
        @{ Switch = 'ORICOOP_MUTATE_CAM_NO_UNPARK';  File = 'src\OriCoop\CutsceneGuard.cs';  Suite = 'net';       Phase = 'CAM' }
        @{ Switch = 'ORICOOP_MUTATE_NO_POST_SESSION_FADE'; File = 'src\OriCoop\CutsceneGuard.cs'; Suite = 'local'; Phase = '' }   # T-212 (1) a screen left black when the session ended is faded back in (local scenario afterblack)
        @{ Switch = 'ORICOOP_MUTATE_KEEP_PLAYED_ON_LOAD'; File = 'src\OriCoop\CutsceneGuard.cs'; Suite = 'net'; Phase = 'CUT' }   # T-212 (7) a loaded save forgets the pedestal plays of the last 30 s (CUT reload)
        @{ Switch = 'ORICOOP_MUTATE_GATE_KEEP_FIRED_ON_RESTORE'; File = 'src\OriCoop\ForcedSync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-212 (6) a checkpoint restore clears the gates' fired marks (CUT reload)
        @{ Switch = 'ORICOOP_MUTATE_GATE_NO_RELEASE'; File = 'src\OriCoop\ForcedSync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-207 (3) the host tells everyone when a fire came to nothing (SEIN step 0)
        @{ Switch = 'ORICOOP_MUTATE_GATE_SCAN_EVERY_FRAME'; File = 'src\OriCoop\ForcedSync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-207 (4) an unresolved gate's trigger is looked for twice a second (SEIN step 5)
        @{ Switch = 'ORICOOP_MUTATE_END_LOST_IN_WINDOW'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-212 (4) a story's end is sent and applied inside a local-only window (CUT, LEFT 4)
        @{ Switch = 'ORICOOP_MUTATE_NO_LATE_START'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-212 (5) a story whose end came before its mirror started is still played (CUT, LEFT 5)
        @{ Switch = 'ORICOOP_MUTATE_KEEP_DROPPED_MIRROR'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-212 (3) a dropped teammate's mirrored story stops while another mirror runs (CUT, LEFT 3)
        @{ Switch = 'ORICOOP_MUTATE_WINDOW_DROPS_STARTS'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-207 (2) a partner's unrelated story is mirrored inside a lever / pedestal window (CUT, LEFT 2)
        @{ Switch = 'ORICOOP_MUTATE_SWARM_LOCAL';    File = 'src\OriCoop\EnemySync.Swarm.cs'; Suite = 'net';      Phase = 'E5' }   # T-195 a Swarm and the pieces it splits into are the same on every PC (E5s)
        @{ Switch = 'ORICOOP_MUTATE_E3_NO_LATE_LIFE'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (1) a split piece's death from its former simulator is taken after the family changed hands (E5k)
        @{ Switch = 'ORICOOP_MUTATE_SWARM_KEEP_COPIES'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (2) the end of a session removes the pieces a PC only showed (E5k)
        @{ Switch = 'ORICOOP_MUTATE_SWARM_FOLLOW_SCENE'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (3) live pieces are still sent when their family's scene unloads (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_BLANK_GRANT'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (5) a grant without a captured state leaves the enemy where it is (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_IDS_FROM_ONE'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (6) hit ids start anew per session (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_FREEZE_ALL'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (7) ownership freezes skip classes that are not mirrored (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_SIGHT_NO_PLAYER'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (8) no sideways look-out without a player (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_TAKEOVER_NO_LIMIT'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 (9) an overdue handoff is given up and asked for again (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_CANCEL_RESETS'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 review 2 a death just before an abandoned handoff stands (E5k)
        @{ Switch = 'ORICOOP_MUTATE_E3_LATE_DEATH_NEEDS_ORI'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 review 3 a late piece death without a local Ori still kills and splits it (E5k)
        @{ Switch = 'ORICOOP_MUTATE_SWARM_DROP_FROZEN_OWN'; File = 'src\OriCoop\EnemySync.Leftovers.cs'; Suite = 'net'; Phase = 'E5' }   # T-208 review 4 the end of a session keeps a frozen simulator's pieces (E5k)
        @{ Switch = 'ORICOOP_NET_NO_FORWARD';        File = 'src\OriCoop\NetPlay.cs';        Suite = 'net-4p';    Phase = '' }
        @{ Switch = 'ORICOOP_MUTATE_WORLD_NO_REBROADCAST'; File = 'src\OriCoop\WorldSync.cs'; Suite = 'net-3p'; Phase = '' }   # T-141 3P guest world change
        @{ Switch = 'ORICOOP_MUTATE_UNSYNCED_BODIES'; File = 'src\OriCoop\WorldSync.cs'; Suite = 'net'; Phase = 'WO' }
        @{ Switch = 'ORICOOP_MUTATE_ESCAPE_NO_TEAM'; File = 'src\OriCoop\Revive.Escape.cs'; Suite = 'net';   Phase = 'ESC' }   # T-169 one finisher brings the team
        @{ Switch = 'ORICOOP_MUTATE_ESCAPE_MASH_NEAREST'; File = 'src\OriCoop\Revive.Escape.cs'; Suite = 'net-3p'; Phase = '' }   # T-169 mash revive: a random teammate in the escape
        @{ Switch = 'ORICOOP_MUTATE_WORLD_WHOLE_BLOB'; File = 'src\OriCoop\WorldSync.Delta.cs'; Suite = 'net-3p'; Phase = '' }   # T-168 same-RTT world events (net W5, net-4p and net-3p-cs check it too)
        @{ Switch = 'ORICOOP_MUTATE_TIMER_PHASE_ONCE'; File = 'src\OriCoop\WorldSync.cs'; Suite = 'net'; Phase = '' }   # T-250 the host resends its level timers' phase (no game check yet: World TimerPhaseTests)
        @{ Switch = 'ORICOOP_MUTATE_SHOT_FOLLOW_OFF'; File = 'src\OriCoop\WorldObjSync.cs'; Suite = 'net'; Phase = 'WO' }   # #444 the Ginso shooter fires with the host's shots (WO2 / WO2L check it)
        @{ Switch = 'ORICOOP_MUTATE_SHARE_TRIGGERS'; File = 'src\OriCoop\WorldSync.Delta.cs'; Suite = 'net'; Phase = 'CUT' }   # T-183 string triggers stay per PC (KURO post-story checks; World unit tests)
        @{ Switch = 'ORICOOP_MUTATE_MIRROR_TRIGGER_RUNS'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-183 a mirror's start consumes the string trigger (KURO: 03 and 04 ran once)
        @{ Switch = 'ORICOOP_MUTATE_START_IN_SCENE_TRIP'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-185 a mirrored start waits for this PC's scene trip (KURO=1 over 90 ms: stories ended only once 04 finished, own Ori back at the tree)
        @{ Switch = 'ORICOOP_MUTATE_SERIALIZE_STOPS'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-183 a scene's LateStart load does not stop a running sequence (KURO: 03 and 04 ran once)
        @{ Switch = 'ORICOOP_MUTATE_STREAM_IN_STORY'; File = 'src\OriCoop\EnemySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-183 the host does not stream the partner's scenes during a story (KURO: 03 and 04 ran once, Ori back at the tree)
        @{ Switch = 'ORICOOP_MUTATE_GATE_FIRE_BUTTON'; File = 'src\OriCoop\ForcedSync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-196 the gate's fire does not re-check the pickup key (SEIN: story starts when the partner arrives)
        @{ Switch = 'ORICOOP_MUTATE_GATE_NO_ADOPT'; File = 'src\OriCoop\ForcedSync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-196 a player standing in a waited-at area counts as present (SEIN: story starts when the partner arrives)
        @{ Switch = 'ORICOOP_MUTATE_SMALL_PROMPTS'; File = 'src\OriCoop\GameText.cs'; Suite = 'net'; Phase = 'R' }   # T-255 the self-revive hint draws as large as the native hint (R6)
        @{ Switch = 'ORICOOP_MUTATE_DIM_PROMPTS'; File = 'src\OriCoop\GameText.cs'; Suite = 'net'; Phase = 'CUT' }   # T-255 the gate prompts draw their text at full alpha (GATE G1, ORICOOP_P3_GATE=1)
        @{ Switch = 'ORICOOP_MUTATE_STORY_ENDS_ON_GAP'; File = 'src\OriCoop\StorySync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-196 a running script with a takeover step ahead is not over (SEIN: a story announced as long as the script ran)
        @{ Switch = 'ORICOOP_MUTATE_SEQ_PROGRESS_SHARED'; File = 'src\OriCoop\WorldSync.cs'; Suite = 'net'; Phase = 'CUT' }   # T-196 a running story script keeps its own step (SEIN with STORY_ENDS_ON_GAP: the host's Ori walks after the story)
    )
}
