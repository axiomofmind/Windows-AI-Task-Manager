using System.IO;
using System.Windows;
using AITaskManager.Adapters.Nvidia;
using AITaskManager.Application;
using AITaskManager.Hosting;
using AITaskManager.Infrastructure.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskManager.Desktop;

public partial class App : System.Windows.Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private SingleInstanceCoordinator? _singleInstance;
    private Task? _activationListener;
    private WebApplication? _host;
    private MainWindow? _window;
    private bool _isExiting;

    public bool IsExiting => _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (NvidiaPrivilegedCommand.IsHelperInvocation(e.Args))
        {
            Shutdown(NvidiaPrivilegedCommand.Execute(e.Args));
            return;
        }

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryAcquirePrimary())
        {
            await _singleInstance.SignalPrimaryAsync().ConfigureAwait(true);
            Shutdown();
            return;
        }

        _activationListener = _singleInstance.ListenForActivationAsync(
            () => Dispatcher.InvokeAsync(ShowMainWindow).Task,
            _shutdown.Token);

        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _host = await ApplicationHost.BuildAsync(
                e.Args,
                new ApplicationHostSettings(
                    ListenUrl: "http://127.0.0.1:0",
                    WebRootPath: webRoot,
                    ContentRootPath: AppContext.BaseDirectory,
                    HardwareHelperPath: Environment.ProcessPath),
                _shutdown.Token).ConfigureAwait(true);
            await _host.StartAsync(_shutdown.Token).ConfigureAwait(true);

            var address = ApplicationHost.GetListeningAddress(_host);
            var paths = _host.Services.GetRequiredService<AppPaths>();
            var options = _host.Services.GetRequiredService<AITaskManagerOptions>();
            _window = new MainWindow(
                address,
                Path.Combine(paths.RootDirectory, "webview2"),
                Path.Combine(paths.DataDirectory, "window-state.json"),
                options.Desktop.CloseToTray,
                RequestExitAsync);
            MainWindow = _window;

            if (options.Desktop.StartMinimized)
            {
                _window.Hide();
            }
            else
            {
                _window.Show();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"AI Task Manager could not start.\n\n{exception.Message}\n\nSee the application logs for details.",
                "AI Task Manager startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await StopHostAsync().ConfigureAwait(true);
            Shutdown(1);
        }
    }

    public async Task RequestExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _window?.Hide();
        _window?.DisposeTrayIcon();
        await StopHostAsync().ConfigureAwait(true);
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        StopHostAsync().GetAwaiter().GetResult();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _shutdown.Cancel();

        if (_host is not null)
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _host = null;
        }

        if (_activationListener is not null)
        {
            try
            {
                _activationListener.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        _singleInstance?.Dispose();
        _shutdown.Dispose();
        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowFromTray();
    }

    private async Task StopHostAsync()
    {
        _shutdown.Cancel();

        if (_host is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _host.StopAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
    }
}
