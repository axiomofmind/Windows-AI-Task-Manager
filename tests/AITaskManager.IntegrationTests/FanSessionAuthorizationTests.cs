using AITaskManager.Contracts;
using AITaskManager.Hosting;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

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

    [Fact]
    public async Task SuccessfulRestoreIsNotMaskedWhenHelperClosesPipeBeforeWriterDisposal()
    {
        var replyJson = """
            {"success":true,"state":"automatic","appliedDutyPercent":null,"temperatureCelsius":null,"leaseRemainingSeconds":0,"detail":"NVIDIA automatic control restored."}
            """;
        await using var replyStream = new MemoryStream(Encoding.UTF8.GetBytes(replyJson + Environment.NewLine));
        var reader = new StreamReader(replyStream, Encoding.UTF8, false, leaveOpen: true);
        await using var commandStream = new ThrowOnFinalFlushStream();
        var writer = new StreamWriter(commandStream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };
        var pipe = new NamedPipeServerStream(
            $"aitm-test-{Guid.NewGuid():N}",
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var process = Process.GetCurrentProcess();
        var session = new FanControlService.FanSession(
            "GPU-test",
            Convert.ToHexString(new byte[32]),
            90,
            process,
            pipe,
            reader,
            writer);

        var reply = await FanControlService.RestoreAndDisposeAsync(session, CancellationToken.None);

        Assert.True(reply.Success);
        Assert.Equal("automatic", reply.State);
        await session.DisposeAsync();
    }

    private static SetFanManualRequest Request(bool confirmControllers, bool acknowledgeManual) =>
        new("GPU-test", 50, confirmControllers, acknowledgeManual, Guid.NewGuid().ToString());

    private sealed class ThrowOnFinalFlushStream : MemoryStream
    {
        private int _flushCount;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _flushCount) >= 2)
            {
                throw new IOException("Pipe is broken.");
            }

            return Task.CompletedTask;
        }
    }
}
