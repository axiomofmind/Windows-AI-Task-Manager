using AITaskManager.Hosting;
using AITaskManager.Infrastructure.Windows;
using System.Net.Http.Json;
using AITaskManager.Contracts;
using AITaskManager.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AITaskManager.IntegrationTests;

public sealed class ApplicationHostTests
{
    [Fact]
    public async Task ReusableHostServesBundledUiOnDynamicLoopbackPortAndStopsCleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "AITaskManager.Tests", Guid.NewGuid().ToString("N"));
        var webRoot = Path.Combine(root, "wwwroot");
        WebApplication? app = null;
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(
            Path.Combine(webRoot, "index.html"),
            "<!doctype html><title>AI Task Manager Test</title>");

        try
        {
            app = await ApplicationHost.BuildAsync(
                [],
                new ApplicationHostSettings(
                    ListenUrl: "http://127.0.0.1:0",
                    WebRootPath: webRoot,
                    ContentRootPath: root,
                    DefaultPaths: AppPaths.ForRoot(Path.Combine(root, "profile"))));
            await app.StartAsync();
            var address = ApplicationHost.GetListeningAddress(app);

            using var client = new HttpClient { BaseAddress = address };
            var rootResponse = await client.GetAsync("/");
            var html = await rootResponse.Content.ReadAsStringAsync();
            var ready = await client.GetStringAsync("/health/ready");
            using var hostileHostRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
            hostileHostRequest.Headers.Host = "example.test";
            var hostileHostResponse = await client.SendAsync(hostileHostRequest);
            var actionSession = await client.GetFromJsonAsync<ActionSessionResponse>("/api/v1/actions/session");
            using var actionWithoutToken = await client.PostAsJsonAsync(
                "/api/v1/actions/gpu-power",
                new SetPowerLimitRequest("GPU-test", 250, "SET GPU-TEST 250W", Guid.NewGuid().ToString()));
            using var guardedActionRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/actions/gpu-power")
            {
                Content = JsonContent.Create(new SetPowerLimitRequest(
                    "GPU-test",
                    250,
                    "SET GPU-TEST 250W",
                    Guid.NewGuid().ToString())),
            };
            guardedActionRequest.Headers.Add("X-AITM-Action-Token", actionSession!.Token);
            using var guardedActionResponse = await client.SendAsync(guardedActionRequest);
            using var fanWithoutToken = await client.PostAsJsonAsync(
                "/api/v1/actions/gpu-fans/manual",
                new SetFanManualRequest("GPU-test", 50, true, true, Guid.NewGuid().ToString()));
            using var guardedFanRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/actions/gpu-fans/manual")
            {
                Content = JsonContent.Create(new SetFanManualRequest(
                    "GPU-test", 50, true, true, Guid.NewGuid().ToString())),
            };
            guardedFanRequest.Headers.Add("X-AITM-Action-Token", actionSession.Token);
            using var guardedFanResponse = await client.SendAsync(guardedFanRequest);
            var auditCount = await app.Services.GetRequiredService<SqliteDatabase>().GetActionAuditCountAsync();

            Assert.Equal("127.0.0.1", address.Host);
            Assert.Contains("AI Task Manager Test", html, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"ready\"", ready, StringComparison.Ordinal);
            Assert.True(rootResponse.Headers.Contains("Content-Security-Policy"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, hostileHostResponse.StatusCode);
            Assert.True(actionSession.ActionsEnabled);
            Assert.True(actionSession.PowerControlEnabled);
            Assert.True(actionSession.FanControlEnabled);
            Assert.False(actionSession.PrivilegedHelperAvailable);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, actionWithoutToken.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, guardedActionResponse.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, fanWithoutToken.StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, guardedFanResponse.StatusCode);
            Assert.Equal(2, auditCount);

            await app.StopAsync();
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
