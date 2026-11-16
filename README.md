# OriCoop

Fan-made co-op mod for *Ori and the Blind Forest: Definitive Edition* (BepInEx 5 plugin, C# 5 / .NET 3.5, Unity 5.3).
《奥日与黑暗森林：终极版》的玩家自制合作模组（BepInEx 5 插件）。

| | |
|---|---|
| Version / 版本 | **0.2.0** |
| Local co-op / 同屏双人 | Works: second Ori on a second controller, shared camera, revive. 可用 |
| Online, 2 players / 双人联机 | Works (experimental): LAN / direct IP, room code through a relay, Steam friends. 可用（实验性） |
| Online, 4 players / 四人联机 | **In progress.** 开发中 |
| C-server (authority server) / 权威服务器 | **In progress**, not used by the game yet. 开发中，游戏还没有接入 |
| Servers / 服务端 | [oricoop-relay](https://github.com/Venompool888/oricoop-relay) (tunnel relay, used today) · [oricoop-server](https://github.com/Venompool888/oricoop-server) (C-server prototype) |

Not affiliated with or endorsed by Moon Studios, iam8bit or Microsoft. This repository contains **no game files, no
game art, no game audio and no decompiled game code**. You need your own copy of the game.
本项目与 Moon Studios、Microsoft 无关。仓库里**没有任何游戏文件、美术、音频或反编译代码**，需要你自己拥有正版游戏。

---

## 中文

### 这是什么

OriCoop 给单人游戏《奥日与黑暗森林：终极版》加上合作模式：

- **同屏双人**：第二个手柄控制第二个奥日（颜色不同），镜头跟随两人，倒下后可以被队友救起。
- **双人联机**：主机的存档为准，访客加入后自动收到存档、出现在主机身边。同步位置、动画、能力、敌人、机关、剧情过场和进度。
  连接方式：局域网 / IP 直连、服务器中继（6 位房间码，不用开端口）、Steam 好友邀请。
- **四人联机** 和 **C-server（权威服务器）** 正在开发，还不能玩。

### 安装（玩家）

1. 在 Steam 装好游戏，找到游戏目录（`oriDE.exe` 所在的文件夹；Steam 里右键游戏 →「管理」→「浏览本地文件」）。
2. 拿到一个 OriCoop 套件 zip（BepInEx + `OriCoop.dll`，由 `src\make-online-kit.ps1` 生成），解压到游戏目录。
   联机的两边必须用同一次构建，按 **F6** 能看到版本和连接状态。
3. 启动游戏，标题菜单选 **多人游玩**，再选连接方式。详细步骤见 [TWO-PC-TEST.md](TWO-PC-TEST.md)（中文）。

### 联机服务器

- **局域网 / 直连**：不需要服务器，主机开放 UDP 47011（或用 Tailscale / ZeroTier 这类虚拟局域网）。
- **服务器中继**：需要一台中继服务器。本仓库**不附带任何公共服务器地址**，请自己部署
  [oricoop-relay](https://github.com/Venompool888/oricoop-relay)（一台小 VPS 就够，开放 UDP 47020），
  然后在游戏里或用游戏目录里的 `OriCoop-Relays.html` 填上 `你的服务器IP:47020`。
- **Steam 好友**：两边都要通过 Steam 启动游戏。

### 自己编译

需要：Windows、你自己的游戏副本、BepInEx 5（装进这个游戏副本）、Windows 自带的 .NET Framework 4.x 编译器
（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，不需要 Visual Studio）。

1. 把游戏复制一份到仓库下的 `game\`（或用 `-Game` 指定别的目录），在里面装好 BepInEx 5，运行一次游戏让它生成文件夹。
   编译引用的是这个副本里的 `oriDE_Data\Managed\*.dll` 和 `BepInEx\core\*.dll`，这些文件不在仓库里，也不要提交。
2. 编译并安装到副本：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1            # 或 -Game "D:\Games\Ori"、-NoInstall
   ```
3. 生成给别的电脑用的套件：`powershell -File src\make-online-kit.ps1`，输出在 `dist\`。

可选资源（缺少时照样能编译和运行）：

- `src\assets\zh-glyphs\zh_glyphs.png/.txt`：中文字形图集，用开源字体霞鹜文楷渲染（见 [THIRD-PARTY.md](THIRD-PARTY.md)）。
  游戏自带的中文字库缺字，有了它中文界面才完整。重新生成见 `src\assets\zh-glyphs\README.md`，需要先从你自己的游戏里导出字体表。
- 倒下标记图片：由 `src\assets\dead-marker\make-dead-marker.py` 从你自己游戏里的地图贴图生成（图片来自游戏，所以不放在仓库）。
- `zh_extra.txt`（游戏中文缺失文本的补译）和 `zh_terms.txt`（官方中文术语修正）：包含游戏原文，不公开。

`src\run-*.ps1` 和 `src\test-launch.ps1` 是自动化测试脚本，会在隔离的游戏副本里启动真实的游戏客户端；
它们默认仓库在 `C:\OriCoop`，用之前请看脚本开头的说明。

### 致谢

- [BepInEx](https://github.com/BepInEx/BepInEx) 和 [Harmony](https://github.com/pardeike/Harmony)。
- [霞鹜文楷 LXGW WenKai](https://github.com/lxgw/LxgwWenKai)（SIL OFL 1.1）。
- Moon Studios 做了这么好的游戏。
- 代码大部分由 AI 编程助手在作者指导下编写和测试。

许可证：MIT（见 [LICENSE](LICENSE)）。

---

## English

### What it is

OriCoop adds co-op to the single-player game *Ori and the Blind Forest: Definitive Edition*:

- **Local co-op**: a second controller drives a second, recoloured Ori; shared camera; downed players can be revived.
- **Online, 2 players**: the host's save is the truth. A guest who joins receives the save and appears next to the host.
  Positions, animation, abilities, enemies, world objects, cutscenes and progress are synced. Transports: LAN / direct
  IP, room code through a relay server (no port forwarding), Steam friend invites.
- **4-player online** and the **C-server** (authority server) are **in progress** and not playable yet.

### Install (players)

1. Install the game (Steam) and open its folder (the one with `oriDE.exe`; Steam: right-click → Manage → Browse local files).
2. Get an OriCoop kit zip (BepInEx + `OriCoop.dll`, built by `src\make-online-kit.ps1`) and unzip it into that folder.
   Both PCs must run the same build; **F6** shows the version and connection status.
3. Start the game, choose **Co-op** on the title menu, then a connection type. Step by step guide (Chinese):
   [TWO-PC-TEST.md](TWO-PC-TEST.md).

### Servers

- **LAN / direct**: no server; the host opens UDP 47011 (or use a virtual LAN such as Tailscale or ZeroTier).
- **Relay (room codes)**: you need a relay server. **No public server address ships with this repo**: run your own
  [oricoop-relay](https://github.com/Venompool888/oricoop-relay) (a small VPS is enough, open UDP 47020), then enter
  `your-server-ip:47020` in the game or in `OriCoop-Relays.html` in the game folder.
- **Steam friends**: both players start the game through Steam.

### Build

Needs Windows, your own copy of the game, BepInEx 5 installed into that copy, and the .NET Framework 4.x compiler that
ships with Windows (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`; no Visual Studio needed).

1. Copy the game to `game\` under the repo (or pass `-Game <dir>`), install BepInEx 5 into it and run it once. The
   build references `oriDE_Data\Managed\*.dll` and `BepInEx\core\*.dll` from that copy; they are not in this repo and
   must never be committed.
2. Build and install into that copy:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File src\build.ps1            # or -Game "D:\Games\Ori", -NoInstall
   ```
3. Make a kit for other PCs: `powershell -File src\make-online-kit.ps1` (output in `dist\`).

Optional resources (the plugin builds and runs without them):

- `src\assets\zh-glyphs\zh_glyphs.png/.txt`: Chinese glyph atlas rendered from the open LXGW WenKai font (see
  [THIRD-PARTY.md](THIRD-PARTY.md)); the game's own Chinese font lacks glyphs. To regenerate it, see
  `src\assets\zh-glyphs\README.md` (needs the font table dumped from your own game).
- Downed-player marker picture: made by `src\assets\dead-marker\make-dead-marker.py` from your own game's map texture
  (game art, so not in this repo).
- `zh_extra.txt` (translations for game texts the Chinese localisation leaves blank) and `zh_terms.txt` (fixes to official Chinese terms): contain game text, not published.

`src\run-*.ps1` and `src\test-launch.ps1` are automated tests that start real game clients in isolated copies. They
assume the repo is at `C:\OriCoop`; read the header of each script first.

### Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) and [Harmony](https://github.com/pardeike/Harmony).
- [LXGW WenKai](https://github.com/lxgw/LxgwWenKai) (SIL OFL 1.1).
- Moon Studios, for the game.
- Most of the code was written and tested by AI coding assistants under the author's direction.

License: MIT (see [LICENSE](LICENSE)).
