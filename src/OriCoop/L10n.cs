using System;
using System.Collections.Generic;

namespace OriCoop
{
    // One string table for every text OriCoop shows to the player. Each key has a Chinese and an English text; the
    // game's current language picks one (Simplified Chinese -> zh, every other language -> en). Never put both
    // languages in one label.
    //
    // Language: GameSettings.Instance.Language, the same value the game's own MessageBoxes read (LanguageUnlock lets
    // it hold Chinese; ORICOOP_FORCE_ZH=1 makes it read Chinese in tests). T() reads it on every call, so IMGUI text
    // follows a language switch on the next frame. Pages that set a MessageBox once subscribe to Changed and
    // re-set their texts (the game raises OnGameLanguageChange from Options > Language).
    //
    // Chinese glyphs: the game's zh font is replaced/extended by FontGlyphs from src/assets/zh-glyphs. After adding
    // or changing a Chinese text here, rerun make-zh-glyphs.py; tools/check-l10n.py fails when a zh text uses a
    // glyph that is not baked, when a text mixes languages, or when a hard-coded Chinese literal is left outside
    // this file.
    public static partial class L10n
    {
        internal static Func<string> LanguageCode;
        internal static Action EnsureHook = null;
        internal static Action<string> Warning = null;
        static bool s_hooked = false;

        public static bool Zh { get { return LanguageCode != null && LanguageCode() == "zh"; } }

        // A (zh, en) pair where callers may pass text that is already in the player's language and "" for the other
        // (T-135): pick by language, fall back to the other side when the picked one is empty; "" only if both are.
        public static string Pick(string zh, string en, bool zhFirst)
        {
            string first = zhFirst ? zh : en, second = zhFirst ? en : zh;
            return !string.IsNullOrEmpty(first) ? first : second ?? "";
        }
        public static string Pick(string zh, string en) { return Pick(zh, en, Zh); }

        public static string T(string key) { return T(key, Zh); }

        public static string T(string key, bool zh)
        {
            if (!s_hooked && EnsureHook != null) EnsureHook();
            string[] e;
            if (key != null && Table().TryGetValue(key, out e)) return zh ? e[0] : e[1];
            if (s_missing.Count < 100 && key != null && s_missing.Add(key) && Warning != null) Warning("l10n: missing key '" + key + "'");
            return key ?? "";
        }

        // T(key) with string.Format arguments ({0}, {1} ...).
        public static string F(string key, params object[] args)
        {
            string s = T(key);
            try { return string.Format(s, args); }
            catch (FormatException) { return s; }
        }

        internal static bool Set(Action<Func<string>> target, string key)
        {
            if (target == null) return false;
            target(delegate { return T(key); });
            return true;
        }

        internal static bool Set(Action<Func<string>> target, Func<string> text)
        {
            if (target == null) return false;
            target(text);
            return true;
        }

        public static bool Has(string key) { return key != null && Table().ContainsKey(key); }
        public static IEnumerable<string> Keys { get { return Table().Keys; } }

        static readonly HashSet<string> s_missing = new HashSet<string>();
        public static int MissingCount { get { return s_missing.Count; } }
        public static string MissingList { get { return string.Join(", ", new List<string>(s_missing).ToArray()); } }
        static Dictionary<string, string[]> s_table;

        static Dictionary<string, string[]> Table()
        {
            if (s_table != null) return s_table;
            var t = new Dictionary<string, string[]>(StringComparer.Ordinal);
            for (int i = 0; i + 2 < S.Length; i += 3)
            {
                if (t.ContainsKey(S[i]) && Warning != null) Warning("l10n: duplicate key '" + S[i] + "'");
                t[S[i]] = new[] { S[i + 1], S[i + 2] };
            }
            s_table = t;
            return t;
        }

