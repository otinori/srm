using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Srm.PolicyEditor.Mvvm;
using Srm.PolicyEditor.Services;
using Srm.PolicyEngine.Models;

namespace Srm.PolicyEditor.ViewModels;

public class VariableRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class HostRow
{
    public string Value { get; set; } = "";
}

public class HostCandidateRow
{
    public string Value { get; set; } = "";
    public bool Selected { get; set; }
}

// filesystem.allow_paths の1行分。AllowedPath自体はINotifyPropertyChangedを実装しない
// プレーンなPOCOのため、「参照...」ボタンでコードから値を書き換えてもUIへ自動反映
// されない。専用のオブザーバブルな行クラスとして扱うことでこれを避ける。
public class AllowedPathRow : ObservableObject
{
    private string _path = "";
    public string Path { get => _path; set => SetField(ref _path, value); }

    private string _access = "r";
    public string Access { get => _access; set => SetField(ref _access, value); }

    public static AllowedPathRow From(AllowedPath p) => new() { Path = p.Path, Access = p.Access };
    public AllowedPath ToModel() => new() { Path = Path, Access = Access };
}

public class PolicyEditViewModel : ObservableObject
{
    private readonly PolicyDirectoryResolver _resolver;
    private readonly PolicyEditorService _editor;
    private readonly PolicyScaffoldService _scaffold;
    private readonly string? _routeName;

    private string? _originalPath;

    public bool IsCreateMode => _routeName is null;

    public PolicyModel Model { get; private set; } = new();

