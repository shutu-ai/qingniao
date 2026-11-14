# 里程碑总览

> 本文件回答三个问题:**整体走到哪、哪些决定偏离了原设计(及理由)、还欠哪些债**。
> 每期验收的会话结束时更新一次并提交;执行期细节不进这里(以 git log 与当期计划为准)。
> 验收标准原文见设计文档 §11;反目标原文见 §5——本文件的再审计清单是它们的逐条落地。

## 硬约束再审计清单(每期验收必过)

| # | 检查项 | 来源 | 期次结论 |
|---|--------|------|----------|
| A1 | 无任何厂商云依赖/遥测回传/在线激活 | §5 | M0/M1 ✅(无出网依赖,STUN 仅探测用不作信任锚) |
| A2 | 屏幕内容/键鼠事件不明文过网 | §5 | M0/M1 ✅(串流未开始);M2 待验 |
| A3 | 中继节点不可解密转发内容 | §5 | M0/M1 ✅(同上);M2 待验 |
| A4 | 无鉴权接入不存在:未信任设备必须显式批准 | §5 | M0/M1 ✅(无接入面);M2 待验(T7 信任决策) |
| A5 | clean-room:无网易 UU 任何代码/资产 | §1 | M0/M1 ✅;M2 待验 |
| A6 | 日志仅本地且不含屏幕内容 | §9 | M0/M1 ✅(stdout 报告无屏幕内容);M2 待验(T1 日志) |
| A7 | 依赖 in-box 优先,例外有选型记录 | §12 | M0/M1 ✅(仅 Makaretu.Dns.Multicast);M2 待验(计划已预记 Vortice/抽象包理由) |

## M0 环境自检 CLI — ✅ 已验收(2026-11-12,随 Phase 1)

- **验收标准(§11)**:一页报告:IPv6/NAT 类型/编码器/VPN 网段
- **证据**:T10 自检报告 + 2026-11-12 真机单机冒烟(overlay 地址与 `yggdrasilctl getSelf` 逐字一致)
- **重要教训(R14→MFT-R1)**:GPU 编码器探测曾按错误假设(HardwareMFT 键)报假阴 Warn;M2 Spike 1 实证纠正——**注册表 ≠ API 真相,编码能力权威 = MFTEnumEx 无过滤硬件枚举**

## M1 Mesh 底座 — ✅ 单机验收完成(2026-11-12);双机场景待验

- **验收标准(§11)**:VPN 侧三节点互 ping overlay 地址
- **已验**:14 任务全绿(217/217 非 Live 零警告)、5/5 Live 测试(管理员)、单机冒烟、HEAD `4525baa` 推送
- **待验**:双机/三节点互 ping(README runbook 已备,等第二台机器)
- **偏差与决定(C1-C3,计划级 Critical,终审实证发现)**:yggdrasil v0.5.15 配置为 HJSON 非 YAML;admin 协议为 `{"request","arguments"}` 信封 + 多行 pretty 响应;yggdrasilctl 须 `tcp://` 前缀 endpoint——均已按真实二进制验证修复(commit `6cad018`)
- **遗留债务(后续期折叠)**:并发停止的 late-Exited 窗口、T2-1/T7-2/T11-3/T13-2/T13-4/T14-2/T14-3/T14-6 各 Minor → 已折叠:M2 T1(日志+配置校验)、M2 T2(身份私钥持久化);mDNS 网卡变更重建 → M6;ReportWriter Detail → M0 工具随需

## M2 串流 MVP(QUIC 版)— 🔶 进行中(计划待批准)

- **验收标准(§11)**:LAN 内可流畅操作,测得延迟数据
- **尖刺已完成(2026-11-12)**:Spike 1 MFTEnumEx——本机 3 个 Intel 硬编确认,NV12→H264 硬编过滤命中 2,HEVC 类型过滤=0 而 H265 硬编存在(裁定 MFT-R1:无过滤枚举+激活后协商,类型过滤不可信);Spike 2 System.Net.Quic——Win11 开箱可用,4 路并发流回显校验通过,RTT 73µs(裁定 QUIC-R1 schannel 私钥须 PFX 重导入、QUIC-R2 ALPN 双处设置、QUIC-R3 服务端 TLS 走 ConnectionOptionsCallback)
- **计划**:`docs/superpowers/plans/2026-11-12-m2-streaming-mvp.md`(11 任务),**待用户批准**
- **计划内预记偏差(批准后生效)**:M2 数据路径经服务进程代理、助手直连留 M4 重审;UI=WPF+HwndHost;Noise_XX 用 in-box 原语(Ed25519/X25519 in-box 可用性为 T2 实测门,缺失则裁定换 NSec);D3D/MF 绑定用 Vortice(任务内置能力核查门,缺失面手写 interop)
- **边界**:锁屏/UAC 注入按 §11 归 M3;M2 只保证会话不崩、解锁后恢复

## M3 IPv4 打洞 + 两档邀请码 + 锁屏/UAC 注入 — ⬜ 未开始

## M4 自研 UDP+FEC 视频路径、自适应码率、多显示器 — ⬜ 未开始
