namespace Srm.PolicyIntegrity;

public class IntegrityWriter
{
    public string Write(string policyPath)
    {
        if (!File.Exists(policyPath))
            throw new FileNotFoundException($"ポリシーファイルが見つかりません: {policyPath}");

        var hash = HashCalculator.ComputeFileSha256(policyPath);
        var sidecarPath = GetSidecarPath(policyPath);
        File.WriteAllText(sidecarPath, hash);
        return sidecarPath;
    }

    public static string GetSidecarPath(string policyPath) => policyPath + ".sha256";
}
