using System.Xml.Linq;

namespace Srm.Runtime.Sandbox;

public record SandboxLaunchSpec
{
    public required string ToolingHostDir { get; init; }
    public required string InputHostDir { get; init; }
    public required string OutboxHostDir { get; init; }
    public required string ControlHostDir { get; init; }

    // DC-012: provision.toolchainが指定されている場合のみ設定される。ホストが
    // 事前キャッシュしたポータブルツールチェーン（%ProgramData%\SRM\toolchain-cache\）
    // を読み取り専用でゲストへマウントする。
    public string? ProvisionHostDir { get; init; }

    public string GuestRoot { get; init; } = @"C:\srm";
    public int MemoryMb { get; init; } = 4096;

    // DC-011: Sandbox境界(Hyper-V Firewall)側でホスト/ドメイン単位のallow/denyを行う
    // 前提のtrue/false。allow_hostsが空かつprovision.networkがfalseならDisableにする。
    public bool NetworkingEnabled { get; init; }

    public required string LogonCommand { get; init; }
}

// Windows Sandbox の .wsb (XML) 設定を生成する（DC-010）。純粋な文字列/XML組み立てで
// Windows APIに依存しないため、Linux上でもユニットテスト可能。
public static class SandboxConfigGenerator
{
    public static string GuestToolingDir(string guestRoot) => $@"{guestRoot}\tooling";
    public static string GuestInputDir(string guestRoot) => $@"{guestRoot}\input";
    public static string GuestOutboxDir(string guestRoot) => $@"{guestRoot}\outbox";
    public static string GuestControlDir(string guestRoot) => $@"{guestRoot}\control";
    public static string GuestProvisionDir(string guestRoot) => $@"{guestRoot}\provision";

    public static XDocument Generate(SandboxLaunchSpec spec)
    {
        var mappedFolders = new XElement("MappedFolders",
            MappedFolder(spec.ToolingHostDir, GuestToolingDir(spec.GuestRoot), readOnly: true),
            MappedFolder(spec.InputHostDir, GuestInputDir(spec.GuestRoot), readOnly: true),
            MappedFolder(spec.OutboxHostDir, GuestOutboxDir(spec.GuestRoot), readOnly: false),
            MappedFolder(spec.ControlHostDir, GuestControlDir(spec.GuestRoot), readOnly: false));

        if (spec.ProvisionHostDir != null)
            mappedFolders.Add(MappedFolder(spec.ProvisionHostDir, GuestProvisionDir(spec.GuestRoot), readOnly: true));

        var configuration = new XElement("Configuration",
            mappedFolders,
            new XElement("Networking", spec.NetworkingEnabled ? "Default" : "Disable"),
            new XElement("MemoryInMB", spec.MemoryMb),
            new XElement("LogonCommand",
                new XElement("Command", spec.LogonCommand)));

        return new XDocument(configuration);
    }

    public static string GenerateXmlString(SandboxLaunchSpec spec) => Generate(spec).ToString();

    public static string WriteToFile(SandboxLaunchSpec spec, string path)
    {
        Generate(spec).Save(path);
        return path;
    }

    private static XElement MappedFolder(string hostFolder, string sandboxFolder, bool readOnly) =>
        new("MappedFolder",
            new XElement("HostFolder", hostFolder),
            new XElement("SandboxFolder", sandboxFolder),
            new XElement("ReadOnly", readOnly ? "true" : "false"));
}
