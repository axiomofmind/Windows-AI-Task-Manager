using AITaskManager.Contracts;
using AITaskManager.Hosting;

namespace AITaskManager.IntegrationTests;

public sealed class FanSessionAuthorizationTests
{
    [Fact]
    public void NewSessionRequiresManualControlAcknowledgement()
    {
        var request = Request(confirmControllers: true, acknowledgeManual: false);

        var exception = Assert.Throws<HardwareActionValidationException>(
            () => FanControlService.ValidateSessionAuthorization(request, hasActiveSession: false));

        Assert.Contains("safety acknowledgement", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewSessionRequiresCompetingControllerAcknowledgement()
    {
        var request = Request(confirmControllers: false, acknowledgeManual: true);

        var exception = Assert.Throws<HardwareActionValidationException>(
            () => FanControlService.ValidateSessionAuthorization(request, hasActiveSession: false));

        Assert.Contains("other fan-control", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveSessionUpdateDoesNotRequireRepeatedAcknowledgements()
    {
        var request = Request(confirmControllers: false, acknowledgeManual: false);

        FanControlService.ValidateSessionAuthorization(request, hasActiveSession: true);
    }

    private static SetFanManualRequest Request(bool confirmControllers, bool acknowledgeManual) =>
        new("GPU-test", 50, confirmControllers, acknowledgeManual, Guid.NewGuid().ToString());
}
