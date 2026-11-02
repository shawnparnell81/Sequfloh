using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Sequfloh.Tests;

/// <summary>The running build is readable without signing in, is never cached, and follows the deployed commit when the host gives one.</summary>
[Collection("api")]
public sealed class BuildVersionTests(TestApp app)
{
    [Fact]
    public async Task Version_is_open_uncached_and_steady()
    {
        var anon = await app.AnonymousAsync();
        var r1 = await anon.Http.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.True(r1.Headers.CacheControl?.NoStore);
        var v1 = JsonDocument.Parse(await r1.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(v1));
        var v2 = JsonDocument.Parse(await (await anon.Http.GetAsync("/api/version")).Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetString();
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void Deployed_commit_is_used_when_given()
    {
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RENDER_GIT_COMMIT"] = "abc123" }).Build();
        Assert.Equal("abc123", BuildVersion.Current(c));
        Assert.False(string.IsNullOrWhiteSpace(BuildVersion.Current(new ConfigurationBuilder().Build())));
    }
}
