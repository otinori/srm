using System.Text.Json;

namespace Srm.Runtime.Sandbox;

// tier2-channel-c-mapped-folder: request/resultモデルがWaitForResult<T>で
// requestIdの一致判定に使えることを示す共通契約。既存のFocusRequestModel等は
// この設計以前から存在し、コンストラクタでの型制約を課さない専用メソッド対
// （TryReadFocusRequest等）を引き続き使うため実装を強制しない（design.md
// Decision 2: 既存3パターンの新プリミティブへの移行は任意）。新規capability
// （tier2-file-transfer/tier2-process-monitor）のモデルはこれを実装する。
public interface IHasRequestId
{
    string RequestId { get; }
}

// host↔guest間のライフサイクル同期をマップフォルダ上の合図ファイルで行う
// （DC-010）。named pipe等のライブブローカーは使わず、DC-008のJSONファイル
// レジストリ方式と同じ「IPC・デーモンなし」の思想を踏襲する。
//
// チャネルB（オートパイロット、DC-017）: scenario.json/scenario-result.jsonも
// 同じ「ファイルの存在・内容をポーリングする」運用モデルで扱う。個々の操作を
// 都度やり取りするコマンドキューにはせず、シナリオ単位（1回投入・1回結果）に
// 粒度を変えることで、ファイルが際限なく積み上がる問題を構造的に避ける。
public class SandboxControlChannel
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _controlDir;

    public SandboxControlChannel(string controlDir) => _controlDir = controlDir;

    private string ReadyPath => Path.Combine(_controlDir, "ready.signal");
    private string StopPath => Path.Combine(_controlDir, "stop.signal");
    private string StoppedPath => Path.Combine(_controlDir, "stopped.signal");
    private string ScenarioPath => Path.Combine(_controlDir, "scenario.json");
    private string ScenarioResultPath => Path.Combine(_controlDir, "scenario-result.json");
    private string FocusRequestPath => Path.Combine(_controlDir, "focus-request.json");
    private string FocusResultPath => Path.Combine(_controlDir, "focus-result.json");
    private string DiagRequestPath => Path.Combine(_controlDir, "diag-request.json");
    private string DiagResultPath => Path.Combine(_controlDir, "diag-result.json");
    private string McpRequestPath => Path.Combine(_controlDir, "mcp-request.json");
    private string McpResultPath => Path.Combine(_controlDir, "mcp-result.json");

    public bool IsReady => File.Exists(ReadyPath);
    public bool IsStopRequested => File.Exists(StopPath);
    public bool IsStopped => File.Exists(StoppedPath);
    public bool HasScenario => File.Exists(ScenarioPath);

    public void SignalReady() => WriteSignal(ReadyPath);
    public void SignalStop() => WriteSignal(StopPath);
    public void SignalStopped() => WriteSignal(StoppedPath);

    public bool WaitForReady(TimeSpan timeout) => WaitFor(() => IsReady, timeout);
    public bool WaitForStop(TimeSpan timeout) => WaitFor(() => IsStopRequested, timeout);
    public bool WaitForStopped(TimeSpan timeout) => WaitFor(() => IsStopped, timeout);

    // ゲスト内（--nested）が stop.signal を待つ間はタイムアウトの概念がないため、
    // 期限なしでポーリングし続ける専用メソッドを用意する。
    public void BlockUntilStop() => BlockUntilStop(onScenarioDetected: null);

    // チャネルB（DC-017）: srm_runの完了（ready.signal）後にホスト側がscenario.jsonを
    // 書き込む場合があるため、起動直後に一度だけ確認するのではなく、stop.signalを
    // 待つ間ずっとscenario.jsonの出現を監視し続ける。現れた時点でonScenarioDetectedを
    // 一度だけ呼び出し、そのままstop待機に戻る（scenario.json未使用のTier2ポリシーでは
    // 従来のBlockUntilStop()と完全に等価に動作する）。
    public void BlockUntilStop(Action? onScenarioDetected) =>
        BlockUntilStop(onScenarioDetected, onFocusRequestDetected: null);

    // チャネルAのTier2フォーカス保証（tier2-channel-a-focus-guarantee）:
    // scenario.jsonと違いfocus-request.jsonはsend_key/send_mouseのたびに
    // 繰り返し上書きされうるため、一度だけ発火するscenarioHandledと同じ仕組みは
    // 使わず、requestIdの変化を都度検出してonFocusRequestDetectedを毎回呼び出す。
    public void BlockUntilStop(Action? onScenarioDetected, Action<FocusRequestModel>? onFocusRequestDetected) =>
        BlockUntilStop(onScenarioDetected, onFocusRequestDetected, onDiagRequestDetected: null);

    // 診断チャネル（DC-016調査用）: diag-request.jsonもfocus-request.jsonと同じく
    // 都度上書きされうるため、requestIdの変化を都度検出してonDiagRequestDetectedを
    // 毎回呼び出す。
    public void BlockUntilStop(
        Action? onScenarioDetected,
        Action<FocusRequestModel>? onFocusRequestDetected,
        Action<DiagRequestModel>? onDiagRequestDetected) =>
        BlockUntilStop(onScenarioDetected, onFocusRequestDetected, onDiagRequestDetected, genericRequestHandlers: null);

    // tier2-channel-c-mapped-folder: scenario/focus/diagのように型付きの専用
    // パラメータを毎回追加する代わりに、新規capability（tier2-file-transfer・
    // tier2-process-monitor等）は`kind`文字列で登録するハンドラ辞書を使う
    // （design.md Decision 2、「登録されたrequestハンドラの辞書を毎周回す」形）。
    // ハンドラは新しいrequestIdだけを受け取り、実際のリクエスト本体の取得
    // （TryReadRequest<T>(kind)）はハンドラ自身の責務とする（BlockUntilStop側は
    // 各kindのリクエスト型を知らずに済む）。
    public void BlockUntilStop(
        Action? onScenarioDetected,
        Action<FocusRequestModel>? onFocusRequestDetected,
        Action<DiagRequestModel>? onDiagRequestDetected,
        IReadOnlyDictionary<string, Action<string>>? genericRequestHandlers)
    {
        var scenarioHandled = false;
        string? lastFocusRequestId = null;
        string? lastDiagRequestId = null;
        var lastGenericRequestIds = new Dictionary<string, string?>();
        while (!IsStopRequested)
        {
            if (!scenarioHandled && onScenarioDetected != null && HasScenario)
            {
                scenarioHandled = true;
                onScenarioDetected();
            }

            if (onFocusRequestDetected != null)
            {
                var request = TryReadFocusRequest();
                if (request != null && request.RequestId != lastFocusRequestId)
                {
                    lastFocusRequestId = request.RequestId;
                    onFocusRequestDetected(request);
                }
            }

            if (onDiagRequestDetected != null)
            {
                var request = TryReadDiagRequest();
                if (request != null && request.RequestId != lastDiagRequestId)
                {
                    lastDiagRequestId = request.RequestId;
                    onDiagRequestDetected(request);
                }
            }

            if (genericRequestHandlers != null)
            {
                foreach (var (kind, handler) in genericRequestHandlers)
                {
                    var currentId = TryReadRequestId(kind);
                    lastGenericRequestIds.TryGetValue(kind, out var lastId);
                    if (currentId != null && currentId != lastId)
                    {
                        lastGenericRequestIds[kind] = currentId;
                        handler(currentId);
                    }
                }
            }

            Thread.Sleep(PollInterval);
        }
    }

    // host側: VM起動前にpolicy.yamlと同様「起動前に一度だけ配置」する
    // （SandboxLauncher参照）。
    public void WriteScenario(ScenarioModel scenario)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(ScenarioPath, JsonSerializer.Serialize(scenario, JsonOpts));
    }

    // guest側: --nestedのメインループが起動直後に一度だけ読む。
    public ScenarioModel? ReadScenario()
    {
        if (!File.Exists(ScenarioPath)) return null;
        try { return JsonSerializer.Deserialize<ScenarioModel>(File.ReadAllText(ScenarioPath), JsonOpts); }
        catch { return null; }
    }

    // guest側: シナリオ実行完了時に一度だけ書く。
    public void WriteScenarioResult(ScenarioResultModel result)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(ScenarioResultPath, JsonSerializer.Serialize(result, JsonOpts));
    }

    public ScenarioResultModel? TryReadScenarioResult()
    {
        if (!File.Exists(ScenarioResultPath)) return null;
        try { return JsonSerializer.Deserialize<ScenarioResultModel>(File.ReadAllText(ScenarioResultPath), JsonOpts); }
        catch { return null; }
    }

    // host側: run_scenario投入後、完了をポーリングする。
    public ScenarioResultModel? WaitForScenarioResult(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = TryReadScenarioResult();
            if (result != null) return result;
            Thread.Sleep(PollInterval);
        }
        return TryReadScenarioResult();
    }

    // host側: send_key/send_mouseのたびに同じパスへ上書きする（蓄積しない）。
    public void WriteFocusRequest(FocusRequestModel request)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(FocusRequestPath, JsonSerializer.Serialize(request, JsonOpts));
    }

    // guest側: --nestedのBlockUntilStopループが毎回のポーリングで読む。
    public FocusRequestModel? TryReadFocusRequest()
    {
        if (!File.Exists(FocusRequestPath)) return null;
        try { return JsonSerializer.Deserialize<FocusRequestModel>(File.ReadAllText(FocusRequestPath), JsonOpts); }
        catch { return null; }
    }

    // guest側: フォーカス要求を処理し終えるたびに同じパスへ上書きする。
    public void WriteFocusResult(FocusResultModel result)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(FocusResultPath, JsonSerializer.Serialize(result, JsonOpts));
    }

    public FocusResultModel? TryReadFocusResult()
    {
        if (!File.Exists(FocusResultPath)) return null;
        try { return JsonSerializer.Deserialize<FocusResultModel>(File.ReadAllText(FocusResultPath), JsonOpts); }
        catch { return null; }
    }

    // host側: 自分が発行したrequestIdと一致する結果が出るまで待つ。古い結果
    // （直前の要求の使い回し）は無視してポーリングを継続する。
    public FocusResultModel? WaitForFocusResult(string requestId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = TryReadFocusResult();
            if (result != null && result.RequestId == requestId) return result;
            Thread.Sleep(PollInterval);
        }
        var last = TryReadFocusResult();
        return last != null && last.RequestId == requestId ? last : null;
    }

    // host側: srm diagのたびに同じパスへ上書きする（focus-requestと同じく蓄積しない）。
    public void WriteDiagRequest(DiagRequestModel request)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(DiagRequestPath, JsonSerializer.Serialize(request, JsonOpts));
    }

    // guest側: --nestedのBlockUntilStopループが毎回のポーリングで読む。
    public DiagRequestModel? TryReadDiagRequest()
    {
        if (!File.Exists(DiagRequestPath)) return null;
        try { return JsonSerializer.Deserialize<DiagRequestModel>(File.ReadAllText(DiagRequestPath), JsonOpts); }
        catch { return null; }
    }

    // guest側: サンプリングを終えるたびに同じパスへ上書きする。
    public void WriteDiagResult(DiagResultModel result)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(DiagResultPath, JsonSerializer.Serialize(result, JsonOpts));
    }

    public DiagResultModel? TryReadDiagResult()
    {
        if (!File.Exists(DiagResultPath)) return null;
        try { return JsonSerializer.Deserialize<DiagResultModel>(File.ReadAllText(DiagResultPath), JsonOpts); }
        catch { return null; }
    }

    // host側: 自分が発行したrequestIdと一致する結果が出るまで待つ。古い結果は無視する。
    public DiagResultModel? WaitForDiagResult(string requestId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = TryReadDiagResult();
            if (result != null && result.RequestId == requestId) return result;
            Thread.Sleep(PollInterval);
        }
        var last = TryReadDiagResult();
        return last != null && last.RequestId == requestId ? last : null;
    }

    // channel-d-guest-mcp-bridge: focus/diagとは呼び出し側の役割が逆になる点に注意。
    // ここではゲスト内MCPブリッジスタブがrequestを書き結果を待ち、ホスト側の
    // --mcp-bridge-hostプロセスがrequestを読み結果を書く（design.md参照。
    // 「ゲストが起点でホストが待ち受ける」初めてのチャネル）。

    // guest側: MCPツール呼び出しのたびに同じパスへ上書きする（蓄積しない）。
    public void WriteMcpRequest(McpRequestModel request)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(McpRequestPath, JsonSerializer.Serialize(request, JsonOpts));
    }

    // host側（--mcp-bridge-host）: ポーリングループで毎回読む。
    public McpRequestModel? TryReadMcpRequest()
    {
        if (!File.Exists(McpRequestPath)) return null;
        try { return JsonSerializer.Deserialize<McpRequestModel>(File.ReadAllText(McpRequestPath), JsonOpts); }
        catch { return null; }
    }

    // host側: リクエストを処理し終えるたびに同じパスへ上書きする。
    public void WriteMcpResult(McpResultModel result)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(McpResultPath, JsonSerializer.Serialize(result, JsonOpts));
    }

    public McpResultModel? TryReadMcpResult()
    {
        if (!File.Exists(McpResultPath)) return null;
        try { return JsonSerializer.Deserialize<McpResultModel>(File.ReadAllText(McpResultPath), JsonOpts); }
        catch { return null; }
    }

    // guest側: 自分が発行したrequestIdと一致する結果が出るまで待つ。古い結果
    // （直前の呼び出しの使い回し）は無視してポーリングを継続する。
    public McpResultModel? WaitForMcpResult(string requestId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = TryReadMcpResult();
            if (result != null && result.RequestId == requestId) return result;
            Thread.Sleep(PollInterval);
        }
        var last = TryReadMcpResult();
        return last != null && last.RequestId == requestId ? last : null;
    }

    // tier2-channel-c-mapped-folder: scenario/focus/diag/mcpの4パターンで
    // コピー&ペーストになっていたrequest/resultのやり取りを、`kind`文字列
    // （例: "file-transfer"・"process-monitor"）でファイル名を決める汎用対に
    // 抽出したもの。新規capabilityはこちらを使い、既存4パターンは変更しない
    // （design.md Decision 2、移行は任意）。ファイル名は`{kind}-request.json`/
    // `{kind}-result.json`に統一する。
    public void WriteRequest<TRequest>(string kind, TRequest request) where TRequest : IHasRequestId
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(RequestPath(kind), JsonSerializer.Serialize(request, JsonOpts));
    }

    public TRequest? TryReadRequest<TRequest>(string kind)
    {
        var path = RequestPath(kind);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<TRequest>(File.ReadAllText(path), JsonOpts); }
        catch { return default; }
    }

    public void WriteResult<TResult>(string kind, TResult result)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(ResultPath(kind), JsonSerializer.Serialize(result, JsonOpts));
    }

    public TResult? TryReadResult<TResult>(string kind)
    {
        var path = ResultPath(kind);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<TResult>(File.ReadAllText(path), JsonOpts); }
        catch { return default; }
    }

    // host側: 自分が発行したrequestIdと一致する結果が出るまで待つ。既存4パターンの
    // WaitForXxxResultと同じロジック（古い結果は無視してポーリング継続、
    // タイムアウト時は最後にもう一度だけ確認する）。
    public TResult? WaitForResult<TResult>(string kind, string requestId, TimeSpan timeout) where TResult : IHasRequestId
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = TryReadResult<TResult>(kind);
            if (result != null && result.RequestId == requestId) return result;
            Thread.Sleep(PollInterval);
        }
        var last = TryReadResult<TResult>(kind);
        return last != null && last.RequestId == requestId ? last : default;
    }

    // guest側BlockUntilStopの汎用ハンドラ登録用。requestIdの型を知らないまま
    // 変化検出だけを行うため、JSONを完全にデシリアライズせず"requestId"
    // プロパティだけを読む（既存4パターンの型付きTryReadXxxRequestと違い、
    // どのkindでも同じロジックで動く）。実際のリクエスト本体の取得は
    // ハンドラ自身がTryReadRequest<T>(kind)で行う。
    public string? TryReadRequestId(string kind)
    {
        var path = RequestPath(kind);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("requestId", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private string RequestPath(string kind) => Path.Combine(_controlDir, $"{kind}-request.json");
    private string ResultPath(string kind) => Path.Combine(_controlDir, $"{kind}-result.json");

    private void WriteSignal(string path)
    {
        Directory.CreateDirectory(_controlDir);
        File.WriteAllText(path, DateTimeOffset.Now.ToString("O"));
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(PollInterval);
        }
        return condition();
    }
}
