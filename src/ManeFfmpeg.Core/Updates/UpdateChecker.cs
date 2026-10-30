using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Updates;

/// <summary>A downloadable file of a release; <see cref="Sha256"/> is GitHub's digest of it, in hex, when it has one.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);

/// <summary>A newer release than this build, with what changed since this build.</summary>
/// <param name="Asset">The download for this platform, or null when the release has none.</param>
/// <param name="Changelog">CHANGELOG.md's sections after this build's version up to the release (Markdown).</param>
public sealed record UpdateOffer(Version Version, string Tag, Uri Page, ReleaseAsset? Asset, string? Changelog);

/// <summary>Looks for a newer release on GitHub.</summary>
public static class UpdateChecker
{
    public const string Repository = "ManeFunction/mane-ffmpeg";

    /// <summary>Talks to api.github.com and the download hosts; GitHub rejects requests without a User-Agent.</summary>
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ManeFfmpeg", AppVersion.Current.ToString(3)));
        return http;
    }

    /// <summary>
    /// The latest release (GitHub's "latest" skips drafts and pre-releases) when it's newer than
    /// <paramref name="current"/>, else null. Network and API errors throw.
    /// </summary>
    public static async Task<UpdateOffer?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync(ReleaseJsonContext.Default.GitHubRelease, ct)
            ?? throw new InvalidDataException("GitHub returned an empty release.");
        if (AppVersion.Parse(release.TagName) is not { } version || version <= current)
            return null;

        var assetName = UpdateInstaller.AssetName(version);
        var asset = release.Assets.FirstOrDefault(a => a.Name == assetName) is { } found
            ? new ReleaseAsset(found.Name, new Uri(found.BrowserDownloadUrl), found.Size, Sha256Of(found.Digest))
            : null;
        var changelog = await ChangelogAsync(http, release.TagName, current, version, ct) ?? NotesWithoutDownloads(release.Body);
        return new UpdateOffer(version, release.TagName, new Uri(release.HtmlUrl), asset, changelog);
    }

    /// <summary>CHANGELOG.md as of the release's tag, cut to the versions between; null if it can't be read.</summary>
    private static async Task<string?> ChangelogAsync(HttpClient http, string tag, Version current, Version latest, CancellationToken ct)
    {
        try
        {
            var markdown = await http.GetStringAsync($"https://raw.githubusercontent.com/{Repository}/{Uri.EscapeDataString(tag)}/CHANGELOG.md", ct);
            return Changelog.Between(markdown, current, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppLog.Write($"Update: couldn't read CHANGELOG.md at {tag}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The release notes without the install instructions the release workflow appends.</summary>
    private static string? NotesWithoutDownloads(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var text = body.ReplaceLineEndings("\n");
        var downloads = text.IndexOf("\n## Downloads", StringComparison.Ordinal);
        return (downloads >= 0 ? text[..downloads] : text).Trim();
    }

    /// <summary>"sha256:abc…" → "abc…".</summary>
    private static string? Sha256Of(string? digest) =>
        digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : null;
}

internal sealed record GitHubRelease(string TagName, string HtmlUrl, string? Body, List<GitHubAsset> Assets);

internal sealed record GitHubAsset(string Name, string BrowserDownloadUrl, long Size, string? Digest);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
