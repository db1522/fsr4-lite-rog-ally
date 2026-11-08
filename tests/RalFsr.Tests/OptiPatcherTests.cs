using System.Net;
using System.Security.Cryptography;
using System.Text;
using RalFsrHelper.Core;
using Xunit;

namespace RalFsrHelper.Tests;

/// <summary>Serves canned responses by URL and counts the requests.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
    public readonly List<string> Asked = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var url = request.RequestUri!.ToString();
        Asked.Add(url);
        return Task.FromResult(Routes.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public void Bytes(string url, byte[] body) =>
        Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public void Json(string json) =>
        Routes[OptiPatcherSource.ApiUrl] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
}

/// <summary>Each test gets its own folder for the cache and a fake game; the network is always faked.</summary>
public abstract class TempFolderTest : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "ralfsr-tests", Guid.NewGuid().ToString("N"));
    internal readonly FakeGitHub Net = new();

    protected TempFolderTest()
    {
        Directory.CreateDirectory(Root);
        OptiPatcherSource.CacheDir = Path.Combine(Root, "cache");
        OptiPatcherSource.Handler = Net;
    }

    public void Dispose()
    {
        OptiPatcherSource.Handler = null;
        OptiPatcherSource.Pinned = OptiPatcherSource.PinnedRelease;
        try { Directory.Delete(Root, recursive: true); } catch { }
    }

    protected static byte[] Dll(string marker) => Encoding.ASCII.GetBytes("MZ" + marker + new string('x', 64));
    protected static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    protected static string Release(string tag, string published, byte[] asi, bool draft = false, bool pre = false,
        string? digest = "auto", string? name = null)
    {
        name ??= $"OptiPatcher_{tag}.asi";
        var url = $"{OptiPatcherSource.DownloadPrefix}{tag}/{name}";
        var d = digest == "auto" ? $"\"digest\": \"sha256:{Sha(asi)}\"," : digest is null ? "" : $"\"digest\": \"{digest}\",";
        return $$"""
            { "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(pre ? "true" : "false")}},
              "published_at": "{{published}}",
              "assets": [ { "name": "{{name}}", "browser_download_url": "{{url}}", "size": {{asi.Length}}, {{d}} "state": "uploaded" } ] }
            """;
    }

    protected static string Url(string tag) => $"{OptiPatcherSource.DownloadPrefix}{tag}/OptiPatcher_{tag}.asi";
}

[Collection("OptiPatcher")]
public class OptiPatcherDownloadTests : TempFolderTest
{
    [Fact]
    public void The_newest_published_release_is_picked()
    {
        var a = Dll("a");
        var json = "[" + string.Join(",",
            Release("rolling", "2025-10-01T00:00:00Z", a),
            Release("v0.43", "2026-11-01T00:00:00Z", a),
            Release("v0.50", "2026-11-05T00:00:00Z", a, pre: true),
            Release("v0.51", "2026-11-06T00:00:00Z", a, draft: true),
            Release("v0.42", "2026-10-20T00:00:00Z", a)) + "]";

        var newest = OptiPatcherSource.Newest(json)!;
        Assert.Equal("v0.43", newest.Tag);
        Assert.Equal(Url("v0.43"), newest.Url);
        Assert.Equal(Sha(a), newest.Sha256);
    }

    [Fact]
    public void Assets_from_elsewhere_or_not_asi_are_ignored()
    {
        var a = Dll("a");
        var json = "[" + Release("v0.43", "2026-11-01T00:00:00Z", a, name: "OptiPatcher.zip") + "]";
        Assert.Null(OptiPatcherSource.Newest(json));
        Assert.Null(OptiPatcherSource.Newest("{\"message\": \"API rate limit exceeded\"}"));
        Assert.Null(OptiPatcherSource.Newest("not json"));
    }

    [Fact]
    public async Task The_newest_release_is_downloaded_checked_by_githubs_sha256()
    {
        var asi = Dll("v43");
        Net.Json("[" + Release("v0.43", "2026-11-01T00:00:00Z", asi) + "]");
        Net.Bytes(Url("v0.43"), asi);

        var op = (await OptiPatcherSource.ForGameAsync())!;
        Assert.Equal("v0.43", op.Tag);
        Assert.Equal(asi, File.ReadAllBytes(op.AsiPath));
        Assert.Equal("v0.43", OptiPatcherSource.Cached()!.Tag);
    }

