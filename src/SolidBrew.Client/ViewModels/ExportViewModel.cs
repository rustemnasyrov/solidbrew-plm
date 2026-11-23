using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Экран «Выгрузить комплект» (6.1 п. 7, 7.10): версия, производные файлы, куда сохранить ZIP.</summary>
public sealed partial class ExportViewModel : DialogViewModel
{
    private readonly AppSession _session;
    private readonly IShellService _shell;
    private readonly int? _initialVersion;

    public ExportViewModel(AppSession session, Guid documentId, int? version = null) : base(session.Services.Dialogs)
    {
        _session = session;
        _shell = session.Services.Shell;
        DocumentId = documentId;
        _initialVersion = version;
    }

    public override string Title => "Выгрузить комплект";

    public Guid DocumentId { get; }

    [ObservableProperty]
    private DocumentSummaryDto? _document;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private VersionDto? _selectedVersion;

    [ObservableProperty]
    private bool _includeDerived = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _destination = string.Empty;

    public ObservableCollection<VersionDto> Versions { get; } = new();

    public string DocumentText => Document == null ? string.Empty : Format.Doc(Document.RelPath, Document.Designation) + (Document.Name is { Length: > 0 } n ? " — " + n : string.Empty);

    public override async Task InitializeAsync()
    {
        var outcome = await Busy.RunAsync("Загрузка версий", (_, ct) => _session.Api.GetDocumentAsync(DocumentId, ct));
        if (!outcome.Ok) return;
        Document = outcome.Value!.Document;
        Versions.ReplaceWith(outcome.Value.Versions);
        SelectedVersion = Versions.FirstOrDefault(v => v.Number == (_initialVersion ?? Document.LatestVersion)) ?? Versions.FirstOrDefault();
        OnPropertyChanged(nameof(DocumentText));
        Busy.Done("Выберите версию и файл ZIP.");
    }

    partial void OnSelectedVersionChanged(VersionDto? value)
    {
        if (value == null || Document == null) return;
        var folder = string.IsNullOrEmpty(Destination) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Path.GetDirectoryName(Destination) ?? string.Empty;
        Destination = Path.Combine(folder, DefaultFileName(value.Number));
    }

    private string DefaultFileName(int version)
    {
        var name = Document?.Designation is { Length: > 0 } d ? d : Path.GetFileNameWithoutExtension(Document?.FileName ?? "Комплект");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return $"{name} v{version}.zip";
    }

    [RelayCommand]
    private void Browse()
    {
        var number = SelectedVersion?.Number ?? 1;
        var file = Dialogs.SaveFile("Куда сохранить комплект", "Архив ZIP (*.zip)|*.zip", DefaultFileName(number),
            string.IsNullOrEmpty(Destination) ? null : Path.GetDirectoryName(Destination));
        if (file != null) Destination = file;
    }

    private bool CanExport() => !Busy.IsBusy && SelectedVersion != null && !string.IsNullOrWhiteSpace(Destination);

    [RelayCommand(CanExecute = nameof(CanExport))]
    public async Task ExportAsync()
    {
        var version = SelectedVersion!.Number;
        var dest = Destination.Trim();
        if (!dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) dest += ".zip";
        if (File.Exists(dest) && !Dialogs.Confirm("Файл существует", $"Файл «{dest}» уже есть. Заменить?", "Заменить", "Отмена"))
            return;
        var derived = IncludeDerived;
        var ok = await Busy.RunAsync("Выгрузка комплекта", (p, ct) => _session.Workspace.ExportAsync(DocumentId, version, derived, dest, p, ct));
        if (!ok) return;
        Busy.Done($"Комплект выгружен: {dest}");
        if (Dialogs.Confirm("Комплект выгружен", $"Комплект v{version} сохранён в «{dest}» вместе с перечнем версий и хешей (manifest.json).", "Показать в проводнике", "Закрыть"))
        {
            try
            {
                _shell.ShowInExplorer(dest);
            }
            catch (Exception ex)
            {
                Dialogs.ShowError("Показать в проводнике", ErrorText.Describe(ex));
            }
        }
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
