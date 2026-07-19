using System.Security.Cryptography;

namespace Srm.PolicyIntegrity;

public static class HashCalculator
{
    public static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
