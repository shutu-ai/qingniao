# M2 串流 MVP 实施计划(QUIC 版)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 单屏查看+控制的串流 MVP:被控端 服务(session-0 形态)+会话助手(交互会话)拆分,主控端 WPF 查看器;DXGI 采集 → MF 硬编(H264)→ QUIC+应用层端到端加密 → MF 硬解 → D3D11 低延迟呈现 → SendInput 注入;单机回环可全链路跑通并测得延迟数据。

**Architecture:** 三进程模型(设计 §7 硬性要求):`Qingniao.Service`(由现有 MeshDaemon 演进,持 mesh/鉴权/会话调度,终止 QUIC+Noise 并把帧/输入经命名管道代理给助手)、`Qingniao.SessionHelper`(交互会话内:采集+编码+输入注入)、`Qingniao.Viewer`(WPF 主控端,经 `Qingniao.ViewerCore` 复用解码呈现)。数据路径 M2 全走服务代理(M4 换自研 UDP 时传输演化只动服务进程)。

**Tech Stack:** .NET 10(net10.0-windows)/ System.Net.Quic(尖刺已验证开箱可用)/ Vortice.Direct3D11 + Vortice.MediaFoundation(DXGI/MF COM 绑定,任务内置能力核查门)/ System.Security.Cryptography(ChaCha20Poly1305、HKDF、SHA-256 in-box;Ed25519/X25519 in-box 可用性为 T2 首个实测门)/ Microsoft.Extensions.Logging.Abstractions(仅抽象,自带文件+控制台 provider)。

**Spec:** `docs/superpowers/specs/2026-11-11-qingniao-design.md` §6-§9、§11(M2 行)、§12(builder 决定);尖刺结论:`%TEMP%\qng-spike`(结论已固化于本文各任务)。

## Global Constraints

- Windows-only;`net10.0-windows` TFM;全部 csproj `TreatWarningsAsErrors=true`
- 屏幕内容与输入事件**永不过明文网络**;应用层端到端加密强制,不信任 Yggdrasil 逐跳加密(§9);中继只见密文
- 会话加密 = Noise_XX_25519_ChaChaPoly_SHA256,双方长期 Ed25519 身份密钥相互认证,前向保密(§9)
- 身份私钥落盘必须 DPAPI(CurrentUser)保护
- 尖刺裁定 MFT-R1:**编码能力权威 = MFTEnumEx,无过滤硬件枚举 + 激活后协商;类型过滤枚举不可信**(HEVC 类型过滤=0 而机器确有 H265 硬编)
- 尖刺裁定 QUIC-R1:内存 CreateSelfSigned 私钥 schannel 不可用 → PFX 导出再导入(`X509CertificateLoader.LoadPkcs12`);QUIC-R2:ALPN 须同时设在监听器级与 `QuicServerConnectionOptions.ServerAuthenticationOptions`;QUIC-R3:net10 服务端 TLS 选项走 `ConnectionOptionsCallback → QuicServerConnectionOptions`
- 无遥测;日志仅本地、不含屏幕内容(§9)
- 提交格式 `feat|fix|refactor|test|docs|chore|perf: 描述`,无任何署名/Co-Authored-By 行(用户全局规则覆盖 harness 默认)
- shell 一律用 Bash 工具(PowerShell 工具损坏,既往裁定 R5)
- 每任务收尾:`dotnet test --filter Category!=Live` 全绿(含既有 217 个)且零警告方可提交
- Phase 1 遗留处置(本计划内折叠):日志框架(T1)、端口/CIDR 配置校验(T1)、身份私钥持久化(T2);**折叠到后续里程碑不进 M2**:mDNS 网卡变更重建(M6)、ReportWriter Detail 显示(M0 工具)、锁屏/UAC 注入(M3)

## Review Focus

1. **无硬件编码器/激活失败**(驱动故障、虚拟机):合理使用者预期自动退到软件编码、查看照常可用——T6 有无过滤枚举为空时激活内置软编 MFT 的测试
2. **锁屏/分辨率变化中途发生**:合理使用者预期会话不崩——采集超时/帧尺寸变化走优雅降级(M2 不要求锁屏可见,只要求不崩、解锁后恢复)——T5/T8 各有对应测试
3. **查看端中途断开**:合理使用者预期被控端释放采集/编码资源、回到可再连状态——T7/T12 有断开后资源回收与重连测试
4. **密文帧被篡改/截断/乱序**:合理使用者预期会话明确报错终止,绝无明文兜底——T3/T4 有防篡改与乱序拒绝测试
5. **首个未信任设备发起连接**:合理使用者预期被控端明确询问而非静默接受或静默拒绝——T7 有 TOFU 提示与拒绝路径测试

