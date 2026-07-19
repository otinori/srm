namespace Srm.Mcp.Tools;

// Program.csで解決したポリシーディレクトリをDI経由で各ツールへ渡すための薄いラッパー。
public record PoliciesDir(string Path);
