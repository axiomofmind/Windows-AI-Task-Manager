using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AITaskManager.Desktop;

public partial class MainWindow : Window
{
    private readonly Uri _applicationAddress;
    private readonly string _webViewDataDirectory;
    private readonly bool _closeToTray;
    private readonly Func<Task> _requestExitAsync;
    private readonly WindowStateStore _windowStateStore;
    private readonly System.Drawing.Icon _applicationIcon;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;

    public MainWindow(
        Uri applicationAddress,
        string webViewDataDirectory,
        string windowStatePath,
        bool closeToTray,
        Func<Task> requestExitAsync)
    {
        ArgumentNullException.ThrowIfNull(applicationAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowStatePath);
        ArgumentNullException.ThrowIfNull(requestExitAsync);

        InitializeComponent();
        _applicationAddress = applicationAddress;
        _webViewDataDirectory = Path.GetFullPath(webViewDataDirectory);
        _closeToTray = closeToTray;
        _requestExitAsync = requestExitAsync;
        _windowStateStore = new WindowStateStore(windowStatePath);
        _windowStateStore.Restore(this);

        _applicationIcon = LoadApplicationIcon();
        _trayIcon = CreateTrayIcon();
        Loaded += HandleLoaded;
        Closing += HandleClosing;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void DisposeTrayIcon()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private async void HandleLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_webViewDataDirectory);
            StartupStatus.Text = "STARTING DISPLAY CORE";
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _webViewDataDirectory).ConfigureAwait(true);
            await DashboardView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            ConfigureWebView(DashboardView.CoreWebView2);
            StartupStatus.Text = "CONNECTING TO LOCAL CONTROL PLANE";
            DashboardView.Source = _applicationAddress;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StartupStatus.Text = "WEBVIEW2 RUNTIME REQUIRED";
            System.Windows.MessageBox.Show(
                "Microsoft WebView2 Runtime is required to display AI Task Manager. Install the Evergreen WebView2 Runtime, then launch the application again.",
                "WebView2 Runtime required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            StartupStatus.Text = "DISPLAY STARTUP FAILED";
            System.Windows.MessageBox.Show(
                $"The dashboard display could not start.\n\n{exception.Message}",
                "AI Task Manager display error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ConfigureWebView(CoreWebView2 webView)
    {
        webView.Settings.AreDefaultContextMenusEnabled = false;
        webView.Settings.AreDevToolsEnabled = false;
        webView.Settings.AreBrowserAcceleratorKeysEnabled = false;
        webView.Settings.IsPasswordAutosaveEnabled = false;
        webView.Settings.IsGeneralAutofillEnabled = false;
        webView.Settings.IsStatusBarEnabled = false;
        webView.NavigationStarting += HandleNavigationStarting;
        webView.NavigationCompleted += HandleNavigationCompleted;
        webView.NewWindowRequested += HandleNewWindowRequested;
        webView.DownloadStarting += (_, eventArgs) => eventArgs.Cancel = true;
        webView.PermissionRequested += (_, eventArgs) => eventArgs.State = CoreWebView2PermissionState.Deny;
    }

    private void HandleNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target) || !IsApplicationOrigin(target))
        {
            eventArgs.Cancel = true;
            OpenExternalHttpLink(target);
        }
    }

    private void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            StartupOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            StartupStatus.Text = $"DISPLAY ERROR {eventArgs.WebErrorStatus}";
        }
    }

    private void HandleNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target))
        {
            OpenExternalHttpLink(target);
        }
    }

    private void HandleClosing(object? sender, CancelEventArgs eventArgs)
    {
        _windowStateStore.Save(this);
        var application = (App)System.Windows.Application.Current;
        if (application.IsExiting)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_closeToTray)
        {
            Hide();
        }
        else
        {
            _ = _requestExitAsync();
        }
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open AI Task Manager", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Collection: running")!.Enabled = false;
        menu.Items.Add("Pause collection")!.Enabled = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.InvokeAsync(_requestExitAsync));

        var icon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _applicationIcon,
            Text = "AI Task Manager — collection running",
            Visible = true,
        };
        icon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(ShowFromTray);
            }
        };
        return icon;
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var embeddedIcon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (embeddedIcon is not null)
            {
                return embeddedIcon;
            }
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private bool IsApplicationOrigin(Uri target) =>
        string.Equals(target.Scheme, _applicationAddress.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(target.Host, _applicationAddress.Host, StringComparison.OrdinalIgnoreCase) &&
        target.Port == _applicationAddress.Port;

    private static void OpenExternalHttpLink(Uri? target)
    {
        if (target is null || target.Scheme is not ("http" or "https"))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(target.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }
}