    // Model.Name / Application.Executable / Application.WorkingDirectory は
    // 「参照...」ボタンや対象フォルダからの解析でコードから直接書き換えることがあるため、
    // 単純な `{Binding Model.X}` パスバインドだと変更がUIへ反映されない
    // （中間のApplicationConfigがINotifyPropertyChanged非対応のため）。
    // ViewModel自身のプロパティとしてラップし、常にPropertyChangedを発火させる。
    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } }
    }

    public string Executable
    {
        get => Model.Application.Executable;
        set { if (Model.Application.Executable != value) { Model.Application.Executable = value; OnPropertyChanged(); } }
    }

    public string WorkingDirectory
    {
        get => Model.Application.WorkingDirectory;
        set { if (Model.Application.WorkingDirectory != value) { Model.Application.WorkingDirectory = value; OnPropertyChanged(); } }
    }

    public bool IsTier1
    {
        get => Model.Tier == 1;
        set { if (value) { Model.Tier = 1; OnPropertyChanged(nameof(IsTier1)); OnPropertyChanged(nameof(IsTier2)); } }
    }

    public bool IsTier2
    {
        get => Model.Tier == 2;
        set { if (value) { Model.Tier = 2; OnPropertyChanged(nameof(IsTier1)); OnPropertyChanged(nameof(IsTier2)); } }
    }

    public ObservableCollection<VariableRow> Variables { get; } = new();
    public ObservableCollection<AllowedPathRow> AllowPaths { get; } = new();
    public ObservableCollection<HostRow> Hosts { get; } = new();
    public ObservableCollection<HostCandidateRow> HostCandidates { get; } = new();
    public ObservableCollection<string> ExecutableCandidates { get; } = new();

    private List<string> _errors = new();
    public List<string> Errors { get => _errors; private set { SetField(ref _errors, value); OnPropertyChanged(nameof(HasErrors)); } }
    public bool HasErrors => Errors.Count > 0;

    private string? _successMessage;
    public string? SuccessMessage { get => _successMessage; private set => SetField(ref _successMessage, value); }

    private string _scaffoldFolder = "";
    public string ScaffoldFolder { get => _scaffoldFolder; set => SetField(ref _scaffoldFolder, value); }

    private string? _scaffoldError;
    public string? ScaffoldError { get => _scaffoldError; private set => SetField(ref _scaffoldError, value); }

    private string _saveDirectory = "";
    public string SaveDirectory { get => _saveDirectory; set => SetField(ref _saveDirectory, value); }

    public string Title => IsCreateMode ? "新規ポリシー作成" : $"ポリシー編集: {_routeName}";

    // 保存に成功した実行ファイル名。作成モードでの初回保存後、編集モードへ
    // 遷移し直すために使う（Blazor版のNavigateTo相当）。
    public event Action<string>? Saved;
    public event Action? BackRequested;

    public ICommand BackCommand { get; }
    public ICommand BrowseSaveDirectoryCommand { get; }
    public ICommand BrowseScaffoldFolderCommand { get; }
    public ICommand RunScaffoldCommand { get; }
    public ICommand ApplySelectedHostsCommand { get; }
    public ICommand BrowseExecutableCommand { get; }
    public ICommand BrowseWorkingDirectoryCommand { get; }
    public ICommand BrowseAllowPathCommand { get; }
    public ICommand AddVariableCommand { get; }
    public ICommand RemoveVariableCommand { get; }
    public ICommand AddAllowPathCommand { get; }
    public ICommand RemoveAllowPathCommand { get; }
    public ICommand AddHostCommand { get; }
    public ICommand RemoveHostCommand { get; }
    public ICommand SaveCommand { get; }

    public PolicyEditViewModel(
        PolicyDirectoryResolver resolver,
        PolicyEditorService editor,
        PolicyScaffoldService scaffold,
        string? routeName)
    {
        _resolver = resolver;
        _editor = editor;
        _scaffold = scaffold;
        _routeName = routeName;

        BackCommand = new RelayCommand(() => BackRequested?.Invoke());
        BrowseSaveDirectoryCommand = new RelayCommand(BrowseSaveDirectory);
        BrowseScaffoldFolderCommand = new RelayCommand(BrowseScaffoldFolder);
        RunScaffoldCommand = new RelayCommand(RunScaffold);
        ApplySelectedHostsCommand = new RelayCommand(ApplySelectedHosts);
        BrowseExecutableCommand = new RelayCommand(BrowseExecutable);
        BrowseWorkingDirectoryCommand = new RelayCommand(BrowseWorkingDirectory);
        BrowseAllowPathCommand = new RelayCommand(p => BrowseAllowPath((AllowedPathRow)p!));
        AddVariableCommand = new RelayCommand(() => Variables.Add(new VariableRow()));
        RemoveVariableCommand = new RelayCommand(p => Variables.Remove((VariableRow)p!));
        AddAllowPathCommand = new RelayCommand(() => AllowPaths.Add(new AllowedPathRow()));
        RemoveAllowPathCommand = new RelayCommand(p => AllowPaths.Remove((AllowedPathRow)p!));
        AddHostCommand = new RelayCommand(() => Hosts.Add(new HostRow()));
        RemoveHostCommand = new RelayCommand(p => Hosts.Remove((HostRow)p!));
        SaveCommand = new RelayCommand(Save);

        if (!IsCreateMode)
        {
            _originalPath = _resolver.GetPolicyPath(_routeName!);
            Model = _editor.LoadPolicy(_originalPath);
        }
        else
        {
            SaveDirectory = _resolver.PoliciesDirectory;
        }

        SyncFromModel();
    }

    private void SyncFromModel()
    {
        Variables.Clear();
        foreach (var kv in Model.Variables)
            Variables.Add(new VariableRow { Key = kv.Key, Value = kv.Value });

        AllowPaths.Clear();
        foreach (var p in Model.Filesystem.AllowPaths)
            AllowPaths.Add(AllowedPathRow.From(p));

        Hosts.Clear();
        foreach (var h in Model.Network.AllowHosts)
            Hosts.Add(new HostRow { Value = h });
    }

    private void BrowseSaveDirectory()
    {
        var picked = NativeDialogService.PickFolder(SaveDirectory);
        if (picked is not null) SaveDirectory = picked;
    }

    private void BrowseScaffoldFolder()
    {
        var picked = NativeDialogService.PickFolder(ScaffoldFolder);
        if (picked is not null) ScaffoldFolder = picked;
    }

    private void BrowseExecutable()
    {
        var picked = NativeDialogService.PickFile(Path.GetDirectoryName(Executable));
        if (picked is not null) Executable = picked;
    }

    private void BrowseWorkingDirectory()
    {
        var picked = NativeDialogService.PickFolder(WorkingDirectory);
        if (picked is not null) WorkingDirectory = picked;
    }

    private void BrowseAllowPath(AllowedPathRow row)
    {
        var picked = NativeDialogService.PickFolder(row.Path);
        if (picked is not null) row.Path = picked;
    }

    private void RunScaffold()
    {
        ScaffoldError = null;
        HostCandidates.Clear();
        ExecutableCandidates.Clear();

        if (string.IsNullOrWhiteSpace(ScaffoldFolder))
        {
            ScaffoldError = "フォルダパスを入力してください。";
            return;
        }

        try
        {
            var result = _scaffold.Scan(ScaffoldFolder);
            foreach (var exe in result.ExecutableCandidates) ExecutableCandidates.Add(exe);
            foreach (var h in result.HostCandidates) HostCandidates.Add(new HostCandidateRow { Value = h });

            Name = result.Draft.Name;
            Executable = result.Draft.Application.Executable;
            WorkingDirectory = result.Draft.Application.WorkingDirectory;

            AllowPaths.Clear();
            foreach (var p in result.Draft.Filesystem.AllowPaths)
                AllowPaths.Add(AllowedPathRow.From(p));
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            ScaffoldError = ex.Message;
        }
    }

    private void ApplySelectedHosts()
    {
        foreach (var h in HostCandidates.Where(h => h.Selected))
        {
            if (!Hosts.Any(r => string.Equals(r.Value, h.Value, StringComparison.OrdinalIgnoreCase)))
                Hosts.Add(new HostRow { Value = h.Value });
        }
    }

    private void Save()
    {
        SuccessMessage = null;

        Model.Variables = Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .ToDictionary(v => v.Key, v => v.Value);
        Model.Network.AllowHosts = Hosts
            .Select(h => h.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        Model.Filesystem.AllowPaths = AllowPaths.Select(r => r.ToModel()).ToList();

        var path = _originalPath ?? Path.Combine(SaveDirectory, Model.Name + ".yaml");
        var (success, validation, sidecarPath) = _editor.SaveAndSign(Model, path);

        Errors = validation.Errors;
        if (!success) return;

        _originalPath = path;
        SuccessMessage = $"保存しました: {path}（サイドカー: {sidecarPath}）";

        if (IsCreateMode)
        {
            _resolver.PoliciesDirectory = SaveDirectory;
            Saved?.Invoke(Model.Name);
        }
    }
}
