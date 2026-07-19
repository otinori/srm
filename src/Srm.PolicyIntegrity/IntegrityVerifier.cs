namespace Srm.PolicyIntegrity;

public class IntegrityVerifier
{
    public void Verify(string policyPath)
    {
        var sidecarPath = IntegrityWriter.GetSidecarPath(policyPath);

        if (!File.Exists(sidecarPath))
            throw new IntegrityException(
                $"整合性サイドカーが見つかりません: {sidecarPath}\n" +
                $"次を実行してサイドカーを生成してください: srm validate {Path.GetFileNameWithoutExtension(policyPath)} --sign");

        var expected = File.ReadAllText(sidecarPath).Trim().ToLowerInvariant();
        var actual = HashCalculator.ComputeFileSha256(policyPath);

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException(
                $"ポリシーファイルが改ざんされています: {policyPath}\n" +
                $"期待値: {expected}\n実際の値: {actual}\n" +
                $"ポリシーを意図的に変更した場合は再度署名してください: srm validate {Path.GetFileNameWithoutExtension(policyPath)} --sign");
    }
}

public class IntegrityException : Exception
{
    public IntegrityException(string message) : base(message) { }
}