---

### Task 1: 日志框架 + 配置校验

**Files:**
- Create: `src/Qingniao.Core/Logging/ILog.cs`、`src/Qingniao.Core/Logging/Log.cs`、`src/Qingniao.Core/Logging/FileConsoleLoggerProvider.cs`
- Modify: `src/Qingniao.Core/MeshConfig.cs`(校验)、`src/Qingniao.Core/ConfigStore.cs`(加载时校验)
- Test: `tests/Qingniao.Tests/LoggingTests.cs`、`tests/Qingniao.Tests/MeshConfigValidationTests.cs`

**Interfaces:**
- Produces: `ILog { void Info(string); void Warn(string); void Error(string, Exception?); void Debug(string); }`;`static Log ForComponent(string name)` 返回 `ILog`,环境变量 `QNG_LOG_LEVEL` ∈ {Debug,Info,Warn,Error} 默认 Info
- Produces: `MeshConfig.Validate()` → `IReadOnlyList<string>`(错误清单,空=合法);`ConfigStore.Load`/`LoadOrDefault` 对非法配置抛 `InvalidConfigException : Exception`(消息含全部错误行)
- Consumes: 现有 `MeshConfig` 字段(MeshPort=13371、AdminPort=13372、VpnCidrs、StunServers、BandwidthEndpoint)

- [ ] **Step 1: 写失败测试**——`LoggingTests`:QNG_LOG_LEVEL=Error 时 Info 不出、Error 出;FileSink 写入临时文件且 5MB 上限触发时截断保留后半;`MeshConfigValidationTests`:端口 ∉ [1024,65535] 报错、CIDR 非 `a.b.c.d/nn` 形态报错、StunServers 项缺 `host:port` 报错、合法配置返回空清单
- [ ] **Step 2: 跑测试确认失败**(类型不存在编译失败即 RED)
- [ ] **Step 3: 实现**——provider 用 `Microsoft.Extensions.Logging.Abstractions` 的 `ILoggerProvider` 包装,包引用只加 `Microsoft.Extensions.Logging.Abstractions`(版本 10.x 最新);校验用 `IPAddress.TryParse` + `int.TryParse` 手写 CIDR 解析(不引网络库)
- [ ] **Step 4: 全量测试绿零警告** `dotnet test --filter Category!=Live`
- [ ] **Step 5: Commit** `feat: 日志框架与 MeshConfig 校验`

### Task 2: 节点身份(Ed25519 密钥 + 指纹 + DPAPI 持久化)

**Files:**
- Create: `src/Qingniao.Core/Identity/NodeIdentity.cs`、`src/Qingniao.Core/Identity/IdentityStore.cs`
- Test: `tests/Qingniao.Tests/IdentityTests.cs`

**Interfaces:**
- Produces: `NodeIdentity { string Fingerprint; byte[] PublicKey; Ed25519PrivateKey PrivateKey?; }`(具体类型见 Step 0 门);`Fingerprint` = SHA-256(publicKey) 前 16 hex 字符
- Produces: `IdentityStore(string directory)`: `NodeIdentity GetOrCreate()`——读 `identity.key`(DPAPI CurrentUser `ProtectedData.Protect/Unprotect`),无则生成并写入;文件权限仅当前用户
- **Step 0 实测门(先行一步,结论记入任务报告)**:net10.0 in-box 是否有 Ed25519 签名与 X25519 密钥交换(`System.Security.Cryptography` 下找 `Ed25519`/`X25519` 类型)。**有**→全用 in-box;**无**→裁定 R-M2-1:改用 NSec(libsodium 绑定)并同步影响 T3 接口签名,报告里写明选型理由。接口形状两种情形保持一致

- [ ] **Step 1: 写失败测试**——同目录两次 `GetOrCreate` 返回同指纹;不同目录不同指纹;`identity.key` 不含明文公钥字节(grep 不到 fingerprint hex);损坏文件(截断/改一字节)抛 `IdentityCorruptException`
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现**(DPAPI:包引用 `System.Security.Cryptography.ProtectedData` 10.x)
- [ ] **Step 4: 全量测试绿零警告**
- [ ] **Step 5: Commit** `feat: Ed25519 节点身份与 DPAPI 持久化`

### Task 3: Noise_XX 会话加密

**Files:**
- Create: `src/Qingniao.Core/Crypto/NoiseSession.cs`、`src/Qingniao.Core/Crypto/NoiseHandshake.cs`、`tests/Qingniao.Tests/TestAssets/noise-vectors-xx.json`(从 noiseprotocol.org 官方测试向量库取 XX_25519_ChaChaPoly_SHA256 全组,落库为测试资产)
- Test: `tests/Qingniao.Tests/NoiseTests.cs`

