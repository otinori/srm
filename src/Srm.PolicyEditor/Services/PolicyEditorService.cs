using System.IO;
using Srm.PolicyEngine;
using Srm.PolicyEngine.Models;
using Srm.PolicyIntegrity;

namespace Srm.PolicyEditor.Services;

public class PolicyEditorService
{
    private readonly PolicyLoader _loader = new();
    private readonly PolicyWriter _writer = new();
    private readonly PolicyValidator _validator = new();
    private readonly IntegrityWriter _integrityWriter = new();

    /// <summary>%VAR% 展開前の生の値でポリシーを読み込む（編集フォーム用）。</summary>
    public PolicyModel LoadPolicy(string path) => _loader.LoadRaw(path);

    public ValidationResult Validate(PolicyModel policy) => _validator.Validate(policy);

    /// <summary>
    /// バリデーションに成功した場合のみYAMLを書き込み、整合性サイドカーを再生成する。
    /// 不正な内容で既存ファイルを上書きしないよう、検証を先に行う。
    /// </summary>
    public (bool Success, ValidationResult Validation, string? SidecarPath) SaveAndSign(PolicyModel policy, string path)
    {
        var validation = Validate(policy);
        if (!validation.IsValid)
            return (false, validation, null);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _writer.Save(policy, path);
        var sidecarPath = _integrityWriter.Write(path);
        return (true, validation, sidecarPath);
    }

    public bool VerifySidecar(string path)
    {
        try
        {
            new IntegrityVerifier().Verify(path);
            return true;
        }
        catch (IntegrityException)
        {
            return false;
        }
    }
}
