using System.Collections.Concurrent;

namespace SolidBrew.Adapters.SolidWorks;

/// <summary>
/// Выделенный поток STA с очередью задач. Все вызовы SOLIDWORKS идут через него по одному,
/// поэтому снаружи адаптер можно вызывать из любых потоков.
/// </summary>
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
    private readonly Thread _thread;

    public StaWorker(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public Task<T> Run<T>(Func<T> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsCurrentThread)
        {
            // Вложенный вызов из той же очереди — выполнить сразу, иначе взаимоблокировка.
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
            return tcs.Task;
        }

        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }
            try { tcs.TrySetResult(action()); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, CancellationToken.None);
        return tcs.Task;
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!IsCurrentThread) _thread.Join(TimeSpan.FromSeconds(5));
    }
}
