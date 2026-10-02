/*
 * Backs the dock pane's "Check version" button: compares this build's version with the latest
 * published GitHub release (https://github.com/ianhorn/kylidar-addin/releases), so someone running
 * a downloaded .esriAddinX can tell whether a newer one exists without visiting the repo.
 *
 * Uses GitHub's public, unauthenticated API (60 requests/hour per IP -- plenty for a button click).
 * /releases/latest skips drafts and pre-releases, which is what "newest download" should mean.
 *
 * The comparison is only meaningful if each release's build carries that release's version: the
 * <Version> in KylidarAddin.csproj must be bumped to match the tag BEFORE building a release (see
 * docs-src/installation.md, "Publishing a rebuilt download"). Revision is ignored when comparing
 * (the default assembly version 1.0.0.0 equals tag v1.0.0).
 */
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KylidarAddin.Services
{
    public sealed record UpdateCheckResult(Version Current, Version Latest, string ReleaseUrl)
    {
        public bool IsNewerAvailable => Latest > Current;
    }

    public static class UpdateCheckService
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/ianhorn/kylidar-addin/releases/latest";
        public const string ReleasesPage = FeedbackService.RepoUrl + "/releases";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KylidarAddin/1.0"); // GitHub's API rejects requests without one
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        /// <summary>This build's version, as Major.Minor.Build (the revision part is ignored).</summary>
        public static Version CurrentVersion()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return Normalize(v);
        }

        /// <summary>Fetch the latest release and compare it with <paramref name="current"/> (this build
        /// when null). Throws on network/parse problems -- the caller reports them.</summary>
        public static async Task<UpdateCheckResult> CheckAsync(Version current = null, CancellationToken ct = default)
        {
            current = Normalize(current ?? CurrentVersion());

            using var resp = await Http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException("No release has been published yet.");
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;

            if (!TryParseTag(tag, out var latest))
                throw new InvalidOperationException($"The latest release's tag \"{tag}\" isn't a version number.");

            return new UpdateCheckResult(current, latest, url ?? ReleasesPage);
        }

        /// <summary>"v1.2.0" or "1.2" -> a normalized Version. False for anything else.</summary>
        public static bool TryParseTag(string tag, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag)) return false;
            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var parsed)) return false;
            version = Normalize(parsed);
            return true;
        }

        private static Version Normalize(Version v) => new Version(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
    }
}
