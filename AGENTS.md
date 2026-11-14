# AGENTS.md — 青鸟(qingniao)会话规则

本项目所有会话(任何工具/任何协作者)必读必守。权威文档:设计文档
`docs/superpowers/specs/2026-11-11-qingniao-design.md`——本文件与其冲突时以设计文档为准;
本文件与用户当期指令冲突时以用户指令为准。

## 项目

去中心化 Windows 远程控制(受 UU 远程启发,clean-room 自研,不依赖厂商云)。
C#/.NET 10 LTS 全栈,Yggdrasil v0.5.x 做 mesh 底座。

- 状态总览:`docs/milestones.md`(每期验收时更新)
- 现行计划:`docs/superpowers/plans/`(经用户批准后执行)

## 硬约束(违反即跑偏,任何会话不得放宽)

1. 屏幕内容与键鼠事件**永不过明文网络**;端到端加密强制(应用层,Noise_XX/TLS1.3 mutual),
   **不信任 Yggdrasil 的逐跳加密**——中继节点必须只见密文
2. 无鉴权接入不存在:未在信任列表的设备必须被显式批准;无人值守 ≠ 无凭据
3. 无遥测、无厂商云依赖、无在线激活;日志仅本地且不含屏幕内容
4. clean-room:不复制网易 UU 的任何代码/资产
5. Windows-only,`net10.0-windows`,全部工程 `TreatWarningsAsErrors=true`
6. 依赖 in-box API 优先;任何 NuGet/原生依赖必须在计划中记录选型理由

## 工作流

- **先计划后实施**:实施计划须用户批准;执行采用子代理 SDD(任务简报 → 实现 → 每任务审查门 →
  裁定台账 → 整分支终审 → 一次修复波 + 范围化复审)
- **实证规则**:外部行为(注册表布局、协议形状、API 签名、第三方库能力)先实测(尖刺或直接验证),
  不凭记忆决策;结论作为裁定记入当期计划/台账
- **提交门**:`dotnet test --filter Category!=Live` 全绿且零警告方可提交;`Category=Live` 测试
  需管理员权限,按需手工运行
- **提交格式**:`<type>: <描述>`(feat/fix/refactor/test/docs/chore/perf),正文详述必要背景;
  **无任何署名/Co-Authored-By 行**(用户全局规则覆盖工具默认)
- **shell 一律用 Bash 工具**(PowerShell 工具在本环境实测损坏)
- **验收时更新 `docs/milestones.md`**:状态与日期、验收证据、偏差与决定(带理由)、
  硬约束再审计清单逐条过、遗留债务清单

## 已沉淀的关键裁定(速查,详见各期计划/台账)

- 编码能力权威 = `MFTEnumEx` 无过滤硬件枚举 + 激活后协商;类型过滤枚举不可信(HEVC 实证漏报)
- yggdrasil v0.5.15:配置 HJSON;admin 协议 `{"request","arguments"}` 信封 + 多行 pretty 响应;
  yggdrasilctl 须 `tcp://` 前缀
- System.Net.Quic(net10):自签证书私钥须 PFX 导出再导入;ALPN 须监听器级与连接级双处设置;
  服务端 TLS 选项走 `ConnectionOptionsCallback → QuicServerConnectionOptions`
