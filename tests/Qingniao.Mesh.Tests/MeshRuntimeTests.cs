using System.Net;
using System.Runtime.CompilerServices;
using Qingniao.Core;
using Qingniao.LinkManager;
using Qingniao.Mesh;
using Qingniao.MeshDaemon;

namespace Qingniao.Mesh.Tests;

/// <summary>
/// MeshRuntime 组装语义(全部依赖为注入假件,R15):
/// (a) StartAsync 后假 discovery 触发 PeerFound → 假 orchestrator 收到 Source=="mdns" 的候选,
///     非 IPv4 候选在接线处丢弃(R17);
/// (b) RunVpnScanCycleAsync:假 scanner 一开一关 → 仅开的进 orchestrator 且 Source=="vpn"。
/// </summary>
public class MeshRuntimeTests
{
    [Fact]
    public async Task StartAsync_StartsHostAdvertiserAndDiscovery()
    {
        MeshRuntimeFixture fixture = new();
        using CancellationTokenSource cts = new();

        await fixture.Runtime.StartAsync(cts.Token);

        Assert.Equal(1, fixture.Host.StartCount);
        Assert.Equal(1, fixture.Advertiser.StartCount);
        Assert.Equal(1, fixture.Discovery.StartCount);
    }

    [Fact]
    public async Task StartAsync_Twice_Throws_MdnsInstancesMustNotRestart()
    {
        MeshRuntimeFixture fixture = new();
        using CancellationTokenSource cts = new();
        await fixture.Runtime.StartAsync(cts.Token);

        // T12 mDNS 红线:Stop→Start 会静默失效,StartAsync 必须拒绝二次启动
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Runtime.StartAsync(cts.Token));
    }

    [Fact]
    public async Task StartAsync_PeerFound_IPv4_DrivesMdnsCandidateIntoOrchestrator()
    {
        MeshRuntimeFixture fixture = new();
        using CancellationTokenSource cts = new();
        TaskCompletionSource<PeerCandidate> arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Orchestrator.OnCandidate = candidate => arrived.TrySetResult(candidate);

        await fixture.Runtime.StartAsync(cts.Token);
        fixture.Discovery.Fire(
            new PeerDiscovered("machine-b", IPAddress.Parse("192.168.1.20"), MeshPort));

        PeerCandidate candidate = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("mdns", candidate.Source);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), candidate.Address);
        Assert.Equal(MeshPort, candidate.Port);
    }

    [Fact]
    public async Task StartAsync_PeerFound_IPv6_IsDroppedBeforeOrchestrator()
    {
        MeshRuntimeFixture fixture = new();
        using CancellationTokenSource cts = new();
        TaskCompletionSource<PeerCandidate> arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Orchestrator.OnCandidate = candidate => arrived.TrySetResult(candidate);

        await fixture.Runtime.StartAsync(cts.Token);

        // R17:IPv6 候选在接线处丢弃;丢弃路径无 await,Fire 返回时已同步完成
        fixture.Discovery.Fire(
            new PeerDiscovered("machine-v6", IPAddress.Parse("fe80::1"), MeshPort));
        Assert.Empty(fixture.Orchestrator.Candidates);

        // 用 v4 到达证明处理管线活着,且此前 v6 未混入
        fixture.Discovery.Fire(
            new PeerDiscovered("machine-v4", IPAddress.Parse("192.168.1.20"), MeshPort));
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        PeerCandidate candidate = Assert.Single(fixture.Orchestrator.Candidates);
        Assert.Equal("mdns", candidate.Source);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), candidate.Address);
    }

    [Fact]
    public async Task RunVpnScanCycleAsync_OnlyOpenResultsReachOrchestratorAsVpnCandidates()
    {
        MeshRuntimeFixture fixture = new(["10.66.0.0/24"]);
        using CancellationTokenSource cts = new();
        fixture.Scanner.Results =
        [
            new ProbeResult(IPAddress.Parse("10.66.0.2"), IsOpen: true, LatencyMs: 1),
            new ProbeResult(IPAddress.Parse("10.66.0.3"), IsOpen: false, LatencyMs: 500),
            new ProbeResult(IPAddress.Parse("10.66.0.4"), IsOpen: true, LatencyMs: 2),
        ];

        await fixture.Runtime.RunVpnScanCycleAsync(cts.Token);

        Assert.Equal(["10.66.0.0/24"], fixture.Scanner.LastCidrs);
        Assert.Equal(MeshPort, fixture.Scanner.LastPort);
        Assert.Equal(2, fixture.Orchestrator.Candidates.Count);
        Assert.All(fixture.Orchestrator.Candidates, c => Assert.Equal("vpn", c.Source));
        Assert.All(fixture.Orchestrator.Candidates, c => Assert.Equal(MeshPort, c.Port));
        Assert.Equal(
            [IPAddress.Parse("10.66.0.2"), IPAddress.Parse("10.66.0.4")],
            fixture.Orchestrator.Candidates.Select(c => c.Address).ToArray());
    }

    [Fact]
    public async Task StopAsync_StopsAllCollaborators_AndUnwiresPeerFound()
    {
        MeshRuntimeFixture fixture = new();
        using CancellationTokenSource cts = new();
        await fixture.Runtime.StartAsync(cts.Token);

        await fixture.Runtime.StopAsync();
        await fixture.Runtime.StopAsync(); // 幂等

        Assert.Equal(1, fixture.Discovery.StopCount);
        Assert.Equal(1, fixture.Advertiser.StopCount);
        Assert.Equal(1, fixture.Host.StopCount);

        // 停止后 PeerFound 已退订:再触发不产生候选(同步断言,无轮询)
        fixture.Discovery.Fire(
            new PeerDiscovered("late-peer", IPAddress.Parse("192.168.1.20"), MeshPort));
        Assert.Empty(fixture.Orchestrator.Candidates);
    }

    private const int MeshPort = 13371;

    /// <summary>组装一套全假件运行时;每测试独立实例,可注入配置的 VPN 网段。</summary>
    private sealed class MeshRuntimeFixture
    {
        public FakeHost Host { get; } = new();
        public FakeAdvertiser Advertiser { get; } = new();
        public FakeDiscovery Discovery { get; } = new();
        public FakeScanner Scanner { get; } = new();
        public FakeOrchestrator Orchestrator { get; } = new();

        public MeshRuntime Runtime { get; }

        public MeshRuntimeFixture(IReadOnlyList<string>? vpnCidrs = null)
        {
            Runtime = new MeshRuntime(
                new MeshConfig { MeshPort = MeshPort, VpnCidrs = vpnCidrs ?? [] },
                Host,
                Advertiser,
                Discovery,
                Scanner,
                Orchestrator);
        }
    }

    private sealed class FakeHost : IYggdrasilHost
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdvertiser : IMdnsAdvertiser
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>假发现器:Fire 在调用线程上同步触发 PeerFound,与真库语义一致。</summary>
    private sealed class FakeDiscovery : IMdnsDiscovery
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public event EventHandler<PeerDiscovered>? PeerFound;

        public void Fire(PeerDiscovered discovered) => PeerFound?.Invoke(this, discovered);

        public Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScanner : IVpnCidrScanner
    {
        public IReadOnlyList<ProbeResult> Results { get; set; } = [];
        public IReadOnlyList<string>? LastCidrs { get; private set; }
        public int LastPort { get; private set; }

        public async IAsyncEnumerable<ProbeResult> ScanAsync(
            IReadOnlyList<string> cidrs,
            int port,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastCidrs = cidrs;
            LastPort = port;
            foreach (ProbeResult result in Results)
            {
                await Task.Yield();
                yield return result;
            }
        }
    }

    private sealed class FakeOrchestrator : ILinkOrchestrator
    {
        public List<PeerCandidate> Candidates { get; } = [];

        public Action<PeerCandidate>? OnCandidate { get; set; }

        public event EventHandler<LinkInfo>? PeerAdded
        {
            add { }
            remove { }
        }

        public Task<bool> TryAddPeerAsync(PeerCandidate candidate, CancellationToken ct = default)
        {
            Candidates.Add(candidate);
            OnCandidate?.Invoke(candidate);
            return Task.FromResult(true);
        }
    }
}
