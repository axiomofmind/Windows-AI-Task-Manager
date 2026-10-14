using AITaskManager.Application;

namespace AITaskManager.UnitTests;

public sealed class ConfigurationAndRedactionTests
{
    [Fact]
    public void GuardedHardwareControlsAreAvailableByDefault()
    {
        var options = new AITaskManagerOptions();

        var errors = ConfigurationValidator.Validate(options);

        Assert.Empty(errors);
        Assert.True(options.Actions.Enabled);
        Assert.True(options.Actions.PowerControlEnabled);
        Assert.True(options.Actions.FanControlEnabled);
        Assert.True(options.Discovery.EnableLocalRuntimeDiscovery);
    }

    [Fact]
    public void NonLoopbackBindingIsRejected()
    {
        var options = new AITaskManagerOptions();
        options.Server.ListenUrl = "http://0.0.0.0:7312";

        var errors = ConfigurationValidator.Validate(options);

        Assert.Contains(errors, error => error.Path == "Server:ListenUrl");
    }

    [Fact]
    public void GuardedHardwareControlsCanBeEnabledTogether()
    {
        var options = new AITaskManagerOptions();
        options.Actions.Enabled = true;
        options.Actions.PowerControlEnabled = true;
        options.Actions.FanControlEnabled = true;

        var errors = ConfigurationValidator.Validate(options);

        Assert.DoesNotContain(errors, error => error.Path == "Actions:PowerControlEnabled");
        Assert.DoesNotContain(errors, error => error.Path == "Actions:FanControlEnabled");
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("https://runtime.test/v1?api_key=topsecret&x=1", "topsecret")]
    [InlineData("tool.exe --token very-secret --port 1", "very-secret")]
    [InlineData("OPENAI_API_KEY=sk-example", "sk-example")]
    [InlineData("https://alice:password123@runtime.test/v1", "password123")]
    public void RedactorRemovesCommonSecretForms(string source, string secret)
    {
        var result = new SensitiveDataRedactor().Redact(source);

        Assert.DoesNotContain(secret, result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactorReplacesKnownUserDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        var result = new SensitiveDataRedactor().Redact(Path.Combine(userProfile, "models", "model.gguf"));

        Assert.DoesNotContain(userProfile, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactorReplacesJsonEscapedUserDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return;
        }

        var escaped = Path.Combine(userProfile, "models").Replace("\\", "\\\\", StringComparison.Ordinal);
        var result = new SensitiveDataRedactor().Redact($"{{\"path\":\"{escaped}\"}}");

        Assert.DoesNotContain(userProfile.Replace("\\", "\\\\", StringComparison.Ordinal), result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", result, StringComparison.Ordinal);
    }
}
