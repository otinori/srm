using System.IO;
using System.Text.RegularExpressions;
using Srm.PolicyEngine.Models;

namespace Srm.PolicyEditor.Services;

public class PolicyScaffoldResult
{
    public string Folder { get; init; } = "";
    public List<string> ExecutableCandidates { get; init; } = new();
    public PolicyModel Draft { get; init; } = new();
    public List<string> HostCandidates { get; init; } = new();
}

/// <summary>
/// 対象フォルダを軽量スキャン（フォルダ構造+文字列抽出のみ、PE解析は行わない）して
/// ポリシーのひな型を提案する。結果はあくまで「候補」であり、最終判断はユーザーが行う。
/// </summary>
public class PolicyScaffoldService
{
    private const int MaxExecutables = 500;
    private const int MaxScanFiles = 20;
    private const int MaxBytesPerFile = 5 * 1024 * 1024;
    private const int MaxHostCandidates = 50;
    private const int MinStringLength = 5;

    private static readonly string[] WritableSubfolderNames =
        { "log", "logs", "data", "save", "saves", "config", "cache", "temp", "tmp", "settings" };

    private static readonly HashSet<string> KnownTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "io", "co", "dev", "app", "cloud", "gov", "edu",
        "jp", "uk", "de", "fr", "cn", "ru", "info", "biz", "me", "tv", "ai",
        "ly", "to", "gg", "xyz", "us", "ca", "au", "in", "br", "nl", "es", "it",
    };

    private static readonly Regex HostPattern = new(
        @"\b(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,24}\b",
        RegexOptions.Compiled);

    public PolicyScaffoldResult Scan(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"フォルダが見つかりません: {folder}");

        var executables = FindExecutables(folder);
        var chosenExe = executables.FirstOrDefault() ?? "";

        var scanTargets = executables.Take(1)
            .Concat(Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
            .Take(MaxScanFiles)
            .ToList();

        var hostCandidates = SuggestHosts(scanTargets);

        var draft = new PolicyModel
        {
            Name = SuggestName(folder),
            Tier = 1,
            Application = new ApplicationConfig
            {
                Executable = chosenExe,
                WorkingDirectory = folder,
            },
            Filesystem = new FilesystemPolicy { AllowPaths = SuggestAllowPaths(folder) },
        };

        return new PolicyScaffoldResult
        {
            Folder = folder,
            ExecutableCandidates = executables,
            Draft = draft,
            HostCandidates = hostCandidates,
        };
    }

    private static string SuggestName(string folder) =>
        Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public List<string> FindExecutables(string folder) =>
        Directory.EnumerateFiles(folder, "*.exe", SearchOption.AllDirectories)
            .Take(MaxExecutables)
            .OrderBy(f => f)
            .ToList();

    public List<AllowedPath> SuggestAllowPaths(string folder)
    {
        var paths = new List<AllowedPath> { new() { Path = folder, Access = "r" } };

        foreach (var sub in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(sub);
            if (WritableSubfolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                paths.Add(new AllowedPath { Path = sub, Access = "rw" });
        }

        return paths;
    }

    public List<string> SuggestHosts(IEnumerable<string> files)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (candidates.Count >= MaxHostCandidates)
                break;

            byte[] bytes;
            try
            {
                using var stream = File.OpenRead(file);
                var length = (int)Math.Min(stream.Length, MaxBytesPerFile);
                bytes = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = stream.Read(bytes, read, length - read);
                    if (n == 0) break;
                    read += n;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var s in ExtractPrintableStrings(bytes))
            {
                foreach (Match m in HostPattern.Matches(s))
                {
                    var host = m.Value;
                    var tld = host[(host.LastIndexOf('.') + 1)..];
                    if (!KnownTlds.Contains(tld))
                        continue;

                    candidates.Add(host.ToLowerInvariant());
                    if (candidates.Count >= MaxHostCandidates)
                        break;
                }
            }
        }

        return candidates.OrderBy(h => h).ToList();
    }

    /// <summary>ASCII および UTF-16LE の印字可能文字列ランを抽出する（簡易 `strings` 相当）。</summary>
    private static IEnumerable<string> ExtractPrintableStrings(byte[] bytes)
    {
        // ASCII ラン
        var start = -1;
        for (var i = 0; i < bytes.Length; i++)
        {
            var isPrintable = bytes[i] is >= 0x20 and <= 0x7E;
            if (isPrintable)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                if (i - start >= MinStringLength)
                    yield return System.Text.Encoding.ASCII.GetString(bytes, start, i - start);
                start = -1;
            }
        }
        if (start >= 0 && bytes.Length - start >= MinStringLength)
            yield return System.Text.Encoding.ASCII.GetString(bytes, start, bytes.Length - start);

        // UTF-16LE ラン（1バイト目が印字可能ASCII、2バイト目が0x00）
        start = -1;
        var i16 = 0;
        for (; i16 + 1 < bytes.Length; i16 += 2)
        {
            var isPrintable = bytes[i16] is >= 0x20 and <= 0x7E && bytes[i16 + 1] == 0x00;
            if (isPrintable)
            {
                if (start < 0) start = i16;
            }
            else if (start >= 0)
            {
                var charCount = (i16 - start) / 2;
                if (charCount >= MinStringLength)
                    yield return System.Text.Encoding.Unicode.GetString(bytes, start, i16 - start);
                start = -1;
            }
        }
        if (start >= 0 && (bytes.Length - start) / 2 >= MinStringLength)
            yield return System.Text.Encoding.Unicode.GetString(bytes, start, bytes.Length - start - (bytes.Length - start) % 2);
    }
}
