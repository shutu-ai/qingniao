# 青鸟 (qingniao)

去中心化 Windows 远控:每台机器运行一个守护进程,通过 yggdrasil(VPN overlay)与 mDNS(局域网发现)
自动组网,互相发现并注入 peer,形成全互联 mesh。Phase 1(M0/M1)目标是**网状连通**:
两台机器在 60 秒内自动互认,overlay 全链路可 ping。

## 快速开始

前置条件:

- Windows 10/11(x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `tools/yggdrasil/` 已就位:`yggdrasil.exe`、`yggdrasilctl.exe`、`wintun.dll`
  (Task 3 交付;本仓库已含。yggdrasil 需要管理员权限安装 wintun 虚拟网卡)
- **以管理员身份运行**终端(daemon 会拉起 yggdrasil 并监听本机 admin socket)

运行守护进程(仓库根目录):

```powershell
dotnet run --project src/Qingniao.MeshDaemon -- --config configs/sample.json
```

启动后它会:

1. 托管 yggdrasil 子进程(意外退出按指数退避自动重启);
2. 通过 mDNS(`_qingniao._udp.local`)向局域网通告自己,并发现其他节点;
3. 按 `--scan-interval`(默认 30 秒,立即先跑一轮)对 `vpnCidrs` 网段做 TCP 存活扫描;
4. 发现的节点(来源 `mdns` 或 `vpn`)经编排器幂等注入 yggdrasil(`addPeer`),
   成功时打印 `[qng] 已添加 tcp://<ip>:13371 (来源)`。

`Ctrl+C` 优雅停止:停 mDNS(发 goodbye)→ 杀 yggdrasil → 退出。

参数:

| 参数 | 默认 | 说明 |
|------|------|------|
| `--config <path>` | `configs/sample.json` | 配置文件路径(缺文件回落默认值) |
| `--scan-interval <sec>` | `30` | VPN 网段扫描间隔(秒,≥1) |

配置(`configs/sample.json`,与 `MeshConfig` 一一对应):

```json
{
  "meshPort": 13371,          // 本机 mesh 监听端口
  "adminPort": 13372,         // 本机 yggdrasil admin socket 端口
  "vpnCidrs": [],             // 要扫描的 VPN 网段,如 ["10.66.0.0/24"] 或对端 "/32"
  "yggdrasilExePath": "tools/yggdrasil/yggdrasil.exe",
  "bandwidthEndpoint": null   // 自检测速端点(可选,未配置则自检用网卡链路速率)
}
```

Phase 1 为 IPv4-only:mDNS 候选中的 IPv6 地址会在接线处直接丢弃。

## 环境自检

```powershell
dotnet run --project src/Qingniao.SelfCheck                       # 人读报告(默认读 configs/sample.json)
dotnet run --project src/Qingniao.SelfCheck -- --json             # JSON 输出
dotnet run --project src/Qingniao.SelfCheck -- --only nat         # 只跑一项
dotnet run --project src/Qingniao.SelfCheck -- --config <path>    # 指定配置
```

五项检查:ipv6、nat、gpu-encoder、vpn-reachability、bandwidth。退出码:全 Pass=0;有 Warn/Unknown=1;有 Fail=2。

> 注:未配置 `vpnCidrs` 时 `vpn-reachability` 显示 **UNKNOWN(未配置 VPN 网段)属预期**,
> 不是故障 —— 填入网段后该项才会真实探测。

## 测试

```powershell
dotnet test --filter Category!=Live    # 全部单元/集成测试(Live 用例需真机环境,单跑)
```

## 真机验收 runbook(M1 验收,Live)

需要:**两台同网段的 Windows 真机(A 与 B)+ 一台 VPN 内机器**(见断言 4),两台都装好
.NET 10 SDK 与 `tools/yggdrasil/`,且以**管理员身份**运行终端。以下每台机器各执行一遍:

```powershell
# 机器 A 与机器 B(同一 LAN,或 B 在 VPN 内时用 VpnCidrs 指向)
dotnet run --project src/Qingniao.MeshDaemon -- --config configs/sample.json   # 管理员权限运行
# 机器 A 的 sample.json 若走 VPN 发现:VpnCidrs = ["<B 的 VPN IPv4>/32"]
dotnet run --project src/Qingniao.SelfCheck
```

验收断言:

1. **mDNS 发现**:60 秒内 A 的日志出现 `[qng] 已添加 tcp://<B 的 IPv4>:13371 (mdns)`
   (VPN 扫描场景为 `(vpn)`)。
2. **admin 通路**:A 上执行
   `tools\yggdrasil\yggdrasilctl.exe -endpoint=127.0.0.1:13372 getPeers`
   列出 B 的公钥与端点;B 上反向同理(应看到 A)。
3. **自检报告**:A 的 SelfCheck 一页报告五项检查全部非 UNKNOWN,且结论与真机实情一致
   (若刻意未配 `vpnCidrs`,该项 UNKNOWN 属预期,见上文说明)。
4. **overlay 全链路**:B 侧 `ping <A 的 SelfAddress>`(SelfAddress 见 A 启动时 yggdrasil
   分配的自身地址,可用 `yggdrasilctl -endpoint=127.0.0.1:13372 getSelf` 查看)可通。

## 项目结构

```
src/
  Qingniao.Core/        MeshConfig 与配置持久化
  Qingniao.Mesh/        yggdrasil 子进程托管、admin socket 客户端
  Qingniao.LinkManager/ mDNS 通告/发现、CIDR 扫描、peer 注入编排
  Qingniao.MeshDaemon/  守护进程组装(MeshRuntime + Program)
  Qingniao.SelfCheck/   环境自检 CLI(五项检查)
tests/                  对应单元/集成测试(Category=Live 为真机用例)
configs/sample.json     示例配置
tools/yggdrasil/        yggdrasil 运行时(外部依赖,勿改动)
```
