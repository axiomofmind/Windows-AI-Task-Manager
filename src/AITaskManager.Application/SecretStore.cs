namespace AITaskManager.Application;

public interface ISecretStore
{
    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
