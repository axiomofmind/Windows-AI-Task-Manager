using System.IO;
using System.IO.Pipes;

namespace AITaskManager.Desktop;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\AITaskManager.Desktop.Primary.v1";
    private const string PipeName = "AITaskManager.Desktop.Activation.v1";
    private readonly Mutex _mutex = new(initiallyOwned: false, MutexName);
    private bool _ownsMutex;

    public bool TryAcquirePrimary()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public async Task SignalPrimaryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task ListenForActivationAsync(
        Func<Task> activateAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activateAsync);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = new NamedPipeServerStream(
                PipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var signal = new byte[1];
            if (await server.ReadAsync(signal, cancellationToken).ConfigureAwait(false) > 0)
            {
                await activateAsync().ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
