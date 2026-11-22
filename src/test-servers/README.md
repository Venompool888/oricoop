# 测试服务器（T-155）

`run-nettest -CServer`、`run-titlejoin -Transport cserver`、`run-rooms` 只连远程测试实例，不在本机起 CServer 或 RoomServer，避免 Windows 防火墙授权弹窗。

## 本机私有配置

在仓库外创建 `%USERPROFILE%\.oricoop\test-servers.psd1`，也可用 `ORICOOP_TEST_SERVERS_FILE` 指定其他文件：

```powershell
@{ Host = '<测试机地址>'; SshHost = '<SSH 别名>'; Slot = 0 }
```

真实地址和 SSH 别名不能进仓库、mod 或 kit。没配置所需地址时，脚本在拿测试副本前报 `NO TEST SERVER` 并退出，不会退回本机起服务器。`ORICOOP_TEST_SERVER_SLOT` 可覆盖配置中的槽位。`ORICOOP_TEST_CSERVER` / `ORICOOP_TEST_ROOMS` 可分别覆盖为 `host:port`。

配置 `SshHost` 时，测试会用一次非交互 SSH 读取所需服务的源码哈希；哈希缺失或不一致会报 `TEST SERVER STALE`，SSH 失败也会阻止启动。没配 `SshHost` 只警告；由环境变量覆盖地址的服务不检查哈希。

## 槽位和端口

| 槽位 | 房间 TCP | C 服务器 UDP | 用途 |
|---|---|---|---|
| 0 | 47101 | 47102 | 跟随 main，共用 |
| N（1–3） | 47101+10N | 47102+10N | 测试分支的服务器改动 |

`47100+10N` 保留不用。测试实例和正式服务分开：systemd 单元为 `oricoop-test-rooms@N` / `oricoop-test-cserver@N`，部署目录为 `/opt/oricoop/test/N/`，房间数据在 `/var/lib/oricoop-test-rooms-N`，定时清理旧数据。C 服务器测试用房间码隔离；`run-rooms` 每次用新的随机测试 SteamID，互相看不到其他测试的房间。

## 部署或更新

```powershell
src\test-servers\deploy-test-servers.ps1 -Ref main
src\test-servers\deploy-test-servers.ps1 -Slot 2 -Services cserver
$env:ORICOOP_TEST_SERVER_SLOT = '2'
```

默认部署 HEAD 的两个服务；用 `-Ref` 选提交，或在槽位 1–3 用 `-Dirty` 部署工作区文件。槽位 0 只接受 main 上的提交。目标机上需要可非交互登录且能管理测试单元的 SSH 配置、`/opt/oricoop/dotnet` 下的 .NET SDK 和 systemd。

脚本上传服务器项目所需源码，在远程 `dotnet publish`，构建成功后替换并重启选中的单元，检查运行状态和监听端口后才写该服务的 `cserver.hash` 或 `rooms.hash`。哈希按文件名和原始文件字节计算；部署和测试请使用一致的检出换行设置。同一槽位的部署用 `flock` 串行，上一版程序留作 `<服务>.prev`。

`CServerTest` 本地模式、`RoomTest` 等只监听回环地址的单元测试仍在本机运行，不使用远程槽位。