**Interfaces:**
- Produces: `static NoiseHandshake.InitiatorAsync(Stream transport, NodeIdentity identity, string expectedRemoteFingerprint, CancellationToken)` / `static NoiseHandshake.ResponderAsync(Stream transport, NodeIdentity identity, IReadOnlyCollection<string> acceptedFingerprints, CancellationToken)` → `NoiseSession`
- Produces: `NoiseSession : IAsyncDisposable`——`string RemoteFingerprint`;`Task WriteFrameAsync(ReadOnlyMemory<byte> plaintext, ct)`;`Task<byte[]> ReadFrameAsync(ct)`;帧格式 `[4字节LE明文长度][ChaCha20Poly1305密文+16字节tag]`,nonce = 8字节 LE 序号补零成 12 字节,方向各自计数;明文上限 512KB;序号回退/重复/长度越界/认证失败 → 抛 `NoiseProtocolException` 并使会话不可再用(无明文兜底)
- Consumes: T2 `NodeIdentity`
- 协议固定:`Noise_XX_25519_ChaChaPoly_SHA256`,prologue 空,`split()` 两方向密钥,无 rekey(2^31 帧上限后拒绝,超限抛 `NoiseProtocolException`)

- [ ] **Step 1: 写失败测试**——(a) 规范向量:JSON 全组 initiator/responder 握手消息逐字节一致,向量含 split 后交换测试的全部断言通过;(b) 双方经 `PipeStream`(自写测试用匿名管道流)握手成功互得指纹;(c) responder 收到不在 accepted 列表的指纹抛 `IdentityRejectedException` 且连接关闭;(d) 单字节密文翻转 → ReadFrame 抛 `NoiseProtocolException`;(e) 乱序重放第 N 帧再读 → 抛;(f) 1MB 明文 WriteFrame 抛 `ArgumentOutOfRangeException`
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现**——握手状态机严格按 Noise 规范 XX 模式 6 次运算顺序写,测试向量是唯一裁判;不引入第三方 Noise 库(除非 T2 Step 0 已裁定 NSec)
- [ ] **Step 4: 全量测试绿零警告**
- [ ] **Step 5: Commit** `feat: Noise_XX 会话加密与规范向量一致性`

### Task 4: QUIC 载体(QuadCarrier)+ 控制协议信封

**Files:**
- Create: `src/Qingniao.Core/Transport/QuicSessionServer.cs`、`src/Qingniao.Core/Transport/QuicSessionClient.cs`、`src/Qingniao.Core/Transport/ControlMessage.cs`、`src/Qingniao.Core/Transport/FrameHeader.cs`
- Modify: `src/Qingniao.Core/MeshConfig.cs`(新增 `SessionPort`(默认 13381))
- Test: `tests/Qingniao.Tests/QuicCarrierTests.cs`、`tests/Qingniao.Tests/ControlMessageTests.cs`

**Interfaces:**
- Consumes: T3 `NoiseHandshake/NoiseSession`(QUIC 流上握手)、尖刺 QUIC-R1/R2/R3(PFX 重导入证书工厂、双处 ALPN `qingniao-m2`、`ConnectionOptionsCallback` 形状)
- Produces: `QuicSessionServer(IPEndPoint, NodeIdentity, IReadOnlyCollection<string> acceptedFingerprints)`,`Task<AuthenticatedSession> AcceptAsync(ct)`;`QuicSessionClient static Task<AuthenticatedSession> ConnectAsync(EndPoint, NodeIdentity, string expectedRemoteFingerprint, ct)`
- Produces: `AuthenticatedSession : IAsyncDisposable`——`NoiseSession Crypto`;`Task SendControlAsync(ControlMessage, ct)`;`Task<ControlMessage> ReceiveControlAsync(ct)`(单条控制流);`Task SendVideoFrameAsync(FrameHeader, ReadOnlyMemory<byte> h264Nals, ct)`(**每帧独立 QUIC 双向流**);`Task<(FrameHeader, byte[])> ReceiveVideoFrameAsync(ct)`;`Task SendInputAsync(ReadOnlyMemory<byte> inputBatch, ct)` / `Task<byte[]> ReceiveInputAsync(ct)`(单条输入流);关闭:用户 `DisposeAsync` → 对端读侧收到 `QuicException` 连接关闭
- Produces: `ControlMessage { string Type; System.Text.Json.JsonElement Body; }`,序列化 UTF-8 JSON;`FrameHeader`(16 字节定长):`ulong FrameId`、`long CaptureTimestampTicks`、`byte Flags`(bit0=关键帧)
- 帧分块:单帧 >16KB 按 16KB 多次 `WriteAsync` 于同一流(不换流)

