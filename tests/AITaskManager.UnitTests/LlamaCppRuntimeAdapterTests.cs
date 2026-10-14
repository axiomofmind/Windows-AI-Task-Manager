using AITaskManager.Adapters.LlamaCpp;

namespace AITaskManager.UnitTests;

public sealed class LlamaCppRuntimeAdapterTests
{
    [Theory]
    [InlineData("llama-server --api-key test-key", "test-key")]
    [InlineData("llama-server --api_key=other-key", "other-key")]
    [InlineData("llama-server --api-key \"key with spaces\"", "key with spaces")]
    public void ReadsApiKeyFromDiscoveredProcessCommandLine(string commandLine, string expected)
    {
        Assert.Equal(expected, LlamaCppRuntimeAdapter.ReadApiKey(commandLine));
    }

    [Fact]
    public void ReadsKvCacheTypesFromDiscoveredProcessCommandLine()
    {
        const string commandLine = "llama-server --cache-type-k q8_0 --cache-type-v=f16";

        Assert.Equal("q8_0", LlamaCppRuntimeAdapter.ReadKeyCacheType(commandLine));
        Assert.Equal("f16", LlamaCppRuntimeAdapter.ReadValueCacheType(commandLine));
    }

    [Fact]
    public void MissingArgumentsRemainUnavailable()
    {
        const string commandLine = "llama-server --port 8080";

        Assert.Null(LlamaCppRuntimeAdapter.ReadApiKey(commandLine));
        Assert.Null(LlamaCppRuntimeAdapter.ReadKeyCacheType(commandLine));
        Assert.Null(LlamaCppRuntimeAdapter.ReadValueCacheType(commandLine));
    }
}
