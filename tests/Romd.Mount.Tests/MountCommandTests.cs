using Romd.Mount.Cli;
using System.Net;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountCommandTests
{
    [Fact]
    public async Task Help_DoesNotContactServerOrRequireKeychain()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        (await Program.RunAsync(["--help"], output, error, CancellationToken.None)).ShouldBe(0);
        output.ToString().ShouldContain("login|status|logout");
        error.ToString().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("token")]
    [InlineData("--password")]
    [InlineData("--server")]
    public void UnknownOrDuplicateArguments_Reject(string option)
    {
        Should.Throw<ArgumentException>(() => Program.Parse(["login", "--server", "https://server.example", option, "secret"]));
    }

    [Fact]
    public async Task Binding_RoundTripsWithoutCredentialsAndRejectsWrongProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-profile-" + Guid.NewGuid().ToString("N"));
        var binding = new ProfileBinding(root, new Uri("https://server.example"), "profile");
        try
        {
            (await binding.ReadAsync(default)).ShouldBeNull();
            Guid instance = Guid.NewGuid();
            await binding.SaveAsync(instance, default);
            (await binding.ReadAsync(default)).ShouldBe(instance);
            string path = Path.Combine(root, "profiles", binding.Key + ".json");
            string json = await File.ReadAllTextAsync(path);
            json.ShouldNotContain("token");
            await File.WriteAllTextAsync(path, json.Replace("\"profile\":\"profile\"", "\"profile\":\"other\""));
            await Should.ThrowAsync<IOException>(() => binding.ReadAsync(default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Binding_ReplacementLeavesNoTemporaryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-profile-" + Guid.NewGuid().ToString("N"));
        var binding = new ProfileBinding(root, new Uri("https://server.example"), "profile");
        try
        {
            await binding.SaveAsync(Guid.NewGuid(), default);
            Guid next = Guid.NewGuid();
            await binding.SaveAsync(next, default);
            (await binding.ReadAsync(default)).ShouldBe(next);
            Directory.GetFiles(Path.Combine(root, "profiles")).Length.ShouldBe(1);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DevicePolling_SlowDown_IncreasesEveryLaterInterval()
    {
        Guid instance = Guid.NewGuid();
        using var client = new MountIdentityClient(new Uri("https://server.example"), new PollHandler(instance));
        var identity = new BoundMountIdentity(client, instance);
        var login = new DeviceLogin("device", "CODE", new Uri("https://server.example/verify"), 600, 2);
        var delays = new List<double>();
        var result = await Program.WaitForLoginAsync(identity, login, default, (delay, ct) =>
        { ct.ThrowIfCancellationRequested(); delays.Add(delay.TotalSeconds); return Task.CompletedTask; });
        result.RefreshToken.ShouldBe("refresh");
        delays.ShouldBe(new double[] { 2, 7, 7 });
    }

    private sealed class PollHandler(Guid instance) : HttpMessageHandler
    {
        private int _polls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"instanceId\":\"" + instance.ToString("D") + "\"}") });
            string json = ++_polls switch
            {
                1 => "{\"error\":\"slow_down\"}",
                2 => "{\"error\":\"authorization_pending\"}",
                _ => "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"token_type\":\"Bearer\",\"expires_in\":900}"
            };
            return Task.FromResult(new HttpResponseMessage(_polls < 3 ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            { Content = new StringContent(json) });
        }
    }
}