- [ ] **Step 1: 写失败测试**(真实回环 QUIC,不需要提权)——(a) 服务端 Accept + 客户端 Connect 各得 `AuthenticatedSession` 且互得指纹;(b) 控制消息往返逐字段相等;(c) 3 帧各 1KB/16KB/48KB 往返字节相等、FrameId 与时间戳保序;(d) 输入批次往返;(e) 指纹不匹配连接被拒(双方都抛 `IdentityRejectedException`);(f) 客户端 Dispose 后服务端 ReceiveControlAsync 抛;(g) `FrameHeader` 序列化 16 字节定长断言
- [ ] **Step 2: 跑测试确认失败**
- [ ] **Step 3: 实现**——证书工厂按 QUIC-R1;流策略:控制流=连接后首条双向流,输入流=第二条,视频帧流=后续每帧一条
- [ ] **Step 4: 全量测试绿零警告**
- [ ] **Step 5: Commit** `feat: QUIC 会话载体与控制协议信封`

### Task 5: DXGI 桌面采集(Vortice 绑定 + 能力核查门)

**Files:**
- Create: `src/Qingniao.Core/Capture/IDesktopCapture.cs`、`src/Qingniao.Core/Capture/CaptureFrame.cs`、`src/Qingniao.Core/Capture/DesktopDuplicationCapture.cs`
- Test: `tests/Qingniao.Tests/CaptureFakeTests.cs`(Unit)+ `tests/Qingniao.Tests/CaptureLiveTests.cs`(`Category=Live`)

**Interfaces:**
- **Step 0 能力核查门**:确认 `Vortice.Direct3D11`(最新 10.x 包)含 `IDXGIOutputDuplication`(Desktop Duplication)与 `IDXGIOutput5.DuplicateOutput1`;**有**→用之;**缺**→裁定 R-M2-2:缺失部分手写 DllImport/vtable 补齐,记录清单
- Produces: `IDesktopCapture : IAsyncDisposable`——`Task StartAsync(StopwatchTimestampSource, ct)`;`ValueTask<CaptureFrame?> WaitFrameAsync(TimeSpan timeout, ct)`(超时返回 null,不抛);`event EventHandler<CaptureResizedEventArgs>? Resized`
- Produces: `CaptureFrame`(record):`Texture2D Texture`(NV12, GPU)、`int Width`、`int Height`、`long CaptureTimestampTicks`(`Stopwatch.GetTimestamp()` 于 AcquireNextFrame 返回时)、`bool IsKeyframeCandidate`(首帧/脏区全屏)
- 语义:主显示器;超时(通常锁屏/无变化时 AccquireNextFrame 超时)→ null + 内部计数,连续 5s 全超时发 `Warn` 日志**但不抛**;采集会话中途桌面分辨率变化 → 触发 `Resized` 事件并重建 duplication

- [ ] **Step 1: 写失败 Unit 测试**——用 `FakeCaptureSource`(实现 `IDesktopCapture`,可编程返回帧/超时/Resized)测:T8 侧消费逻辑将依赖的语义(超时 null、Resized 触发重建调用)——本任务先立 Fake 与接口,实现类 Live 验证
- [ ] **Step 2: 跑确认失败**(接口/类型不存在)
- [ ] **Step 3: 实现 `DesktopDuplicationCapture`**(D3D11 设备创建 `D3D11CreateDevice(BGRA 支持)`、输出枚举、DuplicateOutput1、帧纹理拷贝为自有 NV12 纹理避免 duplication 内部缓冲复用冲突)
- [ ] **Step 4: Unit 全绿;Live 测试**(有桌面会话时手工跑):`dotnet test --filter "Category=Live & FullyQualifiedName~CaptureLive"` 断言 2 秒内取得 ≥10 帧非 null、纹理尺寸=主屏分辨率、时间戳单调递增
- [ ] **Step 5: 全量非 Live 测试绿零警告**
- [ ] **Step 6: Commit** `feat: DXGI 桌面复制采集与超时优雅降级`

### Task 6: MF 硬编 H264(编码能力权威 + 软编兜底)

**Files:**
- Create: `src/Qingniao.Core/Encode/EncodeConfig.cs`、`src/Qingniao.Core/Encode/IVideoEncoder.cs`、`src/Qingniao.Core/Encode/EncodedFrame.cs`、`src/Qingniao.Core/Encode/MediaFoundationH264Encoder.cs`、`src/Qingniao.Core/Encode/EncoderCapabilities.cs`
- Test: `tests/Qingniao.Tests/EncoderFakeTests.cs`(Unit)+ `tests/Qingniao.Tests/EncoderLiveTests.cs`(`Category=Live`)

