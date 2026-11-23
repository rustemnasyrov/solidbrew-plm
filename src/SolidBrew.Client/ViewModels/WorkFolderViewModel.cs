using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Вид «Рабочая папка»: состояние файлов по <see cref="WorkspaceService.GetStatusAsync"/>, включая черновики.</summary>
public sealed partial class WorkFolderViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public WorkFolderViewModel(MainViewModel main) => _main = main;

    private AppSession Session => _main.Session;
    private WorkflowController Flow => _main.Flow;

    public ObservableCollection<FileRow> Files { get; } = new();

    [ObservableProperty]
    private string _header = "Рабочая папка не выбрана.";

    [ObservableProperty]
    private WorkspaceStatus? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand), nameof(ShowDocumentCommand), nameof(RenameCommand), nameof(ShowFileInExplorerCommand))]
    private FileRow? _selectedFile;

    public bool CanModify => Session.CanModify;

    public bool IsWorkspace => Flow.WorkRoot != null && WorkspaceService.IsWorkspace(Flow.WorkRoot);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var root = Flow.WorkRoot;
        OnPropertyChanged(nameof(IsWorkspace));
        if (root == null)
        {
            Files.Clear();
            Header = "Рабочая папка не выбрана. Выберите папку вверху окна или откройте документ из хранилища.";
            return;
        }
        if (!WorkspaceService.IsWorkspace(root))
        {
            Files.Clear();
            Header = $"«{root}» — ещё не рабочая папка SolidBrew. Откройте в неё документ из хранилища (кнопка «Открыть» в обзоре).";
            return;
        }
        var ws = Session.Workspace;
        var outcome = await Flow.Busy.RunAsync("Проверка рабочей папки", (p, ct) => ws.GetStatusAsync(root, true, p, ct));
        if (!outcome.Ok) return;
        var status = outcome.Value!;
        Status = status;
        var keep = new HashSet<string>(Files.Where(f => f.IsSelected).Select(f => f.RelPath), StringComparer.OrdinalIgnoreCase);
        Files.ReplaceWith(status.Files.Select(f => new FileRow(f, this) { IsSelected = keep.Contains(f.RelPath) }));
        var drafts = status.Files.Count(f => f.Local == LocalFileState.Draft);
        var modified = status.Files.Count(f => f.Local == LocalFileState.ModifiedLocally);
        var mine = status.Files.Count(f => f.Lock == LockState.LockedByMe);
        var outdated = status.Files.Count(f => f.IsOutdated);
        Header = $"{root}: документов {status.Files.Count - drafts}, черновиков {drafts}, изменено {modified}, взято мной {mine}, устарело {outdated}." +
                 (status.ServerChecked ? string.Empty : " Сервер не опрошен: " + status.ServerError);
        if (status.Messages.Count > 0) _main.AddNotices(status.Messages);
        Flow.Busy.Done(Header);
        this.NotifyCommands();
    }

    internal void OnSelectionChanged() => this.NotifyCommands();

    private List<FileRow> Selected()
    {
        var list = Files.Where(f => f.IsSelected).ToList();
        if (list.Count == 0 && SelectedFile != null) list.Add(SelectedFile);
        return list;
    }

    private List<Guid> SelectedIds() => Selected().Where(f => f.DocumentId != null).Select(f => f.DocumentId!.Value).Distinct().ToList();

    private bool HasDocuments() => Selected().Any(f => f.DocumentId != null);

    private bool HasFile() => SelectedFile != null;

    private bool HasDocument() => SelectedFile?.DocumentId != null;

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private Task CheckOutAsync() => Flow.CheckOutAsync(SelectedIds());

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private Task UndoCheckOutAsync() => Flow.UndoCheckOutAsync(SelectedIds());

    [RelayCommand]
    private Task CheckinAsync() => Flow.CheckinAsync();

    [RelayCommand(CanExecute = nameof(HasDocuments))]
    private Task UpdateToLatestAsync() => Flow.UpdateToLatestAsync(SelectedIds());

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Rename() => Flow.Rename(SelectedFile!.RelPath);

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void OpenFile() => Flow.OpenFile(SelectedFile!.FullPath);

    [RelayCommand(CanExecute = nameof(HasFile))]
    private void ShowFileInExplorer() => Flow.ShowInExplorer(SelectedFile!.FullPath);

    [RelayCommand]
    private void ShowFolder()
    {
        if (Flow.WorkRoot != null) Flow.ShowInExplorer(Flow.WorkRoot);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task ShowDocumentAsync() => _main.ShowDocumentAsync(SelectedFile!.DocumentId!.Value, SelectedFile.LocalVersion, DocumentSection.Properties);

    /// <summary>Комплект головного документа рабочей папки.</summary>
    [RelayCommand]
    private async Task ShowHeadKitAsync()
    {
        var root = Flow.WorkRoot;
        if (root == null || !WorkspaceService.IsWorkspace(root)) return;
        var state = Session.Workspace.LoadState(root);
        if (state.HeadDocumentId is { } head) await _main.ShowKitAsync(head, state.HeadVersion);
    }
}

public sealed partial class FileRow : ObservableObject
{
    private readonly WorkFolderViewModel _owner;

    public FileRow(FileStatus status, WorkFolderViewModel owner)
    {
        Status = status;
        _owner = owner;
    }

    public FileStatus Status { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.OnSelectionChanged();

    public string RelPath => Status.RelPath;
    public string FullPath => Status.FullPath;
    public string FileName => Status.FileName;
    public Guid? DocumentId => Status.DocumentId;
    public int? LocalVersion => Status.LocalVersion;
    public string? Designation => Status.Designation;
    public string? Name => Status.Name;
    public string LocalVersionText => Status.LocalVersion is { } v ? $"v{v}" : "черновик";
    public string LatestVersionText => Status.LatestVersion is { } v ? $"v{v}" : string.Empty;
    public string StateText => Status.StateText + (Status.Lock == LockState.LockedByOther && Status.LockInfo != null ? $" (с {Format.Time(Status.LockInfo.At)})" : string.Empty);
    public string ReadOnlyText => Status.IsReadOnly ? "только чтение" : string.Empty;

    public StateMark Mark
    {
        get
        {
            if (Status.Local == LocalFileState.Draft) return StateMark.Draft;
            if (Status.Local == LocalFileState.MissingLocally) return StateMark.Missing;
            if (Status.Lock == LockState.LockedByOther) return StateMark.Other;
            if (Status.Local == LocalFileState.ModifiedLocally) return StateMark.Modified;
            if (Status.Lock == LockState.LockedByMe) return StateMark.Mine;
            if (Status.IsOutdated) return StateMark.Outdated;
            return Status.Lock == LockState.Free ? StateMark.Free : StateMark.None;
        }
    }
}