        // key, Chinese, English. Keep one entry per line (tools/check-l10n.py parses this block).
        // Proper nouns allowed inside Chinese text: Steam, IP, P1-P4, OriCoop, F8, numbers (see check-l10n.py).
        // BEGIN STRINGS
        static readonly string[] S =
        {
            "room.title", "联机房间", "Online room",
            "room.invite", "邀请好友", "Invite a friend",
            "room.saves", "存档", "Saves",
            "room.members", "房间成员", "Members",
            "room.member", "P{0} · 设备 {1} · {2}", "P{0} / Device {1} / {2}",
            "room.local", "本机", "Local",
            "room.remote", "远端", "Remote",
            "room.add_local", "增加本机额外玩家", "Add an extra local player",
            "room.host_save", "使用主机选择的存档", "Use the host's selected save",
            "room.pad", "手柄 {0}", "Controller {0}",
            "room.server_pending", "此服务器暂不可增加本机额外玩家", "Extra local players are not supported by this server yet",
            "common.back", "返回", "Back",
            "p2.join_hint", "P2：按手柄任意键（或 {0}）加入  [{1}]    F8：双人设置", "Player 2: press any button on the controller (or {0}) to join  [{1}]    F8: co-op settings",
            "guide.controls", "移动：左摇杆 或 {0}      跳跃：A 键 或 {1}", "Move: left stick or {0}      Jump: A button or {1}",
            "guide.key.arrows", "左右方向键", "Left / Right arrows",
            "guide.key.space", "空格键", "Space",
            "guide.key.left", "左方向键", "Left arrow",
            "guide.key.right", "右方向键", "Right arrow",
            "guide.key.up", "上方向键", "Up arrow",
            "guide.key.down", "下方向键", "Down arrow",
            "guide.key.mouse", "鼠标", "Mouse",
            "p2.pad_found", "已找到手柄", "controller found",
            "p2.pad_missing", "未找到手柄", "no controller found",
            // Server choice sub-page (final names).
            "server.title", "自建服务器", "Your Own Server",
            "server.title.desc", "连接一台你自己的服务器，用房间码联机，不用开端口", "Connect to a server you run yourself and play with a room code. No ports to open.",
            "server.cserver", "服务器模式", "Server Mode",
            "server.cserver.desc", "服务器统一裁决，所有玩家地位相同；有人掉线游戏照常", "The server runs the rules and every player is equal. Anyone can drop out without ending the game.",
            "server.err.noroom", "无此房间", "That room doesn't exist",
            "server.err.full", "房间已满", "The room is full",
            "server.err.version", "版本不符", "Your OriCoop version doesn't match the room's",
            "server.err.token", "重连凭证无效", "Can't rejoin: the rejoin ticket is no longer valid",
            "server.err.kicked", "你已被移出房间", "You were removed from the room",
            "csroom.title", "房间管理", "Room",
            "csroom.players", "玩家列表", "Players",
            "csroom.you", "(你)", "(you)",
            "csroom.waiting_player", "等待玩家加入…", "Waiting for a player...",
            "csroom.waiting", "等待中", "Waiting",
            "csroom.ready", "已准备", "Ready",
            "csroom.not_ready", "未准备", "Not ready",
            "csroom.offline", "已断线", "Disconnected",
            "csroom.playing", "游戏中", "In game",
            "csroom.act.join_game", "加入游戏", "Join the game",
            "csroom.act.start", "开始游戏", "Start game",
            "csroom.act.ready", "准备", "Ready",
            "csroom.act.unready", "取消准备", "Not ready",
            "csroom.act.switch", "换存档", "Change save",
            "csroom.act.kick", "移出", "Kick",
            "csroom.act.leave", "离开房间", "Leave room",
            "csroom.difficulty", "难度：{0}", "Difficulty: {0}",
            "csroom.no_save", "等待房间存档…", "Waiting for the room's save...",
            "csroom.need_ready", "等所有玩家都准备好才能开始", "Everyone must be ready to start",
            "csroom.list.create", "创建房间", "Create room",
            "csroom.list.create.desc", "选一个存档栏位，建一个新房间", "Pick a save slot and open a new room",
            "csroom.list.join", "加入房间", "Join room",
            "csroom.list.join.desc", "输入房间码加入", "Join with a room code",
            "csroom.list.room", "{0}  {1}/4", "{0}  {1}/4",
            "csroom.list.code", "房间码 {0}", "Room code {0}",
            "myrooms.title", "我的房间", "My rooms",
            "myrooms.create", "新建房间", "New room",
            "myrooms.create.desc", "新建一个房间，选好存档后邀请朋友加入。", "Open a new room, pick a save, then invite friends.",
            "myrooms.join", "加入房间", "Join room",
            "myrooms.join.desc", "输入朋友给你的邀请码加入他的房间。", "Join a friend's room with the invite code they gave you.",
            "myrooms.list", "房间列表", "Room list",
            "myrooms.list.desc", "你建过或加入过的房间，选中直接进入。", "Rooms you made or joined; pick one to go in.",
            // T-200: the 我的房间 menu says which server it asks and what it found; a server that does not answer gets a retry row.
            "myrooms.connecting.desc", "正在连接 {0} 的房间服务器，最多等几秒。", "Connecting to the room service of {0}; this takes a few seconds at most.",
            "myrooms.retry", "连不上，再试一次", "Can't connect, try again",
            "myrooms.retry.desc", "连不上 {0} 的房间服务器：服务器可能没开，或者连不到它。按确认再试一次。", "Can't reach the room service of {0}: the server may be down, or the network does not reach it. Confirm to try again.",
            "myrooms.status.rooms", "服务器 {0}：你有 {1} 个房间", "Server {0}: {1} room(s) of yours",
            "myrooms.status.none", "服务器 {0}：还没有你的房间", "Server {0}: no rooms of yours yet",
            "myrooms.status.offline", "服务器 {0}：连不上房间服务器", "Server {0}: can't reach the room service",
            "myrooms.status.other", "另有 {0} 个房间在别的服务器上（到 服务器管理 → 服务器列表 换服务器）", "{0} more room(s) are on other servers (switch in Server management -> Server list)",
            "myrooms.server", "服务器", "Server",
            "myrooms.server_n", "服务器 {0} · {1}", "Server {0} - {1}",
            "myrooms.code", "邀请码", "Invite code",
            "myrooms.join_go", "加入", "Join",
            "myrooms.no_server", "还没有保存服务器，先在服务器管理里添加", "No saved server yet: add one under Manage servers",
            "myrooms.full", "已满", "Full",
            "myrooms.delete_again", "再按一次删除", "Press again to delete",
            "myrooms.key.select", "选择", "Select",
            "myrooms.key.back", "返回", "Back",
            "myrooms.key.confirm", "确认", "Confirm",
            "myrooms.key.switch", "切换服务器", "Switch server",
            "myrooms.key.enter", "进入", "Enter",
            "myrooms.key.delete", "删除", "Delete",
            "csroom.act.enter", "进入", "Enter",
            "csroom.act.open_code", "开放邀请码", "Open invite code",
            "csroom.code", "邀请码 {0}  {1}", "Invite code {0}  {1}",
            "csroom.code_closed", "邀请码 未开放", "Invite code: closed",
            "csroom.room_name", "房间名：{0}", "Room: {0}",
            "csroom.rename", "改名", "Rename",
            "csroom.list.loading", "正在读取房间列表…", "Loading the room list...",
            "csroom.list.none", "这台服务器上还没有房间", "No rooms on this server yet",
            "csroom.list.failed", "读取失败，再读一次", "Couldn't load, try again",   // T-201: the status row (press = read again)
            "csroom.list.failed.noanswer", "服务器没有回应：可能没开，或者连不到它。按确认再读一次；也可以返回换一台服务器。", "The server did not answer: it may be down, or the network does not reach it. Confirm to try again, or go back and pick another server.",
            "csroom.list.failed.old", "服务器的回应读不了：服务器版本可能太旧。按确认再读一次。", "The server's reply could not be read: the server may be too old. Confirm to try again.",
            "csroom.list.failed.address", "找不到这个服务器地址：检查地址，或返回换一台服务器。", "The server address could not be resolved: check it, or go back and pick another server.",
            "csroom.local", "本机", "This PC",   // T-189
            "csroom.remote", "远程", "Online",   // T-189
            "csroom.act.copy_code", "复制邀请码", "Copy code",   // T-189
            "csroom.act.back", "返回", "Back",   // T-189
            "csroom.act.cancel", "取消", "Cancel",   // T-189
            "csroom.act.leave_yes", "确定离开", "Leave",   // T-189
            "csroom.copied", "已复制", "Copied",   // T-189
            "csroom.room_full", "房间已满", "Room full",   // T-189
            "csroom.leave_ask", "确定离开房间并回到主菜单？", "Leave the room and go back to the main menu?",   // T-189
            "csroom.code_minutes", "邀请码开放 5 分钟", "The invite code opens for 5 minutes",   // T-189
            "server.add", "添加服务器", "Add server",
            "server.remove", "删除服务器", "Remove server",
            "server.add.desc", "输入服务器地址和端口，保存到列表", "Enter the server address and port, then save it to the list.",
            "server.remove.desc", "选择要从列表删除的服务器", "Choose a server to remove from the list.",
            "server.pick", "选择此服务器，进入开房或加入", "Select this server, then host or join a room.",
            "server.remove.pick", "按 A 从列表删除此服务器", "Press A to remove this server from the list.",
            "rooms.last", "上次游玩", "Last played",
            "rooms.last.desc", "回到上次玩的房间，直接继续", "Go back to the room you played last and continue.",
            "server.manage", "服务器管理", "Manage servers",
            "server.manage.desc", "添加、删除、查看你的服务器", "Add, remove or look at your servers.",
            "server.list", "服务器列表", "Server list",
            "server.list.desc", "查看已添加的服务器，选一台开房或加入", "See your saved servers. Pick one to host or join a room.",
            "server.none", "还没有添加服务器", "No servers added yet",
            "server.test", "连接测试", "Test connection",
            "server.test.desc", "试着连一下这台服务器的房间服务", "Try the room service of this server.",
            "server.test.running", "测试中…", "Testing...",
            "server.test.ok", "连接成功", "Connected",
            "server.test.fail", "连不上，检查地址和端口", "Could not connect. Check the address and port.",
            "common.ok", "确定", "OK",
            "rooms.list", "房间列表", "Room list",
            "rooms.list.desc", "查看和继续你的房间", "See and continue your rooms.",
            "rooms.join_room", "加入房间", "Join room",
            // rooms / lobby / coop / saves / session / steam
            "rooms.steam_required", "需要 Steam 才能使用房间", "Steam is required to use rooms",
            "rooms.service", "房间服务器", "Room service",
            "rooms.connecting", "连接中…", "Connecting...",
            "rooms.empty", "当前房间为空", "No rooms yet",
            "rooms.empty.desc", "创建一个房间，或用房间码加入朋友的房间", "Create a room, or join a friend's with a room code",
            "rooms.open", "打开", "Open",
            "rooms.create", "新建房间", "Create room",
            "rooms.join_code", "用房间码加入", "Join by code",
            "rooms.continue", "继续", "Continue",
            "rooms.copy_single", "复制到单人存档", "Copy to single player",
            "rooms.level", "等级：", "Level: ",
            "rooms.completion", "完成度：", "Completion: ",
            "rooms.area", "区域：", "Area: ",
            "rooms.seconds", "时长：", "Play time: ",
            "rooms.edit", "编辑", "Edit",
            "rooms.name", "房间名", "Room name",
            "rooms.password_min", "密码（至少 4 位）", "Password (4+ characters)",
            "rooms.choose_save", "选择已有存档", "Choose an existing save",
            "rooms.code", "6 位房间码", "6-character code",
            "rooms.password", "密码", "Password",
            "rooms.join", "加入", "Join",
            "rooms.password_again", "请重新输入房间密码", "Enter the room password again",
            "rooms.confirm", "确认", "Confirm",
            "rooms.choose_slot", "选择存档格", "Choose a save slot",
            "rooms.copied", "已复制到单人存档", "Copied to single player",
            "rooms.err.network", "服务器连不上", "Cannot reach the room service",
            "rooms.err.bad_request", "请检查房间名、房间码和密码", "Check the room name, code and password",
            "rooms.err.old_client", "请更新联机模组", "Please update OriCoop",
            "rooms.err.no_room", "找不到房间", "Room not found",
            "rooms.err.bad_password", "密码不对", "Incorrect password",
            "rooms.err.locked", "密码尝试太多，请稍后再试", "Too many attempts. Try again later",
            "rooms.err.bad_token", "请重新输入密码", "Please enter the password again",
            "rooms.err.not_member", "你不是房间成员", "You are not a member of this room",
            "rooms.err.already_member", "你已在房间中，请输入密码", "You are already a member. Enter the password",
            "rooms.err.forbidden", "不能执行此操作", "Action not allowed",
            "rooms.err.busy", "房间存档正在使用", "This room save is in use",
            "rooms.err.limit", "房间或存档已达上限", "Room or save limit reached",
            "rooms.err.too_large", "存档太大", "Save is too large",
            "rooms.err.solo_off", "请等待伙伴", "Please wait for your partner",
            "rooms.err.no_save", "新建房间暂时需要一个已有存档", "A new room needs an existing save for now",
            "rooms.err.no_slot", "没有空存档格，先删除一个存档", "No empty slot. Delete a save first",
            "rooms.err.no_backup", "本机没有备份", "No local backup",
            "rooms.err.waiting", "伙伴正在开房间，请稍后再试", "Your partner is starting the room. Try again shortly",
            "rooms.err.failed", "操作失败，请稍后重试", "Operation failed. Try again later",
            "rooms.offline", "连不上房间服务器，不能进入房间；可以把本机备份复制到单人存档", "Room service unavailable. You can copy local backups to single player.",
            "rooms.title", "我的房间 — {0}", "My rooms - {0}",
            "rooms.save_count", "{0} ({1} 个存档)", "{0} ({1} saves)",
            "rooms.member", "P{0}: {1}", "P{0}: {1}",
            "rooms.last_played", "最后游玩：{0}", "Last played: {0}",
            "rooms.playing", "{0} 正在玩", "{0} is playing",
            "rooms.copy_named", "复制到单人存档：{0}", "Copy to single player: {0}",
            "rooms.default_name", "{0} 的房间", "{0}'s room",
            "rooms.auto_name", "{0} 的房间 {1}", "{0}'s room {1}",   // no # (MessageBox markup, T-136)
            "rooms.auto_name.desc", "房间名自动生成", "The room name is generated automatically.",
            "rooms.password_dial", "4-8 位数字密码\n上下转动 · 左右移动 · 数字键输入 · X 删除 · Y 粘贴\nA / Enter 完成 · B / Esc 取消", "4-8 digit password\nUp/Down: turn - Left/Right: move - digits: type - X: delete - Y: paste\nA / Enter: done - B / Esc: cancel",
            "rooms.share", "{0} — 把房间码和密码发给伙伴", "{0} - Send the code and password to your partner",
            "rooms.save_version", "{0} 版本 {1}", "{0} v{1}",
            "rooms.seat_seconds", "P{0} 时长：{1}", "P{0} seconds: {1}",
            "rooms.saved", "一起：{0}  最后保存：{1} {2}", "Together: {0}  Saved: {1} {2}",
            "rooms.edit_hint", "上下换字 · 左右移动 · X 删除 · Y 粘贴 · A 完成（{0}）", "Up/Down: character; Left/Right: cursor; X: delete; Y: paste; A: done ({0})",
            "rooms.create_slot", "用存档 {0} 创建{1}", "Create with save {0}{1}",
            "rooms.slot_name", "存档 {0}", "Save {0}",
            "rooms.slot_summary", "{0} {1}", "{0} {1}",
            "rooms.overwrite", "覆盖存档 {0}？", "Overwrite save {0}?",
            "rooms.conflict", "房间存档已被别处更新，本次进度只保存在本机", "The room save was updated elsewhere. This session's progress is saved on this PC only.",
            "coop.cancelled", "已取消", "Cancelled",
            "coop.p1_keyboard", "P1 现在只用键盘鼠标", "P1 now uses keyboard/mouse only",
            "coop.p1", "<color=#7fd0ff>P1</color>", "<color=#7fd0ff>P1</color>",
            "coop.p2", "<color=#ffd166>P2</color>", "<color=#ffd166>P2</color>",
            "coop.unused", "<color=#888888>未使用</color>", "<color=#888888>Unused</color>",
            "coop.settings_hint", "F8 / 长按手柄返回键：双人设置", "F8 / Hold Back: Co-op settings",
            "coop.choose_controllers", "双人 · 选择手柄", "2 players: choose controllers",
            "coop.settings", "双人设置", "Co-op settings",
            "pause.multiplayer_settings", "多人游戏设置", "Multiplayer settings",
            "pause.room", "房间管理", "Room",   // T-189
            "coop.local_seat", "P{0}: {1}", "P{0}: {1}",
            "coop.change_device", "{0}    [更改]", "{0}    [Change device]",
            "coop.add_local", "加入本机玩家", "Add local player",
            "coop.remove_local", "移除 P{0}", "Remove P{0}",
            "coop.online_input", "键盘鼠标和所有手柄", "Keyboard/mouse + all controllers",
            "coop.choose_local_pad", "请在新玩家的手柄上按任意键（Esc 取消）", "Press any button on the new player's controller (Esc to cancel)",
            "coop.choose_extra_join_device", "按新玩家的手柄或键盘任意键（Esc 取消）", "Press a button on the new player's controller or a key on the keyboard (Esc to cancel)",
            "coop.choose_extra_device", "P{0}：按手柄或键盘任意键（Esc 取消）", "P{0}: press a controller button or a keyboard key (Esc to cancel)",
            "coop.seat_keyboard", "P{0} 现在只用键盘鼠标", "P{0} now uses keyboard/mouse only",
            "coop.players_count", "{0} 人", "{0} players",
            "coop.input_failed", "无法更改，请重试", "Could not change the input. Try again.",
            "coop.controller_hint", "控制器（按下任意键，对应那一行会亮）", "Controllers (press a button to see which is which)",
            "coop.connected", "已连接", "Connected",
            "coop.not_connected", "<color=#666666>未连接</color>", "<color=#666666>Not connected</color>",
            "coop.keyboard", "键盘鼠标  →  <color=#7fd0ff>P1</color>", "Keyboard/mouse -> <color=#7fd0ff>P1</color>",
            "coop.cutscene_hint", "剧情“两人都可以”：P2 碰到剧情触发点时，会先把 P1 带到 P2 身边再开始。", "Either player: when P2 triggers a cutscene, P1 is brought to P2 first.",
            "coop.keyboard_only", "只用键盘鼠标", "Keyboard/mouse only",
            "coop.choose_p1", "P1：请在 P1 的手柄上按任意键，或按键盘任意键 = 只用键鼠（Esc 取消）", "P1: press any button on P1's controller, or any keyboard key for keyboard/mouse only (Esc to cancel)",
            "coop.choose_p2", "P2：请在 P2 的手柄上按任意键（Esc 取消）", "P2: press any button on P2's controller (Esc to cancel)",
            "coop.either", "两人都可以", "Either player",
            "coop.p1_only", "只有 P1", "Player 1 only",
            "coop.start", "<b>▶ 开始双人游戏</b>  （开始键）", "<b>Start 2 players</b>  (Start)",
            "coop.back", "◀ 返回 (Esc / B)", "Back (Esc / B)",
            "coop.add_p2", "让 P2 加入", "Add player 2",
            "coop.remove_p2", "移除 P2", "Remove player 2",
            "coop.single", "单人", "Single player",
            "coop.two_players", "双人", "2 players",
            "coop.close", "关闭 (F8 / Esc / B)", "Close (F8 / Esc / B)",
            "coop.open_logs", "打开日志文件夹", "Open log folder",
            "coop.logs_opened", "已打开日志文件夹（反馈问题时请附上里面的日志）", "Log folder opened (attach its logs when you report a problem)",
            "coop.logs_failed", "无法打开日志文件夹：{0}", "Couldn't open the log folder: {0}",
            "build.version", "OriCoop 版本 {0}", "OriCoop version {0}",
            "coop.play_how", "怎么玩？", "How do you want to play?",
            "coop.play_hint", "↑↓ / 摇杆选择，A / Enter 确定。之后可按 F8 或长按手柄返回键更改", "Up/Down to pick, A / Enter to confirm. Change later with F8 or hold Back.",
            "coop.assigned", "手柄 {0} 现在是 P{1}", "Controller {0} is now P{1}",
            "coop.controller_state", "手柄 {0}：{1}", "Controller {0}: {1}",
            "coop.keyboard_pad", "键盘鼠标  →  <color=#7fd0ff>P1</color>（和手柄 {0} 一起）", "Keyboard/mouse -> <color=#7fd0ff>P1</color> (with controller {0})",
            "coop.p2_disconnected", "<color=#ff8080>P2 的手柄 {0} 没有连接</color>", "<color=#ff8080>P2's controller {0} is not connected</color>",
            "coop.controller", "手柄 {0}", "Controller {0}",
            "coop.p1_uses", "P1 用：{0}    [选择]", "P1 uses: {0}    [Choose]",
            "coop.p2_uses", "P2 用：{0}    [选择]", "P2 uses: {0}    [Choose]",
            "coop.cutscenes", "剧情触发：{0}", "Story cutscenes: {0}",
            "coop.mode", "模式：{0}    [更改]", "Mode: {0}    [Change]",
            "coop.single_desc", "<b>单人</b>\n<size={0}>键盘鼠标和手柄都控制 Ori</size>", "<b>Single player</b>\n<size={0}>Keyboard/mouse and controller both control Ori</size>",
            "coop.two_desc", "<b>双人</b>\n<size={0}>下一步：给两位玩家选择手柄</size>", "<b>2 players</b>\n<size={0}>Next: choose each player's controller</size>",
            "saves.copy_single", "从单机存档复制一份过来当联机存档", "Copy a single-player save as a co-op save",
            "saves.copy_binding", "RB / F6", "RB / F6",
            "saves.copy_pick", "选择单机存档", "Choose a single-player save",
            "saves.copy_empty", "没有单机存档可复制", "No single-player saves to copy",
            "saves.copy_full", "没有空联机存档位，请先删除一个联机存档", "No empty co-op slot. Delete a co-op save first",
            "saves.copy_done", "已复制到联机存档 {0}", "Copied to co-op slot {0}",
            "saves.copy_failed", "复制失败", "Copy failed",
            "saves.check_timeout", "伙伴未回应存档比对，以主机存档为准", "No save check from partner: using host save",
            "saves.upload_timeout", "未收到伙伴进度，以主机存档为准", "Partner progress not received: using host save",
            "saves.new", "新的联机存档：以主机进度开始", "New co-op save: starting from the host's progress",
            "saves.partner_no_slot", "伙伴没有空存档位，进度只保存在主机", "Partner has no empty slot: progress is kept on the host only",
            "saves.match", "联机存档一致", "Co-op saves match",
            "saves.cancelled", "已取消联机，两份存档都未改动", "Online play cancelled: neither save was changed",
            "saves.invalid_progress", "伙伴进度无效，以主机存档为准", "Partner progress invalid: using host save",
            "saves.load_failed", "无法载入伙伴进度，以主机存档为准", "Could not load partner progress: using host save",
            "saves.using_partner", "已采用伙伴的进度", "Using the partner's progress",
            "saves.waiting_choice", "两份联机存档不同，等待主机选择…", "Co-op saves differ: waiting for the host to choose...",
            "saves.host_cancelled", "主机取消了联机，你的存档未改动", "Host cancelled online play: your save is unchanged",
            "saves.no_slot", "没有空存档位：联机进度不会保存在本机", "No empty save slot: co-op progress is not kept on this PC",
            "saves.host_no_slot", "你现在是主机，但没有空存档位：联机进度不会保存在本机", "You are now the host, but no save slot is free: co-op progress is not saved on this PC",
            "saves.new_game", "新游戏", "New game",
            "saves.saved_slot", "联机进度保存在存档 {0}（与 {1}）", "Co-op progress saved to slot {0} (with {1})",
            "saves.diverged", "联机存档出现分歧", "Your co-op saves have diverged",
            "saves.host", "主机（你）", "Host (you)",
            "saves.partner", "伙伴 {0}", "Partner {0}",
            "saves.use_host", "以主机进度为准", "Use the host's progress",
            "saves.use_partner", "以伙伴进度为准", "Use the partner's progress",
            "saves.cancel", "取消", "Cancel (leave online play)",
            "saves.backup_note", "被替换的一份会先备份；选定的进度会写回双方", "The replaced copy is backed up first; the chosen progress is written to both PCs",
            "saves.prompts", "<color=#91C95B>(A)</color> 确定    ↑↓ 选择    <color=#D76659>(B)</color> 取消", "<color=#91C95B>(A)</color> Confirm    Up/Down: select    <color=#D76659>(B)</color> Cancel",
            "saves.both_changed", "来自同一份联机存档，之后你们各自单独玩过", "Same co-op save, but you both played alone since your last session together",
            "saves.host_changed", "来自同一份联机存档，之后主机单独玩过", "Same co-op save, but the host played alone since your last session together",
            "saves.partner_changed", "来自同一份联机存档，之后伙伴单独玩过", "Same co-op save, but the partner played alone since your last session together",
            "saves.sync_incomplete", "来自同一份联机存档，上次同步未完成", "These copies share a co-op save, but the last sync did not finish",
            "saves.changed", "单独玩过", "Played alone",
            "saves.unchanged", "未改动", "Unchanged",
            "saves.slot_tag", "  联机·与 {0}", "  Co-op - {0}",
            "saves.slot_label", "*{0}:* {1} - {2}%{3}", "*{0}:* {1} - {2}%{3}",
            "saves.summary", "{0} · {1}% · {2:D2}:{3:D2}:{4:D2}", "{0} - {1}% - {2:D2}:{3:D2}:{4:D2}",
            "session.paused", "伙伴暂停了游戏", "Your partner paused the game",
            "session.failed", "主机存档同步失败", "Couldn't load the host's save",
            "session.failed_help", "你仍在联机中，但进度没有和主机同步。建议回到标题后重新加入。", "You are still connected, but your progress is not the host's. Rejoining from the title is recommended.",
            "session.resume_help", "伙伴继续后游戏会自动恢复", "The game resumes when they unpause",
            "session.both_paused", "联机中：双方都已暂停", "Online: both players are paused",
            "session.continue", "按任意键继续", "Press any key to continue",
            "session.waiting", "正在等待主机存档…", "Waiting for the host's save...",
            "session.receiving", "正在同步主机存档…", "Receiving the host's save...",
            "session.loading", "正在载入主机存档…", "Loading the host's save...",
            "session.waiting_help", "主机进入游戏后会自动开始", "This starts once the host is in the game",
            "session.loading_help", "完成后你会出现在主机身边", "You'll appear next to the host when it's done",
            "session.progress", "{0}%", "{0}%",
            "session.waiting_prologue", "主机正在观看开场剧情…", "The host is watching the opening...",
            "session.waiting_prologue_help", "开场剧情结束后会自动加入", "You'll join when the opening ends",
            "session.catching_up", "正在跟上主机的进度…", "Catching up with the host...",
            "session.catching_up_help", "很快就能和主机一起观看", "You'll be watching with the host in a moment",
            "prologue.skip_p1_only", "只有 P1 可以跳过序章", "Only P1 can skip the prologue",
            "guest.host_left", "主机已离开", "The host left",
            "guest.ended", "联机已断开", "Online play ended",
            "guest.no_save", "没有收到主机存档，请确认主机已进入游戏", "The host's save didn't arrive: make sure the host is in the game",
            "guest.no_screen", "找不到存档界面，无法载入主机存档", "Couldn't load the host's save (no save-slot screen)",
            "guest.load_failed", "载入主机存档失败", "Couldn't load the host's save",
            "guest.still_connected", "{0}（仍保持连接）", "{0} (still connected)",
            "roles.tag", "P{0}", "P{0}",
            "roles.host", "主机", "Host",
            "roles.partner", "伙伴", "Partner",
            "roles.label", "{0} · {1}", "{0} - {1}",
            "roles.named_label", "{0} · {1} · {2}", "{0} - {1} - {2}",
            "display.restart", "显示修复已开启：请重启一次游戏，画面就会按屏幕的真实分辨率清晰显示。", "Display fix: restart the game once to get sharp, true-resolution graphics.",
            "steam.failed", "没能加入好友的 Steam 大厅，请让好友重新邀请", "Could not join your friend's Steam lobby. Ask them to invite you again",
            "steam.friend", "好友", "your friend",
            "steam.invite_end", "收到 {0} 的 Steam 邀请：你正在和别人联机。回到标题画面会结束这次联机，然后自动加入 {0}", "Steam invite from {0}: you are playing online with someone. Return to the title screen to end that session; then you join {0} automatically",
            "steam.invite_menu", "收到 {0} 的 Steam 邀请：回到主菜单就会自动加入", "Steam invite from {0}: return to the main menu to join",
            "steam.starting", "已连接 {0}，正在进入游戏…", "Connected to {0}. Starting the game...",
            "steam.receiving", "已连接 {0}，正在接收主机存档…", "Connected to {0}. Receiving the host's save...",
            "steam.retry", "加入 {0} 的 Steam 大厅出错，正在重试{1}", "Problem joining {0}'s Steam lobby, retrying{1}",
            "steam.joining", "正在加入 {0} 的 Steam 大厅{1}", "Joining {0}'s Steam lobby{1}",
            "lobby.enter_address", "请输入地址", "Please enter an address",
            "lobby.address_copied", "已复制地址", "Address copied",
            "lobby.cancel", "取消", "Cancel",
            "lobby.delete", "删除", "Delete",
            "lobby.connect", "连接", "Connect",
            "lobby.creating", "正在创建房间…", "Creating room...",
            "lobby.direct_mode", "局域网主机", "LAN host",
            "lobby.your_address", "你的地址", "Your address",
            "lobby.direct_hint", "朋友不在同一局域网时，需要自己用 Tailscale 等组网", "Not on the same LAN? Set up your own tunnel or VPN (e.g. Tailscale).",
            "lobby.steam_friends", "Steam 好友联机", "Steam friends",
            "lobby.steam_ready", "Steam 就绪，按下方按钮或功能键 5 邀请好友", "Steam ready: invite friends below or press F5",
            "lobby.steam_preparing", "正在准备 Steam 大厅…", "Preparing Steam lobby...",
            "lobby.join_anytime", "伙伴随时可以加入", "Your partner can join any time",
            "lobby.prompts", "<color=#91C95B>(A)</color> 确定    <color=#D76659>(B)</color> 返回", "<color=#91C95B>(A)</color> Confirm    <color=#D76659>(B)</color> Back",
            "lobby.join_room", "加入房间", "Join room",
            "lobby.waiting_partner", "等待伙伴加入…", "Waiting for your partner...",
            "lobby.connecting", "连接中…", "Connecting...",
            "lobby.direct", "局域网", "LAN",
            "lobby.steam", "Steam 好友", "Steam friends",
            "lobby.title", "联机 · {0}", "Online - {0}",
            "lobby.host", "开房", "Host",
            "lobby.wait_invite", "等待邀请", "Wait for an invite",
            "lobby.partner_joined", "{0} 已加入！", "{0} joined!",
            "lobby.copy", "复制", "Copy",
            "lobby.retry", "再试一次", "Retry",
            "lobby.accept_invite", "在 Steam 里接受好友的邀请", "Accept your friend's invite in Steam",
            "lobby.connecting_stage", "正在连接", "Connecting",
            "lobby.preparing", "正在准备", "Preparing",
            "lobby.err.timeout", "连接超时", "Connection timed out",
            "lobby.err.no_room", "找不到这个房间", "Room not found",
            "lobby.err.full", "房间已满", "Room is full",
            "lobby.err.no_address", "找不到这个地址", "Cannot find that address",
            "lobby.err.steam", "Steam 出错", "Steam error",
            "lobby.err.failed", "连接失败", "Connection failed",
            // hud / revive
            "hud.reconnected", "已重新连接", "Reconnected",
            "hud.joined.host", "P2 伙伴已加入 · 你是 P1 主机", "P2 joined - You are P1, the host",
            "hud.joined.partner", "已加入主机的游戏 · 你是 P2", "Joined P1's game - You are P2",
            "hud.version.mismatch", "版本不一致 · 本机：{0} / 伙伴：{1}", "Version mismatch - Yours: {0} / Partner's: {1}",
            "hud.partner.left", "P2 伙伴已离开 · 房间仍然开着", "P2 left - The room is still open",
            "hud.signal.lost", "信号中断 {0} 秒 · 正在恢复连接", "Signal lost for {0}s - Reconnecting",
            "hud.role.host", "{0} 主机", "{0} Host",
            "hud.role.partner", "{0} 伙伴", "{0} Partner",
            "hud.watching", "正在观看伙伴的过场", "Watching partner's cutscene",
            "hud.spectate", "正在观看：{0}  A/D 或手柄左右键换人", "Spectating {0}  A/D or D-pad left/right",
            "hud.partner.menu", "伙伴在菜单中", "Partner is in the menu",
            "hud.room", "房间码  {0}", "Room code  {0}",
            "hud.invite", "按 {0} 邀请好友", "Press {0} to invite a friend",
            "hud.address", "地址  {0}", "Address  {0}",
            "hud.solo.reconnecting", "单人中 · 正在重连", "Playing solo - Reconnecting",
            "hud.disconnect.title", "与主机断开", "Lost connection to host",
            "hud.disconnect.reconnecting", "正在重连 {0}", "Reconnecting {0}",
            "hud.disconnect.solo", "继续单人", "Keep playing alone",
            "hud.disconnect.back", "回到标题", "Back to title",
            "hud.disconnect.note", "单人进度不会保存", "Solo progress is not saved",
            "hud.disconnect.prompt", "{0} 确定    {1} 返回", "{0} Confirm    {1} Back",
            "revive.hold", "按住 {0} 复活", "Hold {0} to revive",
            "revive.again", "被打断了，重新按住 {0}", "Interrupted - Hold {0} again",
            "revive.mouse", "鼠标左键", "the left mouse button",
            "revive.self_hold", "按住 {0} 回到上一个灵魂连接或存档点复活", "Hold {0} to revive at the last soul link or save point",
            "revive.link_made", "{0} 建立了新的灵魂连接", "{0} made a new soul link",
            "revive.death_save", "你在有伙伴死亡时创建了存档，生命值已自动减半", "You saved while a partner was down: half your health was taken",
            "escape.mash", "连按 {0} 到伙伴身边复活", "Mash {0} to come back beside a teammate",
            "escape.through", "伙伴通过了，全员前进", "A teammate made it through - the team moves on",
            "gate.force", "长按 {0} 强制触发", "Hold {0} to force it",
            "gate.wait", "需全员都到才能推进（{0}/{1}）", "Everyone must be here to continue ({0}/{1})",
            "pause.by", "{0} 暂停了", "{0} paused the game",
            "session.resumed", "有玩家继续了游戏", "A player resumed the game",
            // title / mode / lobby
            "title.single", "单人游玩", "Single Player",
            "title.coop", "多人游玩", "Co-op",
            "title.local", "本地同屏", "Local Co-op",
            "title.local.desc", "两个人在同一台电脑上一起玩，选好存档后设置手柄", "Two players on this PC. Choose a save, then set up your controllers.",
            "title.direct.desc", "一位玩家当主机，其他玩家在同一局域网（同一个路由器）输入主机的 IP 地址", "One player hosts; the others on the same WiFi or LAN enter the host's IP address.",
            "title.steam.desc", "通过 Steam 邀请好友，不用开端口，不用知道 IP", "Invite a friend through Steam. No ports to open, no IP address needed.",
            "title.slots.full", "存档位都满了：选一个存档位保存联机进度（原存档会先备份）", "All save slots are full. Choose a slot for your co-op progress. Its current save will be backed up first.",
            "mode.single", "单人", "Single Player",
            "mode.single.desc", "一个人玩，键盘鼠标和手柄都控制 Ori", "Play solo. Keyboard, mouse and controller all control Ori.",
            "mode.local", "本地同屏", "Local Co-op",
            "mode.local.desc", "同一台电脑两个人玩，下一步选择手柄", "Two players on this PC. Next, choose each player's controller.",
            "mode.online", "联机", "Online Co-op",
            "mode.online.desc", "两台电脑一起玩", "Two PCs, each with its own game.",
            "mode.direct", "局域网联机", "LAN Play",
            "mode.direct.desc", "一位玩家当主机，其他玩家在同一局域网用 IP 地址加入", "One player hosts; the others on the same LAN join by IP address.",
            "mode.steam", "Steam 好友", "Steam Friends",
            "mode.steam.desc", "通过 Steam 邀请好友", "Invite a friend through Steam.",
            "lobby.host.room.desc", "你是 P1 主机，创建房间，把房间码发给伙伴", "You are P1, the host. Create a room and send its code to your partner.",
            "lobby.host.direct.desc", "你是 P1 主机，等伙伴用你的 IP 地址连进来", "You are P1, the host. Your partner connects to your IP address.",
            "lobby.host.steam.desc", "你是 P1 主机，创建 Steam 大厅，邀请好友", "You are P1, the host. Open a Steam lobby and invite a friend.",
            "lobby.join", "加入", "Join",
            "lobby.join.room.desc", "你是 P2，输入主机的房间码", "You are P2. Enter the host's room code.",
            "lobby.join.direct.desc", "你是 P2，输入主机的 IP 地址", "You are P2. Enter the host's IP address.",
            "lobby.join.steam.desc", "你是 P2，等待并接受主机的 Steam 邀请", "You are P2. Wait for and accept the host's Steam invite.",
            "lobby.rooms", "我的房间", "My rooms",
            "lobby.rooms.desc", "继续保存在服务器的房间存档", "Continue a room save stored on the server.",
            "lobby.edit.digits", "上下换数字 · 左右移动 · X 删除\nY 粘贴 · A 完成", "Up/Down: digit - Left/Right: move - X: delete - Y: paste - A: done",
            "lobby.ip.desc", "伙伴的 IP 地址，4 段，每段 0–255：按 A 输入，也可以直接打字", "Your partner's IP address has 4 parts, each from 0 to 255. Press A to edit, or just type.",
            "lobby.port.desc", "伙伴的端口，1–65535，一般不用改（默认 {0}）", "Your partner's port, from 1 to 65535. Usually you can leave the default ({0}).",
            "lobby.connect.back.desc", "连接到伙伴 · B 返回", "Connect to your partner - B: back",
            "lobby.room.code", "房间码", "Room code",
            "lobby.ip", "IP 地址", "IP address",
            "lobby.room.legacy", "旧版字母房间码：直接按字母键", "Older letter room code: just press the letter keys",
            "lobby.dial.help","↑↓ 转动 · ←→ 换一位 · 数字键直接输入\nA / Enter 完成 · B / Esc 取消", "Up/Down: turn - Left/Right: next digit - digit keys type\nA / Enter: done - B / Esc: cancel",
            "lobby.edit.chars", "上下换字 · 左右移动 · X 删除\nY 粘贴 · A 完成", "Up/Down: change - Left/Right: move - X: delete - Y: paste - A: done",
            "lobby.entry.desc", "{0}：按 A 输入，也可以直接用键盘打字", "{0}: press A to edit, or just type.",
            "lobby.connect.desc", "连接到伙伴", "Connect to your partner.",
            "lobby.creating.desc", "正在连接服务器", "Contacting the server.",
            "lobby.room.copy.desc", "房间码：按 A 复制，发给伙伴", "Press A to copy the room code and send it to your partner.",
            "lobby.address.copy.desc", "你的地址：按 A 复制。伙伴不在同一局域网时，需要自己用 Tailscale 等组网", "Press A to copy your address. Partners outside your LAN need their own tunnel or VPN (e.g. Tailscale).",
            "lobby.invite", "邀请好友", "Invite Friends",
            "lobby.invite.desc", "打开 Steam 好友列表发邀请", "Open the Steam friends list to send an invite.",
            "lobby.steam.preparing", "正在准备 Steam 大厅…", "Preparing the Steam lobby...",
            "lobby.partner", "伙伴", "Your partner",
            "lobby.partner.joined", "P2 {0}已加入，开始吧", "P2 {0} joined. Let's go!",
            "lobby.start.ready", "现在就能进游戏，伙伴随时可以加入", "You can start now. Your partner can join any time.",
            "lobby.start.preparing", "正在准备…", "Getting ready...",
            "lobby.start", "开始游戏", "Start Game",
            "lobby.host.cancel.desc", "关闭房间，回到上一页", "Close the room and go back.",
            "lobby.connected", "已连接", "Connected",
            "lobby.connected.desc", "正在进入游戏…", "Starting the game...",
            "lobby.steam.waiting", "等待好友邀请", "Waiting for a friend's invite",
            "lobby.steam.accept", "让好友在他的房间里点“邀请好友”，再到 Steam 里接受邀请；也可以在 Steam 好友列表里对好友点“加入游戏”", "Ask your friend to press Invite a friend in their room, then accept the invite in Steam; or use Join Game on your friend in the Steam friends list.",
            "lobby.steam.joining", "正在加入好友的 Steam 房间…", "Joining your friend's Steam room...",
            "steam.overlay.off", "Steam 界面没有开启，邀请框可能打不开。让好友在 Steam 好友列表里对你点“加入游戏”", "The Steam overlay is off, so the invite window may not open. Ask your friend to use Join Game on you in the Steam friends list.",
            "lobby.room.found", "找到房间，正在建立连接", "Room found. Establishing a connection.",
            "lobby.connecting.target", "正在连接 {0}", "Connecting to {0}",
            "lobby.connecting_bare", "连接中", "Connecting",
            "lobby.join.cancel.desc", "停止连接，回到上一页", "Stop connecting and go back.",
            "lobby.save.note", "\n联机进度会自动保存到你的电脑", "\nCo-op progress is saved on your PC automatically.",
            "lobby.error.timeout", "连接超时", "Connection timed out",
            "lobby.error.noanswer", "服务器没有回应：可能没开，或者连不到它", "The server did not answer: it may be down, or the network does not reach it",   // T-201
            "lobby.error.noanswer.host", "主机没有回应：对方可能已经关掉房间，或者两边连不通", "The host did not answer: the room may be closed, or the two networks cannot reach each other",
            "lobby.error.room", "找不到这个房间", "Room not found",
            "lobby.error.full", "房间已满", "Room is full",
            "lobby.error.address", "找不到这个地址", "Cannot find that address",
            "lobby.error.steam", "Steam 出错", "Steam error",
            "lobby.error.failed", "连接失败", "Connection failed",
            "lobby.ip.enter", "输入 IP 地址", "Enter IP address",
            "lobby.server.enter", "输入服务器地址", "Enter server address",
            "lobby.port.prefix", "端口 ", "Port ",
            "lobby.server.required", "先选择服务器", "Choose a server first",
            "lobby.room.copied", "已复制房间码", "Room code copied",
            "lobby.address.copied", "已复制地址", "Address copied",
            "lobby.room.required", "请输入 6 位房间码", "Enter the 6-character room code",
            "lobby.ip.required", "IP 地址要填满 4 段，每段 0–255", "Fill in all 4 parts of the IP address (0-255 each)",
            "lobby.port.required", "端口要在 1–65535 之间", "The port must be 1-65535",
            "lobby.ip.clipboard", "剪贴板里不是 IP 地址", "The clipboard doesn't hold an IP address",
            "lobby.port.range", "端口范围是 1–65535", "The port range is 1-65535",
            "lobby.ip.range", "IP 每段只能是 0–255", "Each part of an IP address must be 0-255",
            "lobby.port.zero", "端口不能以 0 开头", "A port can't start with 0",
            "common.save", "保存", "Save",
            "common.back.desc", "回到上一页", "Back to the previous page.",
            "common.cancel", "取消", "Cancel",
            "common.retry", "再试一次", "Retry",
            "cserver.addr", "服务器地址", "Server address",
            "cserver.port", "端口", "Port",
            "cserver.addr.invalid", "服务器地址无效", "Invalid server address",
            "cserver.addr.save_failed", "服务器地址保存失败", "Could not save server address",
            "cserver.edit.desc", "按 A 设置，也可以用键盘输入", "Press A to set, or use the keyboard.",
            "cserver.save.desc", "保存服务器地址和端口", "Save the server address and port.",
            "cserver.typing.desc", "上下换字 · 左右移动 · X 删除\nA 或 Enter 保存 · B 或 Esc 取消", "Up/Down: change - Left/Right: move - X: delete\nA or Enter: save - B or Esc: cancel",
        };
        // END STRINGS
    }

}
