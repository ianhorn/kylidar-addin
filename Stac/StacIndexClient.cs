/*
 * Fetches the list of public STAC APIs from STAC Index (https://stacindex.org) so the
 * "Bring Your Own API" dialog can offer them in a dropdown.
 *
 * Ported from kyfromabove-ext's StacIndexClient.cs (same name, same logic, no KyFromAbove-specific
 * behavior beyond the BuiltIn entry's own description below) -- these are two independent add-ins
 * (see README), so this is duplicated rather than shared as a library.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KylidarAddin.Stac
{
    /// <summary>One selectable STAC API from STAC Index. <c>Name</c> is what the dialog's Name box gets filled with.</summary>
    public sealed record StacIndexCatalog(string Title, string Name, string Url, string Detail, bool IsBuiltIn);

    public static class StacIndexClient
    {
        private const string CatalogsUrl = "https://stacindex.org/api/catalogs";

        /// <summary>Name of the built-in source (see KylidarDockpaneViewModel), used when the user picks Kentucky's catalog.</summary>
        public const string BuiltInName = "KyFromAbove";

        /// <summary>
        /// Kentucky's own catalog. Always offered, first in the list and even when STAC Index can't be reached,
        /// so it's the way back after switching to another API.
        /// </summary>
        public static StacIndexCatalog BuiltIn { get; } = new(
            "KyFromAbove (Kentucky's default catalog)", BuiltInName, StacClient.DefaultBaseUri,
            StacClient.DefaultBaseUri + "\n\nKentucky Aerial Photography and Elevation Data (KYAPED), 2010 - present: " +
            "LiDAR point clouds (COPC/LAZ), by phase.",
            IsBuiltIn: true);

        private static readonly HttpClient _http = CreateClient();
        private static IReadOnlyList<StacIndexCatalog> _cache;

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KylidarAddin/1.0");
            return http;
        }

        /// <summary>
        /// Public, searchable STAC APIs from STAC Index, sorted by title. The list is cached for the
        /// life of the process, so reopening the dialog doesn't refetch it.
        /// </summary>
        public static async Task<IReadOnlyList<StacIndexCatalog>> GetSearchableCatalogsAsync(CancellationToken ct = default)
        {
            if (_cache != null) return _cache;

            using var resp = await _http.GetAsync(CatalogsUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var list = new List<StacIndexCatalog>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // isApi: a live STAC API rather than a static catalog. Protected/private catalogs
                // need credentials, which this add-in has no way to supply.
                if (!GetBool(el, "isApi") || GetBool(el, "isPrivate") || GetString(el, "access") != "public") continue;

                var title = GetString(el, "title");
                var url = GetString(el, "url")?.Trim();
                if (string.IsNullOrWhiteSpace(title) || !IsUsableBaseUrl(url)) continue;

                var baseUrl = url.TrimEnd('/');
                if (string.Equals(baseUrl, StacClient.DefaultBaseUri, StringComparison.OrdinalIgnoreCase)) continue; // BuiltIn covers it

                var summary = GetString(el, "summary");
                if (summary != null && summary.Length > 240) summary = summary.Substring(0, 240).TrimEnd() + "...";
                var detail = string.IsNullOrWhiteSpace(summary) ? url : url + "\n\n" + summary;
                list.Add(new StacIndexCatalog(title.Trim(), title.Trim(), baseUrl, detail, IsBuiltIn: false));
            }

            _cache = list.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).Prepend(BuiltIn).ToList();
            return _cache;
        }

        // This add-in talks to {base}/collections and {base}/search, so anything that isn't a plain
        // API root is dead weight in the list: static catalogs mislabeled as APIs (".json"), openEO
        // endpoints (a different API), and URLs carrying a query string/API key.
        private static bool IsUsableBaseUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.Query)) return false;
            if (uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.IndexOf("openeo", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static string GetString(JsonElement el, string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        private static bool GetBool(JsonElement el, string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    }
}
