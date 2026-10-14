using System.IO;
using System.Text.Json;
using System.Windows;

namespace AITaskManager.Desktop;

public sealed record SavedWindowState(
    double Left,
    double Top,
    double Width,
    double Height,
    bool Maximized);

public sealed class WindowStateStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);

    public void Restore(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<SavedWindowState>(File.ReadAllText(_path));
            if (state is null ||
                state.Width < window.MinWidth ||
                state.Height < window.MinHeight ||
                !IntersectsVirtualDesktop(state))
            {
                return;
            }

            window.Left = state.Left;
            window.Top = state.Top;
            window.Width = state.Width;
            window.Height = state.Height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (state.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
    }

    public void Save(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bounds = window.RestoreBounds;
        var state = new SavedWindowState(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            window.WindowState == WindowState.Maximized);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The window-state path has no parent directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(state));
    }

    private static bool IntersectsVirtualDesktop(SavedWindowState state)
    {
        var right = state.Left + state.Width;
        var bottom = state.Top + state.Height;
        var virtualRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        return right > SystemParameters.VirtualScreenLeft &&
            state.Left < virtualRight &&
            bottom > SystemParameters.VirtualScreenTop &&
            state.Top < virtualBottom;
    }
}
