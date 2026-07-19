using Srm.PolicyEngine.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Srm.PolicyEngine;

public class PolicyLoader
{
    private readonly EnvExpander _expander;
    private readonly IDeserializer _deserializer;

    public PolicyLoader(EnvExpander? expander = null)
    {
        _expander = expander ?? new EnvExpander();
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public PolicyModel Load(string policyPath)
    {
        var policy = LoadRaw(policyPath);
        ExpandPolicy(policy);
        return policy;
    }

    /// <summary>%VAR% 展開前の生のポリシー値を返す。編集UIなど、元の値をそのまま扱いたい用途向け。</summary>
    public PolicyModel LoadRaw(string policyPath)
    {
        if (!File.Exists(policyPath))
            throw new FileNotFoundException($"ポリシーファイルが見つかりません: {policyPath}\n次を確認してください: ファイルパスが正しいか、policies/ ディレクトリに配置されているか");

        var yaml = File.ReadAllText(policyPath);
        try
        {
            return _deserializer.Deserialize<PolicyModel>(yaml);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"ポリシーファイルのパースに失敗しました: {policyPath}\n{ex.Message}\n次を確認してください: YAMLの構文が正しいか");
        }
    }

    private void ExpandPolicy(PolicyModel policy)
    {
        var vars = policy.Variables;

        policy.Application.Executable = _expander.Expand(policy.Application.Executable, vars);
        policy.Application.Arguments = _expander.Expand(policy.Application.Arguments, vars);
        policy.Application.WorkingDirectory = _expander.Expand(policy.Application.WorkingDirectory, vars);

        foreach (var p in policy.Filesystem.AllowPaths)
            p.Path = _expander.Expand(p.Path, vars);
    }
}