**Interfaces:**
- **Step 0 绑定能力核查门**:`Vortice.MediaFoundation` 是否覆盖——`MFTEnumEx`、`IMFActivate.ActivateObject<IMFTransform>`、`IMFDXGIDeviceManager`、`ICodecAPI` 属性设置、异步 MFT 事件队列(`IMFMediaEventGenerator`);**全有**→用之;**缺项**→裁定 R-M2-3:缺失面手写 COM interop 补齐(参照尖刺 mft-spike 的 vtable 方法),清单入报告
- Produces: `EncoderCapabilities static H264EncoderProbe()`→`IReadOnlyList<EncoderCandidate>`;**枚举方式 = MFTEnumEx 无过滤硬件枚举(MFT-R1)**,激活后按 NV12→H264 协商失败再试下一候选;硬件候选耗尽 → 内置软编 `H264 Encoder MFT`(同步 MFT)兜底,`EncoderCandidate.IsHardware` 区分
- Produces: `IVideoEncoder : IAsyncDisposable`——`Task ConfigureAsync(EncodeConfig, ct)`;`ValueTask<EncodedFrame?> EncodeAsync(CaptureFrame, ct)`(NV12 纹理进、H264 NAL 字节出;`EncodedFrame(record byte[] Data, bool KeyFrame, long CaptureTimestampTicks, int Width, int Height)`)
- Produces: `EncodeConfig(int Width, int Height, int Fps=30, int BitrateKbps=8000)`(合法范围 1–20 Mbps,§8;越界抛)
- 低延迟配置(全部设置,失败即抛不静默):`MF_LOW_LATENCY=1`、CODECAPI `AVLowLatencyMode=1`、无 B 帧(CODECAPI `AVLowLatencyMode` + GOP:CODECAPI `AVEncVideoForceKeyFrame` 可用性探测,若驱动不支持 intra-refresh 则 GOP=30)、CBR(`AVEncCommonRateControlMode=1`)

- [ ] **Step 1: 写失败 Unit 测试**——`FakeEncoder` 实现 `IVideoEncoder`(可编程关键帧间隔/丢帧/异常);`EncodeConfig` 越界(bitrate 25000)抛;`EncoderCandidate` 排序硬件优先
- [ ] **Step 2: 跑确认失败**
- [ ] **Step 3: 实现**(激活路径:枚举→逐候选 `BindToActivation`→媒体类型协商→属性设置→`ProcessInput(NV12 纹理)`/`ProcessOutput` 循环;输出聚合为单帧 NAL;时间戳透传 `CaptureTimestampTicks`)
- [ ] **Step 4: Live 测试**(手工):合成 320x240 NV12 纹理喂 30 帧,断言产出 ≥1 关键帧 + 后续差分帧、总字节 >1KB、时间戳透传一致;另断言 `H264EncoderProbe()` 至少返回 1 候选(本机已实证有硬编)
- [ ] **Step 5: 全量非 Live 测试绿零警告**
- [ ] **Step 6: Commit** `feat: MF H264 编码器(无过滤硬编枚举+软编兜底+低延迟配置)`

### Task 7: 被控端服务(Qingniao.Service:会话调度 + 信任决策 + 助手拉起 + 管道代理)

**Files:**
- Modify: `src/Qingniao.MeshDaemon/` → **git mv 重命名 `src/Qingniao.Service/`**(项目名、命名空间、README 引用同步;现有 mesh 功能全部保留)
- Create: `src/Qingniao.Service/Session/SessionCoordinator.cs`、`src/Qingniao.Service/Session/TrustList.cs`、`src/Qingniao.Service/Ipc/ServiceIpcServer.cs`、`src/Qingniao.Service/Ipc/HelperLaunch.cs`、`src/Qingniao.Service/SessionRelay.cs`
- Test: `tests/Qingniao.Tests/TrustListTests.cs`、`tests/Qingniao.Tests/SessionCoordinatorTests.cs`、`tests/Qingniao.Tests/ServiceIpcTests.cs`

