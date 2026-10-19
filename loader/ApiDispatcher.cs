using Autodesk.Revit.UI;
using System.Collections.Concurrent;

namespace RevitMCP.Loader;

internal sealed class ApiDispatcher : IExternalEventHandler, IDisposable
{
    readonly ConcurrentQueue<Work> queue = new();
    readonly ExternalEvent externalEvent;
    bool disposed;

    public ApiDispatcher() => externalEvent = ExternalEvent.Create(this);

    public Task InvokeAsync(Action<UIApplication> action, CancellationToken cancellation = default)
    {
        if (disposed) return Task.FromException(new ObjectDisposedException(nameof(ApiDispatcher)));
        if (cancellation.IsCancellationRequested) return Task.FromCanceled(cancellation);
        var work = new Work(action, cancellation);
        queue.Enqueue(work);
        if (externalEvent.Raise() == ExternalEventRequest.Denied)
        {
            work.Completion.TrySetException(new InvalidOperationException("Revit занят. Завершите текущую команду и повторите обновление."));
            work.Dispose();
        }
        return work.Completion.Task;
    }

    public void Execute(UIApplication application)
    {
        while (queue.TryDequeue(out var work))
        {
            using var registration = work;
            if (work.Completion.Task.IsCompleted) continue;
            if (work.Cancellation.IsCancellationRequested) { work.Completion.TrySetCanceled(work.Cancellation); continue; }
            try { work.Action(application); work.Completion.TrySetResult(); }
            catch (Exception ex) { work.Completion.TrySetException(ex); }
        }
    }
    public string GetName() => "Revit MCP updates";
    public void Dispose()
    {
        disposed = true;
        while (queue.TryDequeue(out var work)) { work.Completion.TrySetCanceled(); work.Dispose(); }
        externalEvent.Dispose();
    }
    sealed class Work : IDisposable
    {
        public Action<UIApplication> Action { get; }
        public CancellationToken Cancellation { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration registration;
        public Work(Action<UIApplication> action, CancellationToken cancellation)
        {
            Action = action; Cancellation = cancellation;
            registration = cancellation.Register(() => Completion.TrySetCanceled(cancellation));
        }
        public void Dispose() => registration.Dispose();
    }
}
