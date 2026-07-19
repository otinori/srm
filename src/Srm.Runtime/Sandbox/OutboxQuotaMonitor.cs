namespace Srm.Runtime.Sandbox;

// outboxディレクトリの合計サイズを計算し、閾値超過を判定する（DC-013）。
// 純粋なファイルI/Oのみで完結するため、実際のポーリングループ・プロセス起動を
// 行うCLI側の呼び出し元と分離してLinux上でもテスト可能にする。
public static class OutboxQuotaMonitor
{
    public static long ComputeSize(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { /* 列挙後に削除された等はベストエフォートで無視 */ }
        }
        return total;
    }

    public static bool IsOverQuota(string dir, long maxBytes) => ComputeSize(dir) > maxBytes;
}
