using System.Text.Json;
using AITaskManager.Core;

namespace AITaskManager.UnitTests;

public sealed class DomainPrimitiveTests
{
    [Fact]
    public void ConflictResolverPrefersUsableThenAuthoritativeThenRecent()
    {
        var now = DateTimeOffset.UtcNow;
        var unavailable = new ObservedValue<int?>(null, ObservationState.Unavailable, now, "vendor");
        var heuristic = new ObservedValue<int?>(42, ObservationState.Available, now.AddSeconds(-1), "heuristic");

        var usable = ObservationConflictResolver.Prefer(
            unavailable,
            ObservationSourcePriority.VendorApi,
            heuristic,
            ObservationSourcePriority.Heuristic);

        Assert.Same(heuristic, usable);

        var operatingSystem = heuristic with { Value = 43, Source = "os" };
        var authoritative = ObservationConflictResolver.Prefer(
            heuristic,
            ObservationSourcePriority.Heuristic,
            operatingSystem,
            ObservationSourcePriority.OperatingSystem);
        Assert.Same(operatingSystem, authoritative);
    }

    [Fact]
    public void ObservedValueAllowsMissingOptionalFieldsAndFutureNumericEnumValues()
    {
        const string json = "{\"Value\":7,\"State\":999,\"ObservedAt\":\"2026-01-01T00:00:00Z\",\"Source\":\"future-adapter\"}";

        var value = JsonSerializer.Deserialize<ObservedValue<int>>(json);

        Assert.NotNull(value);
        Assert.Equal(7, value.Value);
        Assert.Equal((ObservationState)999, value.State);
        Assert.Null(value.Detail);
    }
}
