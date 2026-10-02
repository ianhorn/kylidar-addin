/*
 * Backs the Feedback dialog's two paths:
 *   - BuildGitHubIssueUrl: a pre-filled "new issue" link opened in the user's browser. Requires a
 *     GitHub account (GitHub itself has no anonymous issue submission).
 *   - SendDirectAsync: posts to a Formspree form (https://formspree.io/f/xppweeda) via plain HTTP,
 *     not a web page, so a recipient address never needs to live in this add-in's source or
 *     compiled binary -- it's configured privately in the Formspree dashboard. The Referer header
 *     is set to the project's own docs site (true, and the nearest thing a desktop app has to an
 *     "origin page") since Formspree's spam heuristics are tuned around browser submissions, which
 *     always carry one.
 *
 * GetEnvironmentSummary is appended to every direct message so bug reports arrive with the add-in,
 * ArcGIS Pro, and OS versions already attached -- the three things normally asked for first.
 */
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KylidarAddin.Services
{
    public static class FeedbackService
    {
        private const string FormspreeEndpoint = "https://formspree.io/f/xppweeda";
        /// <summary>Also the dock pane's Help button target, and the Referer this class sends with
        /// a direct-feedback POST (see SendDirectAsync) -- one URL, reused for both.</summary>
        public const string DocsSiteUrl = "https://ianhorn.github.io/kylidar-addin/";
        public const string RepoUrl = "https://github.com/ianhorn/kylidar-addin";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>A pre-filled GitHub "new issue" URL -- open with Process.Start(..., UseShellExecute = true).</summary>
        public static string BuildGitHubIssueUrl()
        {
            var body =
                "**What happened?**\n\n\n**What did you expect?**\n\n\n**Steps to reproduce**\n1. \n\n" +
                "---\n" + "Environment: " + GetEnvironmentSummary();
            return $"{RepoUrl}/issues/new?title={Uri.EscapeDataString("")}&body={Uri.EscapeDataString(body)}";
        }

        /// <summary>
        /// Send a feedback message directly, with the environment summary appended. replyToEmail is
        /// optional (the sender's own address, for a reply) and is never required -- this is the
        /// no-account, no-visible-recipient path. Returns (ok, error) -- error is null on success.
        /// </summary>
        public static async Task<(bool ok, string error)> SendDirectAsync(string message, string replyToEmail, CancellationToken ct)
        {
            var fullMessage = message.TrimEnd() + "\n\n---\nEnvironment: " + GetEnvironmentSummary();
            var payload = new
            {
                message = fullMessage,
                _subject = "Kylidar Add-in feedback",
                _replyto = string.IsNullOrWhiteSpace(replyToEmail) ? null : replyToEmail.Trim(),
                _gotcha = "" // honeypot: a real browser form leaves this hidden field empty; bots often fill every field
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, FormspreeEndpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Referrer = new Uri(DocsSiteUrl);

            try
            {
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return (true, null);

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (false, $"Formspree returned {(int)response.StatusCode}: {Summarize(body)}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>Pull just the first "message" field out of Formspree's JSON error body, if present, rather than dumping the whole response.</summary>
        private static string Summarize(string jsonBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonBody);
                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 &&
                    errors[0].TryGetProperty("message", out var msg))
                    return msg.GetString();
            }
            catch (JsonException) { /* fall through to the raw body */ }
            return jsonBody;
        }

        /// <summary>"Kylidar 1.0.0.0 | ArcGIS Pro 3.6.5.59530 | Microsoft Windows 10.0.26100" -- each
        /// part best-effort; a part that can't be determined is just left out rather than failing
        /// the whole summary.</summary>
        public static string GetEnvironmentSummary()
        {
            var parts = new System.Collections.Generic.List<string> { "Kylidar " + AddInVersion() };
            var proVersion = ArcGisProVersion();
            if (proVersion != null) parts.Add("ArcGIS Pro " + proVersion);
            parts.Add(RuntimeInformation.OSDescription.Trim());
            return string.Join(" | ", parts);
        }

        private static string AddInVersion()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"; }
            catch { return "unknown"; }
        }

        /// <summary>Same registry lookup PdalRunner.FindPythonExe uses to locate the Pro install,
        /// then reads the installed ArcGISPro.exe's own file version -- avoids guessing at an SDK
        /// API for "the running Pro version" that may not exist.</summary>
        private static string ArcGisProVersion()
        {
            try
            {
                string installDir;
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ESRI\ArcGISPro"))
                    installDir = key?.GetValue("InstallDir") as string;
                installDir ??= @"C:\Program Files\ArcGIS\Pro\";

                var exePath = Path.Combine(installDir, "bin", "ArcGISPro.exe");
                return File.Exists(exePath) ? FileVersionInfo.GetVersionInfo(exePath).ProductVersion : null;
            }
            catch { return null; }
        }
    }
}
