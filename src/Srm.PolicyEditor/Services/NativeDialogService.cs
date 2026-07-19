using System.IO;
using Microsoft.Win32;

namespace Srm.PolicyEditor.Services;

// WPFアプリはメインスレッド自体がSTAのため、旧Blazor Server版のような専用STA
// スレッド起動（`RunOnStaThread`）は不要になった。.NET 8 WPFが標準搭載する
// `Microsoft.Win32.OpenFolderDialog`を使い、WinForms依存も解消する（DC-014）。
public static class NativeDialogService
{
    public static string? PickFolder(string? initialDirectory = null)
    {
        var dlg = new OpenFolderDialog();
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        // オーナーウィンドウを明示しないと、実機で「ダイアログが一切表示されず
        // ShowDialog()がtrue・FolderNameが空文字列のまま即座に返る」という
        // サイレント失敗が発生することを確認した（ユーザーには何も表示されないまま
        // 入力欄が空文字列で上書きされる）。MainWindowを明示的に渡して回避する。
        return dlg.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dlg.FolderName : null;
    }

    public static string? PickFile(
        string? initialDirectory = null,
        string filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*")
    {
        var dlg = new OpenFileDialog { Filter = filter };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;

        return dlg.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dlg.FileName : null;
    }
}
