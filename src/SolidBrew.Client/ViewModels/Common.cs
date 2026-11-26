using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Итог длительной операции.</summary>
public readonly record struct Outcome<T>(bool Ok, T? Value, ErrorInfo? Error, bool Canceled)
{
    public static Outcome<T> Success(T value) => new(true, value, null, false);
}

/// <summary>
/// Ход длительной операции: строка состояния, полоса хода и кнопка «Отменить».
/// Работа выполняется в пуле потоков, окно не блокируется; ошибки переводятся в понятный текст.
/// </summary>
public sealed partial class BusyState : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Готово";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private ErrorInfo? _lastError;

    /// <summary>Как показать ошибку (по умолчанию — только <see cref="LastError"/> и строка состояния).</summary>
    public Action<string, ErrorInfo>? ErrorHandler { get; set; }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Отмена…";
    }

    /// <summary>
    /// Выполнить операцию. Операции одного состояния выполняются по очереди: следующая ждёт окончания текущей
    /// (например, обновление дерева после «Взять в работу»).
    /// </summary>
    public async Task<Outcome<T>> RunAsync<T>(string title, Func<IProgress<WorkProgress>, CancellationToken, Task<T>> work, bool reportErrors = true)
    {
        await _gate.WaitAsync();
        try
        {
            return await RunExclusiveAsync(title, work, reportErrors);
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<Outcome<T>> RunExclusiveAsync<T>(string title, Func<IProgress<WorkProgress>, CancellationToken, Task<T>> work, bool reportErrors)
    {
        using var cts = new CancellationTokenSource();
        _cts = cts;
        LastError = null;
        IsBusy = true;
        IsIndeterminate = true;
        Progress = 0;
        StatusText = title + "…";
        var progress = new Progress<WorkProgress>(Report);
        try
        {
            var value = await Task.Run(() => work(progress, cts.Token), cts.Token);
            StatusText = "Готово";
            return Outcome<T>.Success(value);
        }
        catch (OperationCanceledException)
        {
            StatusText = title + ": отменено";
            return new Outcome<T>(false, default, new ErrorInfo("Операция отменена."), true);
        }
        catch (Exception ex)
        {
            var error = ErrorText.Describe(ex);
            LastError = error;
            StatusText = title + ": ошибка";
            if (reportErrors) ErrorHandler?.Invoke(title, error);
            return new Outcome<T>(false, default, error, false);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            IsIndeterminate = false;
            Progress = 0;
        }
    }

    public async Task<bool> RunAsync(string title, Func<IProgress<WorkProgress>, CancellationToken, Task> work, bool reportErrors = true)
    {
        var r = await RunAsync<bool>(title, async (p, ct) =>
        {
            await work(p, ct);
            return true;
        }, reportErrors);
        return r.Ok;
    }

    private void Report(WorkProgress p)
    {
        if (!IsBusy) return;
        if (!string.IsNullOrWhiteSpace(p.Message)) StatusText = p.Message;
        if (p.Fraction is { } f)
        {
            IsIndeterminate = false;
            Progress = f * 100;
        }
        else
        {
            IsIndeterminate = true;
        }
    }

    /// <summary>Итоговое сообщение в строке состояния.</summary>
    public void Done(string message) => StatusText = message;
}

/// <summary>Основа модального экрана.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    protected DialogViewModel(IDialogService dialogs)
    {
        Dialogs = dialogs;
        Busy.ErrorHandler = (title, error) => Dialogs.ShowError(title, error);
        Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusyState.IsBusy)) this.NotifyCommands();
        };
    }

    protected IDialogService Dialogs { get; }

    public abstract string Title { get; }

    public BusyState Busy { get; } = new();

    /// <summary>Итог окна (true — операция выполнена).</summary>
    public bool? DialogResult { get; private set; }

    public event EventHandler<bool?>? CloseRequested;

    protected void Close(bool? result)
    {
        DialogResult = result;
        CloseRequested?.Invoke(this, result);
    }

    /// <summary>Загрузка данных экрана; окно вызывает после показа.</summary>
    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Окно закрывают крестиком: можно ли (во время операции — нет).</summary>
    public virtual bool CanClose => !Busy.IsBusy;

    /// <summary>Вызывается окном после закрытия.</summary>
    public virtual void OnClosed()
    {
    }
}

/// <summary>Проблема плана для списка.</summary>
public sealed class ProblemItem
{
    public ProblemItem(PlanProblem p)
    {
        Severity = p.Severity;
        Message = p.Message;
        RelPath = p.RelPath;
    }

    public ProblemItem(ProblemSeverity severity, string message)
    {
        Severity = severity;
        Message = message;
    }

    public ProblemSeverity Severity { get; }
    public string Message { get; }
    public string? RelPath { get; }
    public bool IsBlocking => Severity == ProblemSeverity.Error;
    public bool IsWarning => Severity == ProblemSeverity.Warning;

    public string SeverityText => Severity switch
    {
        ProblemSeverity.Error => "Ошибка",
        ProblemSeverity.Warning => "Внимание",
        _ => "Сведения",
    };

    /// <summary>Глиф Segoe MDL2.</summary>
    public string Icon => Severity switch
    {
        ProblemSeverity.Error => "",
        ProblemSeverity.Warning => "",
        _ => "",
    };

    public static List<ProblemItem> From(IEnumerable<PlanProblem> problems) =>
        problems.GroupBy(p => (p.Severity, p.Message)).Select(g => new ProblemItem(g.First()))
            .OrderBy(p => p.Severity).ToList();
}

/// <summary>Цветовая метка состояния документа.</summary>
public enum StateMark
{
    None,
    Free,
    Mine,
    Other,
    Modified,
    Draft,
    Outdated,
    Missing,
}

/// <summary>Цветной кружок после значка документа в дереве (порядок важности: красный, жёлтый, синий, зелёный).</summary>
public enum StatusDot
{
    /// <summary>Без кружка (служебный узел, черновик, ссылка вне PDM).</summary>
    None,
    /// <summary>Зелёный: сдан в хранилище — свободен, правок нет.</summary>
    InStorage,
    /// <summary>Синий: взят мной, не изменён.</summary>
    Mine,
    /// <summary>Красный: взят мной и изменён.</summary>
    MineModified,
    /// <summary>Жёлтый: сборку или чертёж надо взять и пересохранить — изменилась или устарела входящая модель.</summary>
    NeedsResave,
    /// <summary>Оранжевый: взят другим.</summary>
    Other,
    /// <summary>Серый: файла нет в рабочей папке.</summary>
    Missing,
    /// <summary>Розовый: устарел — в хранилище есть более новая версия.</summary>
    Outdated,
    /// <summary>Фиолетовый: проблема (нет файла, правка без взятия, ссылки нет и т. п.).</summary>
    Problem,
    /// <summary>Бирюзовый: не учтён в PDM — новый файл или файл по ссылке.</summary>
    NotInPdm,
}

public static class ViewModelExtensions
{
    /// <summary>Пересчитать доступность всех команд объекта.</summary>
    public static void NotifyCommands(this object vm)
    {
        foreach (var p in vm.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(IRelayCommand).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0 && p.GetValue(vm) is IRelayCommand c)
                c.NotifyCanExecuteChanged();
        }
    }

    public static void ReplaceWith<T>(this System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }
}
