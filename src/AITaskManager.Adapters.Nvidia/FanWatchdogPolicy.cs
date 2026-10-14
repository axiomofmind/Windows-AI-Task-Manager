namespace AITaskManager.Adapters.Nvidia;

public sealed record FanPolicyDecision(
    bool IsValid,
    string State,
    int DesiredDutyPercent,
    string Detail);

public sealed class FanWatchdogPolicy
{
    private const int EmergencyHoldSeconds = 10;
    private readonly int _minimum;
    private readonly int _maximum;
    private readonly int _ceiling;
    private readonly Queue<(DateTimeOffset ObservedAt, int Temperature)> _recent = new();
    private int _requested;
    private DateTimeOffset? _emergencyUntil;
    private (DateTimeOffset ObservedAt, int Temperature)? _previous;

    public FanWatchdogPolicy(int requested, int minimum, int maximum, int ceiling)
    {
        if (minimum is < 0 or > 100 || maximum is < 0 or > 100 || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Fan duty bounds are invalid.");
        if (requested < minimum || requested > maximum)
            throw new ArgumentOutOfRangeException(nameof(requested), "Requested fan duty is outside the supported range.");
        if (ceiling is < 60 or > 115)
            throw new ArgumentOutOfRangeException(nameof(ceiling), "Emergency temperature ceiling is invalid.");

        _requested = requested;
        _minimum = minimum;
        _maximum = maximum;
        _ceiling = ceiling;
    }

    public void UpdateRequestedDuty(int requested)
    {
        if (requested < _minimum || requested > _maximum)
            throw new ArgumentOutOfRangeException(nameof(requested), "Requested fan duty is outside the supported range.");
        _requested = requested;
    }

    public FanPolicyDecision Evaluate(int temperature, DateTimeOffset observedAt, int? appliedDutyPercent)
    {
        if (temperature is < 0 or > 120)
            return Invalid(appliedDutyPercent, "Temperature is outside the defensible sensor range.");

        if (_previous is { } previous)
        {
            var elapsed = observedAt - previous.ObservedAt;
            if (elapsed <= TimeSpan.Zero)
                return Invalid(appliedDutyPercent, "Temperature timestamp did not advance.");
            if (elapsed <= TimeSpan.FromSeconds(2) && Math.Abs(temperature - previous.Temperature) > 30)
                return Invalid(appliedDutyPercent, "Temperature changed implausibly quickly.");
        }

        _previous = (observedAt, temperature);
        _recent.Enqueue((observedAt, temperature));
        while (_recent.TryPeek(out var sample) && observedAt - sample.ObservedAt > TimeSpan.FromSeconds(5))
            _recent.Dequeue();

        var rapidRise = _recent.Count > 1 && temperature - _recent.Min(sample => sample.Temperature) >= 8;
        if (temperature >= _ceiling || rapidRise)
            _emergencyUntil = observedAt.AddSeconds(EmergencyHoldSeconds);

        var emergency = _emergencyUntil is { } holdUntil &&
                        (observedAt < holdUntil || temperature >= _ceiling - 5);
        var desired = emergency
            ? _maximum
            : FanSafetyCurve.CalculateDuty(_requested, temperature, _ceiling, _minimum, _maximum);
        var rateLimited = appliedDutyPercent is null
            ? desired
            : desired > appliedDutyPercent.Value
                ? Math.Min(desired, appliedDutyPercent.Value + 10)
                : Math.Max(desired, appliedDutyPercent.Value - 2);

        return new(
            true,
            emergency ? "emergency" : "manual",
            Math.Clamp(rateLimited, _minimum, _maximum),
            emergency
                ? rapidRise && temperature < _ceiling ? "Emergency cooling triggered by rapid temperature rise." : "Emergency cooling active."
                : "Manual minimum and thermal ramp active.");
    }

    private static FanPolicyDecision Invalid(int? applied, string detail) =>
        new(false, "faulted", applied ?? 0, detail);
}
