using Srm.PolicyEngine.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Srm.PolicyEngine;

public class PolicyWriter
{
    private readonly ISerializer _serializer;

    public PolicyWriter()
    {
        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
    }

    public string Serialize(PolicyModel policy) => _serializer.Serialize(policy);

    public void Save(PolicyModel policy, string policyPath) =>
        File.WriteAllText(policyPath, Serialize(policy));
}