**Interfaces:**
- Consumes: T1-T4 全部;现有 `YggdrasilHost`/`MdnsAdvertiser`/`LinkOrchestrator`/`ConfigStore`/`AdminSocketClient`
- Produces: `TrustList(string filePath)`——`TrustState Classify(string fingerprint)`(`Trusted|Unknown|Denied`);`Trust(string fingerprint, string displayName)`、`Deny(string)`;文件 JSON,手改兼容
- Produces: `SessionCoordinator`——状态机 `Idle → Authenticating → Active ↔ Idle`;`Task RunAsync(ct)`:Accept → Noise 握手 → `TrustList.Classify`:`Unknown` 时经控制消息 `TrustPrompt` 询问(控制台 Y/N + 超时 60s 默认拒绝;M3 换托盘)、`Denied` 直接断;单会话 MVP(已有会话时新连接回 `SessionBusy` 控制消息);Active 期间:把 `ReceiveVideoFrameAsync` 输出经管道转发助手、助手回传的输入批次经 `SendInputAsync` 转发查看端
- Produces: `ServiceIpcServer`(命名管道 `qingniao-helper-<sessionId>`,JSON 行协议)——`HelperChannel` 消息:`SessionStart{sessionId,width,height,fps,bitrateKbps}`、`FrameBatch{frames:[{keyFrame,captureTimestampTicks,data(base64)}]}`(服务→助手)、`InputBatch{events:[...]}`(服务→助手)、`HelperReady`、`SessionEnded{reason}`(助手→服务)
- Produces: `HelperLaunch static Task<Process> LaunchAsync(string helperExePath, string pipeName, ct)`——`WTSQueryUserToken`(活动会话)+ `CreateProcessAsUser`;进程树随会话结束回收
- 服务进程形态:保持控制台可运行(dev/Live),`--service` 开关预留 SCM 入口(M3 安装为服务)

- [ ] **Step 1: 写失败测试**——TrustList:分类/信任/拒绝/文件往返;SessionCoordinator:Fake 传输注入——未信任指纹触发 `TrustPrompt` 消息、拒绝后 `Idle` 可再连、信任后进入 `Active`、第二连接得 `SessionBusy`、查看端断开回到 `Idle` 且 FakeCapture/FakeEncoder `DisposeAsync` 被调用;ServiceIpc:管道消息往返、FrameBatch 转发顺序保序
- [ ] **Step 2: 跑确认失败**
- [ ] **Step 3: 实现**(git mv + 引用重组;`HelperLaunch` 的 WTS API 用 DllImport wtsapi32/kernel32)
- [ ] **Step 4: 全量非 Live 测试绿零警告**
- [ ] **Step 5: Commit** `feat: Qingniao.Service 会话调度与信任决策及助手拉起`

### Task 8: 会话助手(Qingniao.SessionHelper:采集+编码 worker + 输入注入)

**Files:**
- Create: `src/Qingniao.SessionHelper/Program.cs`、`src/Qingniao.SessionHelper/HelperSessionWorker.cs`、`src/Qingniao.Core/Input/InputEvent.cs`、`src/Qingniao.Core/Input/IInputInjector.cs`、`src/Qingniao.Core/Input/SendInputInjector.cs`
- Test: `tests/Qingniao.Tests/InputMappingTests.cs`、`tests/Qingniao.Tests/HelperWorkerTests.cs` + `tests/Qingniao.Tests/InputLiveTests.cs`(`Category=Live`)

**Interfaces:**
- Consumes: T5 `IDesktopCapture`/`CaptureFrame`、T6 `IVideoEncoder`、T7 `HelperChannel` 管道协议、T1 `ILog`
- Produces: `InputEvent`(二进制打包,UTF-8 JSON 信封内 base64 载荷):`MouseAbsolute{double Nx, double Ny}`(查看端发送归一化 [0,1]×[0,1] 相对采集屏)、`MouseButton{byte Button, bool Down}`、`Wheel{int Delta}`、`KeyScan{ushort ScanCode, bool Up, bool Extended}`、`UnicodeText{string Text}`(KEYEVENTF_UNICODE)
- Produces: `IInputInjector { void Inject(ReadOnlySpan<InputEvent> events); }`;`SendInputInjector`——绝对坐标换算 `x=(ushort)(Nx*65535)`(SendInput 绝对坐标域);扫描码键盘;失败(返回值 0)抛 `InputInjectionException` 带 Win32 错误码
- Produces: `HelperSessionWorker`——管道连接 → `SessionStart` → 起 `IDesktopCapture`+`IVideoEncoder` 循环(编码出的帧组 `FrameBatch` 发服务,服务断开/`SessionEnded` → 优雅收尾资源全释放);收 `InputBatch` → 注入;分辨率 `Resized` 事件 → 向服务发控制消息(经管道 `SessionEnded{reason=resized}` 结束会话,MVP 不做中途换挡,Review Focus 2 的"不崩"语义)
- 助手进程参数:`--pipe <name>`,启动即 `HelperReady`

