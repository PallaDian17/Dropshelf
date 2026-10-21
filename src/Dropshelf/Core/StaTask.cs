namespace Dropshelf.Core;

/// <summary>
/// Runs work on a fresh STA thread. WPF imaging (RenderTargetBitmap) and several shell APIs
/// require STA, which thread-pool threads are not.
/// </summary>
public static class StaTask
{
    public static Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Dropshelf STA worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    public static Task Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });
}
