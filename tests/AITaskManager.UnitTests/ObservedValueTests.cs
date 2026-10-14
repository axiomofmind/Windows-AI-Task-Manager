using AITaskManager.Core;

namespace AITaskManager.UnitTests;

public sealed class ObservedValueTests
{
    [Theory]
    [InlineData(ObservationState.Available, true)]
    [InlineData(ObservationState.Stale, true)]
    [InlineData(ObservationState.Unsupported, false)]
    [InlineData(ObservationState.Unavailable, false)]
    [InlineData(ObservationState.PermissionDenied, false)]
    [InlineData(ObservationState.Error, false)]
    public void HasUsableValueReflectsQualityState(ObservationState state, bool expected)
    {
        var observation = new ObservedValue<int>(42, state, DateTimeOffset.UtcNow, "test");

        Assert.Equal(expected, observation.HasUsableValue);
    }

    [Fact]
    public void MissingValueIsNeverUsable()
    {
        var observation = new ObservedValue<int?>(null, ObservationState.Available, DateTimeOffset.UtcNow, "test");

        Assert.False(observation.HasUsableValue);
    }
}
