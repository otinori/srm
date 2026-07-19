using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Srm.PolicyEditor.Mvvm;
using Srm.PolicyEditor.Services;

namespace Srm.PolicyEditor.ViewModels;

public class PolicyRow
{
    public string Name { get; init; } = "";
    public bool Signed { get; init; }
}

public class PolicyListViewModel : ObservableObject
{
    private readonly PolicyDirectoryResolver _resolver;
    private readonly PolicyEditorService _editor;

    private string _folderInput = "";
    public string FolderInput { get => _folderInput; set => SetField(ref _folderInput, value); }

    private string? _folderError;
    public string? FolderError { get => _folderError; set => SetField(ref _folderError, value); }

    public ObservableCollection<PolicyRow> Policies { get; } = new();

    // null = 新規作成
    public event Action<string?>? NavigateToEdit;

    public ICommand BrowseFolderCommand { get; }
    public ICommand SwitchFolderCommand { get; }
    public ICommand NewPolicyCommand { get; }
    public ICommand EditPolicyCommand { get; }

    public PolicyListViewModel(PolicyDirectoryResolver resolver, PolicyEditorService editor)
    {
        _resolver = resolver;
        _editor = editor;

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        SwitchFolderCommand = new RelayCommand(SwitchFolder);
        NewPolicyCommand = new RelayCommand(() => NavigateToEdit?.Invoke(null));
        EditPolicyCommand = new RelayCommand(p => NavigateToEdit?.Invoke((string)p!));

        FolderInput = _resolver.PoliciesDirectory;
        RefreshList();
    }

    private void BrowseFolder()
    {
        var picked = NativeDialogService.PickFolder(FolderInput);
        if (picked is not null) FolderInput = picked;
    }

    private void SwitchFolder()
    {
        FolderError = null;
        var folder = FolderInput.Trim();

        if (!Directory.Exists(folder))
        {
            FolderError = $"フォルダが見つかりません: {folder}";
            return;
        }

        _resolver.PoliciesDirectory = folder;
        RefreshList();
    }

    public void RefreshList()
    {
        FolderInput = _resolver.PoliciesDirectory;
        Policies.Clear();
        foreach (var path in _resolver.ListPolicyFiles())
        {
            Policies.Add(new PolicyRow
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Signed = _editor.VerifySidecar(path),
            });
        }
    }
}
