using System.Windows;
using System.Windows.Controls;
using Srm.PolicyEditor.Services;
using Srm.PolicyEditor.ViewModels;
using Srm.PolicyEditor.Views;

namespace Srm.PolicyEditor;

// DIコンテナは使わず、規模に見合う最小構成でサービスをフィールドとして直接保持する
// （旧Blazor Server版のASP.NET Core DIから単純化。DC-014）。
public partial class MainWindow : Window
{
    private readonly PolicyDirectoryResolver _resolver = new();
    private readonly PolicyEditorService _editor = new();
    private readonly PolicyScaffoldService _scaffold = new();

    public MainWindow()
    {
        InitializeComponent();
        ShowList();
    }

    private void ShowList()
    {
        var vm = new PolicyListViewModel(_resolver, _editor);
        vm.NavigateToEdit += ShowEdit;
        SetContent(new PolicyListView(vm));
    }

    private void ShowEdit(string? name)
    {
        var vm = new PolicyEditViewModel(_resolver, _editor, _scaffold, name);
        vm.BackRequested += ShowList;
        // 作成モードでの初回保存後、Blazor版のNavigateTo相当として編集モードへ遷移し直す。
        vm.Saved += savedName => ShowEdit(savedName);
        SetContent(new PolicyEditView(vm));
    }

    private void SetContent(UIElement view)
    {
        RootGrid.Children.Clear();
        RootGrid.Children.Add(view);
    }
}
