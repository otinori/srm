namespace Srm.Runtime.Operations;

// CLI（Environment.Exit相当）とMCP（構造化エラー）の両方から使える、既知の失敗系統
// 一本化した例外。ExitCodeは既存CLIのexit codeとの後方互換のために保持する
// （RunCommand/StopCommand/ValidateCommandが元々使っていた値をそのまま踏襲）。
public class SrmOperationException : Exception
{
    public int ExitCode { get; }

    public SrmOperationException(string message, int exitCode) : base(message) => ExitCode = exitCode;
}