- [ ] **Step 1: 写失败测试**——坐标映射边界(Nx=0/1 → 0/65535)、事件二进制往返、Fake 管道上 worker 生命周期:`SessionStart` 后 FakeCapture 出帧 → `FrameBatch` 顺序到达、服务断开 → worker `DisposeAsync` 全链调用(FakeEncoder/FakeCapture 计数)、`InputBatch` 送达注入器(注入器注入 Fake)
- [ ] **Step 2: 跑确认失败**
- [ ] **Step 3: 实现**
- [ ] **Step 4: Live 测试**(手工,真实 SendInput):对当前会话注入 5 次扫描码'A',断言 `Inject` 无异常且 SendInput 返回 1(5/5)——不做 UI 断言
- [ ] **Step 5: 全量非 Live 测试绿零警告**
- [ ] **Step 6: Commit** `feat: 会话助手采集编码 worker 与 SendInput 注入`

### Task 9: 主控端核心(Qingniao.ViewerCore:ViewClient + MF 解码 + D3D11 呈现)

**Files:**
- Create: `src/Qingniao.ViewerCore/ViewClient.cs`、`src/Qingniao.ViewerCore/Decode/MediaFoundationH264Decoder.cs`、`src/Qingniao.ViewerCore/Present/SwapchainPresenter.cs`、`src/Qingniao.ViewerCore/LatencyMeter.cs`
- Test: `tests/Qingniao.Tests/ViewerCoreTests.cs`(Unit)+ `tests/Qingniao.Tests/DecodeLiveTests.cs`(`Category=Live`)

**Interfaces:**
- Consumes: T4 `QuicSessionClient`/`AuthenticatedSession`/`FrameHeader`、T6 `EncodedFrame` 同构
- Produces: `IVideoDecoder : IAsyncDisposable`——`ValueTask<DecodedFrame?> DecodeAsync(EncodedFrame, ct)`;`DecodedFrame(record Texture2D Texture, long CaptureTimestampTicks)`(NV12)
- Produces: `MediaFoundationH264Decoder`——激活 in-box `Microsoft H264 Video Decoder MFT`(软/硬自动,解码无需探查)+ D3D 设备管理器,输出 NV12 纹理
- Produces: `SwapchainPresenter(IntPtr hwnd)`——`void EnsureSize(int w, int h)`;`void Present(DecodedFrame)`;waitable swap chain(`IDXGISwapChain2.WaitOnVBlank` 前置 `GetFrameLatencyWaitableObject`,最大帧延迟=1)、NV12→RGB 经 `ID3D11VideoProcessor`(YUV 转换,不入 CPU);DPI per-monitor v2(宿主 WPF 声明)
- Produces: `ViewClient : IAsyncDisposable`——`Task ConnectAsync(EndPoint remote, NodeIdentity identity, string expectedRemoteFingerprint, ct)`;`event EventHandler<FramePresentedEventArgs>? FramePresented`(参数含端到端延迟 ms);`Task SendInputAsync(ReadOnlySpan<InputEvent>, ct)`;`Task RunAsync(ct)`(接收帧→解码→Present 循环)
- Produces: `LatencyMeter`——`void OnFrame(long captureTicks)`;`LatencyStats Snapshot()`(`record LatencyStats(double P50Ms, double P95Ms, double P99Ms, int FrameCount)`;tick→ms 用 `Stopwatch.Frequency` 换算)

- [ ] **Step 1: 写失败 Unit 测试**——`LatencyMeter`:注入已知 tick 差算出 P50/P95(用可控 TimeProvider/Fake 时钟);解码器 Live 前先 Unit:无效 NAL 输入返回 null 不抛;`ViewClient` 用 Fake 会话:帧事件顺序、输入发送经 Fake 断言载荷
- [ ] **Step 2: 跑确认失败**
- [ ] **Step 3: 实现**(解码:处理 H264 流式输入——攒帧喂 MFT,输出聚合;呈现:resize 时重建交换链)
- [ ] **Step 4: Live 测试**(手工):T6 Live 产出的 H264 字节落盘为测试资产,`DecodeLiveTests` 喂解码器断言:输出帧尺寸 320x240、NV12 纹理可读回(UA 采样中心像素非零)、`LatencyMeter` 在合成时间戳上 P50 计算正确
- [ ] **Step 5: 全量非 Live 测试绿零警告**
- [ ] **Step 6: Commit** `feat: 主控端核心(查看客户端+MF 硬解+waitable 交换链呈现+延迟计量)`

### Task 10: 主控端 WPF(Qingniao.Viewer)+ 端到端单机集成

**Files:**
- Create: `src/Qingniao.Viewer/Qingniao.Viewer.csproj`(WPF,net10.0-windows,`UseWPF=true`)、`src/Qingniao.Viewer/App.xaml(.cs)`、`src/Qingniao.Viewer/MainWindow.xaml(.cs)`、`src/Qingniao.Viewer/RenderHost.cs`(HwndHost 承载 swap chain HWND)
- Create: `src/Qingniao.ViewerCore/HeadlessViewRunner.cs`(无窗运行 ViewClient,供 Live 端到端测试与命令行复测)
- Test: `tests/Qingniao.Tests/E2ELiveTests.cs`(`Category=Live`,单机全链路)

