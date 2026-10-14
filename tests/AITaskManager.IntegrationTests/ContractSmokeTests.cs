using AITaskManager.Contracts;

namespace AITaskManager.IntegrationTests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void EmptySummaryKeepsAllMutationsDisabled()
    {
        var summary = SummaryResponse.Empty(DateTimeOffset.UtcNow);

        Assert.Equal(0, summary.GpuCount);
        Assert.False(summary.ActionsEnabled);
        Assert.False(summary.FanControlEnabled);
    }
}