    [Fact]
    public async Task A_file_that_doesnt_match_the_digest_falls_back_to_the_pinned_release()
    {
        var asi = Dll("v43");
        Net.Json("[" + Release("v0.43", "2026-11-01T00:00:00Z", asi) + "]");
        Net.Bytes(Url("v0.43"), Dll("tampered")); // same length, different bytes
        var pinned = PinnedBytes();
        Net.Bytes(OptiPatcherSource.Pinned.Url, pinned);

        var op = (await OptiPatcherSource.ForGameAsync())!;
        Assert.Equal("v0.42", op.Tag);
    }

    [Fact]
    public async Task Without_githubs_api_the_pinned_release_is_used()
    {
        Net.Routes[OptiPatcherSource.ApiUrl] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Net.Bytes(OptiPatcherSource.Pinned.Url, PinnedBytes());

        var op = (await OptiPatcherSource.ForGameAsync())!;
        Assert.Equal("v0.42", op.Tag);
    }

    [Fact]
    public async Task A_pinned_file_with_the_wrong_hash_is_refused()
    {
        Net.Routes[OptiPatcherSource.ApiUrl] = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var wrong = new byte[OptiPatcherSource.PinnedRelease.Size]; // the real size, but not the real file
        wrong[0] = (byte)'M'; wrong[1] = (byte)'Z';
        Net.Bytes(OptiPatcherSource.Pinned.Url, wrong);

        Assert.Null(await OptiPatcherSource.ForGameAsync());
        Assert.Null(OptiPatcherSource.Cached());
    }

    [Fact]
    public async Task A_download_that_isnt_a_windows_dll_is_refused()
    {
        var html = Encoding.ASCII.GetBytes("<html>not a plugin</html>");
        Net.Json("[" + Release("v0.43", "2026-11-01T00:00:00Z", html) + "]");
        Net.Bytes(Url("v0.43"), html);
        Net.Routes[OptiPatcherSource.Pinned.Url] = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        Assert.Null(await OptiPatcherSource.ForGameAsync());
        Assert.Null(OptiPatcherSource.Cached());
    }

    [Fact]
    public async Task Nothing_is_looked_for_before_a_game_needs_it()
    {
        Assert.Null(await OptiPatcherSource.UpdateAsync());
        Assert.Empty(Net.Asked);
    }

    [Fact]
    public async Task A_newer_release_is_looked_for_once_a_day()
    {
        var v43 = Dll("v43");
        Net.Json("[" + Release("v0.43", "2026-11-01T00:00:00Z", v43) + "]");
        Net.Bytes(Url("v0.43"), v43);
        await OptiPatcherSource.ForGameAsync();

        // A newer release appears, but the last look was just now: GitHub isn't asked.
        var v44 = Dll("v44");
        Net.Json("[" + Release("v0.44", "2026-11-07T00:00:00Z", v44) + "," + Release("v0.43", "2026-11-01T00:00:00Z", v43) + "]");
        Net.Bytes(Url("v0.44"), v44);
        Net.Asked.Clear();
        Assert.Equal("v0.43", (await OptiPatcherSource.UpdateAsync(DateTime.UtcNow.AddHours(2)))!.Tag);
        Assert.Empty(Net.Asked);

        // A day later it is, and the new release replaces the old one.
        var op = (await OptiPatcherSource.UpdateAsync(DateTime.UtcNow.AddHours(25)))!;
        Assert.Equal("v0.44", op.Tag);
        Assert.Equal(v44, File.ReadAllBytes(op.AsiPath));
    }

    /// <summary>A stand-in for the pinned file (the real one can't be in the tests), made the fallback.</summary>
    private static byte[] PinnedBytes()
    {
        var b = Dll("pinned");
        OptiPatcherSource.Pinned = OptiPatcherSource.PinnedRelease with { Size = b.Length, Sha256 = Sha(b) };
        return b;
    }
}
