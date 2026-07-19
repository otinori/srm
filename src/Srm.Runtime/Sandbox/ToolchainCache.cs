namespace Srm.Runtime.Sandbox;

// DC-012: ホストが事前にダウンロード・展開してキャッシュしたポータブル
// （xcopy展開可能）なツールチェーンの置き場所。バージョン解決・取得自体は
// srmの責務にせず、ユーザー側または既存ツール（npm/winget等）に委ねる。
// レイアウト: <Root>\<name>\<version>\*.zip
public static class ToolchainCache
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SRM", "toolchain-cache");

    public static string EntryDir(string name, string version) => Path.Combine(Root, name, version);
}