**Interfaces:**
- Consumes: T7 服务、T8 助手(以子进程启动)、T9 ViewerCore
- Produces: `MainWindow`——目标输入(节点名或 overlay 地址)、连接/断开按钮、状态条(指纹对、延迟 P50/P95、fps)、视频渲染区(HwndHost)、键鼠事件捕获(PreviewKeyDown 等 → `ViewClient.SendInputAsync`);归一化坐标换算(鼠标 pos ÷ 渲染区尺寸)
- Produces: `HeadlessViewRunner static Task<HeadlessResult> RunAsync(ViewerOptions, ct)`——`HeadlessResult(record LatencyStats Latency, int FramesPresented, TimeSpan Duration)`;CLI:`Qingniao.Viewer --headless --address <overlay> --seconds N`(验收复测入口)
- **DPI**:app.manifest 声明 PerMonitorV2

- [ ] **Step 1: 写失败 E2E Live 测试**——测试编排器:起 `Qingniao.Service`(临时目录配置,信任列表预置测试指纹)→ 起本机 helper(直接拉起,模拟服务行为——服务自身由被测代码拉起)→ `HeadlessViewRunner` 连本机 overlay 地址 8 秒 → 断言:FramesPresented ≥ 100、`Latency.P50Ms > 0`(测量而非设阈值)、期间注入 3 个扫描码事件无异常、`DisposeAsync` 后服务回 `Idle`(`SessionCoordinator` 状态可查)、**重连第二次会话成功**(Review Focus 3)
- [ ] **Step 2: 跑确认失败**(工程不存在)
- [ ] **Step 3: 实现**(WPF 壳 + HwndHost + 输入桥;HeadlessViewRunner 与 WPF 共用 ViewerCore)
- [ ] **Step 4: Live 测试跑通并记录延迟数字**(命令行 `--headless` 复测 30s,把 P50/P95/fps 记入任务报告——这就是 M2 验收的"测得延迟数据")
- [ ] **Step 5: 全量非 Live 测试绿零警告**
- [ ] **Step 6: Commit** `feat: WPF 主控端与单机端到端集成(含延迟实测)`

### Task 11: README + 双机 runbook(M2 章节)

**Files:**
- Modify: `README.md`(M2 章节:三进程启动顺序、SessionPort 防火墙预置 UDP 13381、指纹显示与首次信任流程、`--headless` 延迟复测命令、已知边界:锁屏画面不可见(M3)、软编兜底路径)

- [ ] **Step 1: 写 README 章节**(runbook 按双机:被控机起 Service、主控机起 Viewer;LAN 同网段 mDNS 发现沿用 M1)
- [ ] **Step 2: 单机按 README 从零走一遍**(临时目录配置)全程无文档歧义
- [ ] **Step 3: 全量非 Live 测试绿零警告**
- [ ] **Step 4: Commit** `docs: M2 串流运行手册`

## Self-Review 结论

- **Spec 覆盖**:§7 三进程模型(T7/T8/T10)、§8.1 采集(T5)、§8.2 编码(T6,MFT-R1 替代 MFT_ENUM_HARDWARE 直名——无过滤枚举涵盖并修正之)、§8.3 M2 传输=QUIC+每帧流(T4)、§8.4 解码呈现(T9)、§8.5 输入(T8,独立输入流在 T4)、§8.6 自适应码率——**M2 仅占位(EncodeConfig 码率可配,发送端动态调整属 M4)**,§9 e2e 加密+身份+信任(T2/T3/T4/T7)、§11 M2 行验收(单机实测延迟 T10,LAN 双机 runbook T11)。缺口自查:§9 "每次连接弹窗二次确认"可配置——M2 由 `TrustList.Classify` 的 Unknown→询问覆盖默认路径,可配置化随 M3 信任码体系。
- **类型一致性**:`CaptureFrame.CaptureTimestampTicks`(T5/T6/T9 一致)、`EncodedFrame(byte[] Data, bool KeyFrame, long CaptureTimestampTicks, int Width, int Height)`(T6/T9 一致)、`AuthenticatedSession` 方法名(T4/T7/T9 一致)、管道消息名(T7/T8 一致)。
- **步骤可执行性**:每个 Step 0 门(实测型决策)都规定了产出去向(任务报告/裁定记录),接口两种情形保持不变。
- **比例**:计划 11 任务,与 Phase 1(14 任务)同量级;测试代码只钉断言,无实现正文。
