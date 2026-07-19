using System.Runtime.InteropServices;

namespace Srm.Runtime;

// AppContainerLauncher/RestrictedAccountLauncherの両方が、対象プロセスへ独自の環境変数
// （channel-d-guest-mcp-bridgeのSRM_MCP_CONTROL_DIR/SRM_MCP_SERVERS等）を渡す際に使う
// Windows環境ブロック（"VAR1=value1\0VAR2=value2\0...\0\0"の二重null終端Unicode文字列）の
// 構築・解析を共通化する。土台となる環境変数の取得方法が起動経路ごとに異なる
// （AppContainerLauncherは呼び出し元(srm.exe)自身の環境をそのまま使うのに対し、
// RestrictedAccountLauncherは別アカウントのトークンから導出した環境を使う必要がある
// — でなければUSERPROFILE/TEMP等が呼び出し元(Administrator)のものになってしまう）ため、
// 土台の取得はそれぞれの呼び出し元に任せ、ここでは「マージしてブロックを作る」
// 「ブロックを辞書に戻す」という共通部分だけを提供する。
internal static class EnvironmentBlockBuilder
{
    internal static IntPtr Build(IReadOnlyDictionary<string, string> baseEnv, IReadOnlyDictionary<string, string> extra)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in baseEnv)
            merged[key] = value;
        foreach (var (key, value) in extra)
            merged[key] = value;

        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in merged)
            sb.Append(key).Append('=').Append(value).Append('\0');
        sb.Append('\0');

        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    // CreateEnvironmentBlock等が返す二重null終端Unicode環境ブロックを辞書へ変換する。
    internal static Dictionary<string, string> Parse(IntPtr blockPtr)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (blockPtr == IntPtr.Zero)
            return result;

        var offset = 0;
        while (true)
        {
            var entry = Marshal.PtrToStringUni(blockPtr + offset * sizeof(char));
            if (string.IsNullOrEmpty(entry))
                break;

            var eq = entry.IndexOf('=');
            // CreateEnvironmentBlockの先頭には"=C:=C:\..."のようなドライブごとの
            // カレントディレクトリ疑似変数（キーが空）が含まれることがあるため、
            // 2つ目の'='を境界に使う必要がある変数を除き、通常はそのまま無視してよい
            // （後続のBuildで再度書き出す際に消えても実害が無い、CreateProcess自体が
            // 常にこの疑似変数を再生成するため）。
            if (eq > 0)
                result[entry[..eq]] = entry[(eq + 1)..];

            offset += entry.Length + 1;
        }

        return result;
    }
}
