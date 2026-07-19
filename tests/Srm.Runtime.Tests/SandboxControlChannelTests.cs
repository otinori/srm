using Srm.Runtime.Sandbox;
using Xunit;

namespace Srm.Runtime.Tests;

public class SandboxControlChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "srm-control-" + Guid.NewGuid());
    private readonly SandboxControlChannel _sut;

    public SandboxControlChannelTests()
    {
        Directory.CreateDirectory(_dir);
        _sut = new SandboxControlChannel(_dir);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void InitialState_NoSignalsSet()
    {
        Assert.False(_sut.IsReady);
        Assert.False(_sut.IsStopRequested);
        Assert.False(_sut.IsStopped);
    }

    [Fact]
    public void SignalReady_SetsIsReady()
    {
        _sut.SignalReady();
        Assert.True(_sut.IsReady);
    }

    [Fact]
    public void SignalStop_SetsIsStopRequested()
    {
        _sut.SignalStop();
        Assert.True(_sut.IsStopRequested);
    }

    [Fact]
    public void SignalStopped_SetsIsStopped()
    {
        _sut.SignalStopped();
        Assert.True(_sut.IsStopped);
    }

    [Fact]
    public void WaitForReady_AlreadySignaled_ReturnsTrueImmediately()
    {
        _sut.SignalReady();
        Assert.True(_sut.WaitForReady(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void WaitForReady_NeverSignaled_ReturnsFalseAfterTimeout()
    {
        Assert.False(_sut.WaitForReady(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void WaitForReady_SignaledDuringWait_ReturnsTrue()
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(200);
            _sut.SignalReady();
        });
        t.Start();

        Assert.True(_sut.WaitForReady(TimeSpan.FromSeconds(2)));
        t.Join();
    }

    [Fact]
    public void HasScenario_NoScenarioWritten_ReturnsFalse()
    {
        Assert.False(_sut.HasScenario);
        Assert.Null(_sut.ReadScenario());
    }

    [Fact]
    public void WriteScenario_ThenReadScenario_RoundTrips()
    {
        var scenario = new ScenarioModel
        {
            Steps =
            [
                new ScenarioStep { Type = "send_key", Text = "hello" },
                new ScenarioStep { Type = "send_mouse", X = 10, Y = 20 },
                new ScenarioStep { Type = "screenshot", Label = "after-typing" },
            ],
        };

        _sut.WriteScenario(scenario);

        Assert.True(_sut.HasScenario);
        var read = _sut.ReadScenario();
        Assert.NotNull(read);
        Assert.Equal(3, read!.Steps.Count);
        Assert.Equal("send_key", read.Steps[0].Type);
        Assert.Equal("hello", read.Steps[0].Text);
        Assert.Equal(10, read.Steps[1].X);
        Assert.Equal(20, read.Steps[1].Y);
        Assert.Equal("after-typing", read.Steps[2].Label);
    }

    [Fact]
    public void TryReadScenarioResult_NoResultWritten_ReturnsNull()
    {
        Assert.Null(_sut.TryReadScenarioResult());
    }

    [Fact]
    public void WriteScenarioResult_ThenTryReadScenarioResult_RoundTrips()
    {
        var result = new ScenarioResultModel
        {
            Success = true,
            CompletedAt = DateTimeOffset.Now,
            Steps =
            [
                new ScenarioStepResult { Index = 0, Type = "send_key", Success = true },
                new ScenarioStepResult { Index = 1, Type = "screenshot", Success = true, ArtifactRelativePath = "scenario-step-001-x.png" },
            ],
        };

        _sut.WriteScenarioResult(result);

        var read = _sut.TryReadScenarioResult();
        Assert.NotNull(read);
        Assert.True(read!.Success);
        Assert.Equal(2, read.Steps.Count);
        Assert.Equal("scenario-step-001-x.png", read.Steps[1].ArtifactRelativePath);
    }

    [Fact]
    public void WaitForScenarioResult_WrittenDuringWait_ReturnsResult()
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(200);
            _sut.WriteScenarioResult(new ScenarioResultModel { Success = true, CompletedAt = DateTimeOffset.Now });
        });
        t.Start();

        var result = _sut.WaitForScenarioResult(TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        t.Join();
    }

    [Fact]
    public void WaitForScenarioResult_NeverWritten_ReturnsNullAfterTimeout()
    {
        Assert.Null(_sut.WaitForScenarioResult(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void BlockUntilStop_WithCallback_InvokesOnceWhenScenarioAppears()
    {
        var invocations = 0;
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteScenario(new ScenarioModel());
            Thread.Sleep(200);
            _sut.SignalStop();
        });
        t.Start();

        _sut.BlockUntilStop(() => Interlocked.Increment(ref invocations));

        Assert.Equal(1, invocations);
        t.Join();
    }

    [Fact]
    public void BlockUntilStop_WithCallback_NoScenario_NeverInvokesCallback()
    {
        var invocations = 0;
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.SignalStop();
        });
        t.Start();

        _sut.BlockUntilStop(() => Interlocked.Increment(ref invocations));

        Assert.Equal(0, invocations);
        t.Join();
    }

    [Fact]
    public void TryReadFocusRequest_NoRequestWritten_ReturnsNull()
    {
        Assert.Null(_sut.TryReadFocusRequest());
    }

    [Fact]
    public void WriteFocusRequest_ThenTryReadFocusRequest_RoundTrips()
    {
        _sut.WriteFocusRequest(new FocusRequestModel { RequestId = "req-1", App = "notepad-test" });

        var read = _sut.TryReadFocusRequest();
        Assert.NotNull(read);
        Assert.Equal("req-1", read!.RequestId);
        Assert.Equal("notepad-test", read.App);
    }

    [Fact]
    public void WriteFocusRequest_Twice_OverwritesRatherThanAccumulating()
    {
        _sut.WriteFocusRequest(new FocusRequestModel { RequestId = "req-1", App = "notepad-test" });
        _sut.WriteFocusRequest(new FocusRequestModel { RequestId = "req-2", App = "notepad-test" });

        Assert.Equal("req-2", _sut.TryReadFocusRequest()!.RequestId);
        Assert.Single(Directory.GetFiles(_dir, "focus-request*"));
    }

    [Fact]
    public void WriteFocusResult_ThenTryReadFocusResult_RoundTrips()
    {
        _sut.WriteFocusResult(new FocusResultModel { RequestId = "req-1", Success = true });

        var read = _sut.TryReadFocusResult();
        Assert.NotNull(read);
        Assert.Equal("req-1", read!.RequestId);
        Assert.True(read.Success);
    }

    [Fact]
    public void WaitForFocusResult_MatchingRequestId_ReturnsResult()
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteFocusResult(new FocusResultModel { RequestId = "req-1", Success = true });
        });
        t.Start();

        var result = _sut.WaitForFocusResult("req-1", TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        Assert.True(result!.Success);
        t.Join();
    }

    [Fact]
    public void WaitForFocusResult_StaleRequestId_IgnoredAndTimesOut()
    {
        // 古いrequestIdの結果しか無い場合、一致する結果が出るまで待ち続け、
        // 最終的にタイムアウトしてnullを返す（不一致の結果を誤って採用しない）。
        _sut.WriteFocusResult(new FocusResultModel { RequestId = "req-old", Success = true });

        var result = _sut.WaitForFocusResult("req-new", TimeSpan.FromMilliseconds(300));
        Assert.Null(result);
    }

    [Fact]
    public void BlockUntilStop_WithFocusCallback_InvokesForEachDistinctRequestId()
    {
        var seenRequestIds = new List<string>();
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteFocusRequest(new FocusRequestModel { RequestId = "req-1", App = "a" });
            Thread.Sleep(300);
            _sut.WriteFocusRequest(new FocusRequestModel { RequestId = "req-2", App = "a" });
            Thread.Sleep(300);
            _sut.SignalStop();
        });
        t.Start();

        _sut.BlockUntilStop(
            onScenarioDetected: null,
            onFocusRequestDetected: req =>
            {
                lock (seenRequestIds) seenRequestIds.Add(req.RequestId);
            });

        Assert.Equal(["req-1", "req-2"], seenRequestIds);
        t.Join();
    }

    [Fact]
    public void TryReadDiagRequest_NoRequestWritten_ReturnsNull()
    {
        Assert.Null(_sut.TryReadDiagRequest());
    }

    [Fact]
    public void WriteDiagRequest_ThenTryReadDiagRequest_RoundTrips()
    {
        _sut.WriteDiagRequest(new DiagRequestModel { RequestId = "req-1", SampleWindowMs = 500 });

        var read = _sut.TryReadDiagRequest();
        Assert.NotNull(read);
        Assert.Equal("req-1", read!.RequestId);
        Assert.Equal(500, read.SampleWindowMs);
    }

    [Fact]
    public void WriteDiagRequest_Twice_OverwritesRatherThanAccumulating()
    {
        _sut.WriteDiagRequest(new DiagRequestModel { RequestId = "req-1" });
        _sut.WriteDiagRequest(new DiagRequestModel { RequestId = "req-2" });

        Assert.Equal("req-2", _sut.TryReadDiagRequest()!.RequestId);
        Assert.Single(Directory.GetFiles(_dir, "diag-request*"));
    }

    [Fact]
    public void WriteDiagResult_ThenTryReadDiagResult_RoundTrips()
    {
        _sut.WriteDiagResult(new DiagResultModel { RequestId = "req-1", Success = true, KernelTimePercent = 12.3 });

        var read = _sut.TryReadDiagResult();
        Assert.NotNull(read);
        Assert.Equal("req-1", read!.RequestId);
        Assert.True(read.Success);
        Assert.Equal(12.3, read.KernelTimePercent);
    }

    [Fact]
    public void WaitForDiagResult_MatchingRequestId_ReturnsResult()
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteDiagResult(new DiagResultModel { RequestId = "req-1", Success = true });
        });
        t.Start();

        var result = _sut.WaitForDiagResult("req-1", TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        Assert.True(result!.Success);
        t.Join();
    }

    [Fact]
    public void WaitForDiagResult_StaleRequestId_IgnoredAndTimesOut()
    {
        _sut.WriteDiagResult(new DiagResultModel { RequestId = "req-old", Success = true });

        var result = _sut.WaitForDiagResult("req-new", TimeSpan.FromMilliseconds(300));
        Assert.Null(result);
    }

    [Fact]
    public void BlockUntilStop_WithDiagCallback_InvokesForEachDistinctRequestId()
    {
        var seenRequestIds = new List<string>();
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteDiagRequest(new DiagRequestModel { RequestId = "req-1" });
            Thread.Sleep(300);
            _sut.WriteDiagRequest(new DiagRequestModel { RequestId = "req-2" });
            Thread.Sleep(300);
            _sut.SignalStop();
        });
        t.Start();

        _sut.BlockUntilStop(
            onScenarioDetected: null,
            onFocusRequestDetected: null,
            onDiagRequestDetected: req =>
            {
                lock (seenRequestIds) seenRequestIds.Add(req.RequestId);
            });

        Assert.Equal(["req-1", "req-2"], seenRequestIds);
        t.Join();
    }

    // channel-d-guest-mcp-bridge: focus/diagと呼び出し側の役割が逆
    // （ゲストがrequestを書きresultを待つ、ホストがrequestを読みresultを書く）だが、
    // ファイルI/O自体はSandboxControlChannel側からは対称なので同じ往復パターンで検証する。
    [Fact]
    public void WriteMcpRequest_ThenTryReadMcpRequest_RoundTrips()
    {
        _sut.WriteMcpRequest(new McpRequestModel
        {
            RequestId = "req-1",
            ServerName = "github",
            Method = "tools/call",
            ToolName = "list_issues",
            ArgumentsJson = "{}",
        });

        var read = _sut.TryReadMcpRequest();
        Assert.NotNull(read);
        Assert.Equal("req-1", read!.RequestId);
        Assert.Equal("github", read.ServerName);
        Assert.Equal("tools/call", read.Method);
        Assert.Equal("list_issues", read.ToolName);
    }

    [Fact]
    public void WriteMcpRequest_Twice_OverwritesRatherThanAccumulating()
    {
        _sut.WriteMcpRequest(new McpRequestModel { RequestId = "req-1", ServerName = "github", Method = "tools/list" });
        _sut.WriteMcpRequest(new McpRequestModel { RequestId = "req-2", ServerName = "github", Method = "tools/list" });

        Assert.Single(Directory.GetFiles(_dir, "mcp-request*.json"));
        Assert.Equal("req-2", _sut.TryReadMcpRequest()!.RequestId);
    }

    [Fact]
    public void WaitForMcpResult_MatchingRequestId_ReturnsResult()
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteMcpResult(new McpResultModel { RequestId = "req-1", Success = true, ResultJson = "{\"ok\":true}" });
        });
        t.Start();

        var result = _sut.WaitForMcpResult("req-1", TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("{\"ok\":true}", result.ResultJson);
        t.Join();
    }

    [Fact]
    public void WaitForMcpResult_StaleRequestId_IgnoredAndTimesOut()
    {
        _sut.WriteMcpResult(new McpResultModel { RequestId = "req-old", Success = true });

        var result = _sut.WaitForMcpResult("req-new", TimeSpan.FromMilliseconds(300));
        Assert.Null(result);
    }

    [Fact]
    public void WriteMcpResult_RejectedCall_CarriesReason()
    {
        _sut.WriteMcpResult(new McpResultModel { RequestId = "req-1", Success = false, Reason = "tool not in allow_tools" });

        var read = _sut.TryReadMcpResult();
        Assert.NotNull(read);
        Assert.False(read!.Success);
        Assert.Equal("tool not in allow_tools", read.Reason);
    }

    // tier2-channel-c-mapped-folder: kind文字列で汎用化したWriteRequest<T>/
    // TryReadRequest<T>/WriteResult<T>/WaitForResult<T>の往復確認。
    // FileTransferRequestModel/ResultModelを実際のIHasRequestId実装として使う
    // （SandboxControlChannel自体はこれらのモデルを知らない）。
    [Fact]
    public void WriteRequest_ThenTryReadRequest_RoundTrips()
    {
        _sut.WriteRequest("file-transfer", new FileTransferRequestModel
        {
            RequestId = "req-1",
            Direction = FileTransferDirection.HostToGuest,
            GuestPath = @"C:\srm\outbox\file.txt",
            FileName = "file.txt",
        });

        var read = _sut.TryReadRequest<FileTransferRequestModel>("file-transfer");
        Assert.NotNull(read);
        Assert.Equal("req-1", read!.RequestId);
        Assert.Equal(FileTransferDirection.HostToGuest, read.Direction);
    }

    [Fact]
    public void WriteRequest_Twice_OverwritesRatherThanAccumulating()
    {
        _sut.WriteRequest("file-transfer", new FileTransferRequestModel { RequestId = "req-1", FileName = "a.txt" });
        _sut.WriteRequest("file-transfer", new FileTransferRequestModel { RequestId = "req-2", FileName = "b.txt" });

        Assert.Single(Directory.GetFiles(_dir, "file-transfer-request*.json"));
        Assert.Equal("req-2", _sut.TryReadRequest<FileTransferRequestModel>("file-transfer")!.RequestId);
    }

    [Fact]
    public void TryReadRequest_NoRequestWritten_ReturnsNull()
    {
        Assert.Null(_sut.TryReadRequest<FileTransferRequestModel>("file-transfer"));
    }

    [Fact]
    public void WaitForResult_MatchingRequestId_ReturnsResult()
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteResult("file-transfer", new FileTransferResultModel { RequestId = "req-1", Success = true });
        });
        t.Start();

        var result = _sut.WaitForResult<FileTransferResultModel>("file-transfer", "req-1", TimeSpan.FromSeconds(2));
        Assert.NotNull(result);
        Assert.True(result!.Success);
        t.Join();
    }

    [Fact]
    public void WaitForResult_StaleRequestId_IgnoredAndTimesOut()
    {
        _sut.WriteResult("file-transfer", new FileTransferResultModel { RequestId = "req-old", Success = true });

        var result = _sut.WaitForResult<FileTransferResultModel>("file-transfer", "req-new", TimeSpan.FromMilliseconds(300));
        Assert.Null(result);
    }

    [Fact]
    public void TryReadRequestId_ReadsIdWithoutFullDeserialization()
    {
        _sut.WriteRequest("file-transfer", new FileTransferRequestModel { RequestId = "req-1", FileName = "a.txt" });

        Assert.Equal("req-1", _sut.TryReadRequestId("file-transfer"));
    }

    [Fact]
    public void TryReadRequestId_NoRequestWritten_ReturnsNull()
    {
        Assert.Null(_sut.TryReadRequestId("file-transfer"));
    }

    [Fact]
    public void BlockUntilStop_WithGenericHandler_InvokesOnceWhenNewRequestIdAppears()
    {
        var invokedWith = new List<string>();
        var handlers = new Dictionary<string, Action<string>>
        {
            ["file-transfer"] = requestId => invokedWith.Add(requestId),
        };

        var t = new Thread(() =>
        {
            Thread.Sleep(100);
            _sut.WriteRequest("file-transfer", new FileTransferRequestModel { RequestId = "req-1", FileName = "a.txt" });
            Thread.Sleep(600);
            _sut.SignalStop();
        });
        t.Start();

        _sut.BlockUntilStop(onScenarioDetected: null, onFocusRequestDetected: null, onDiagRequestDetected: null, genericRequestHandlers: handlers);
        t.Join();

        Assert.Single(invokedWith);
        Assert.Equal("req-1", invokedWith[0]);
    }
}
