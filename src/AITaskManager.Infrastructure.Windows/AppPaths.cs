namespace AITaskManager.Infrastructure.Windows;

public sealed record AppPaths(
    string RootDirectory,
    string ConfigurationFile,
    string DataDirectory,
    string LogDirectory)
{
    public static AppPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Windows did not provide a LocalApplicationData path.");
        }

        return ForRoot(Path.Combine(localAppData, "AITaskManager"));
    }

    public static AppPaths ForRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var root = Path.GetFullPath(rootDirectory);
        return new AppPaths(
            root,
            Path.Combine(root, "config.json"),
            Path.Combine(root, "data"),
            Path.Combine(root, "logs"));
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
