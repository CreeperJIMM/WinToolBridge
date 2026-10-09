using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace WinToolBridge;

public static class UpdateChecker
{
    public const string GitHubRepository = "CreeperJIMM/WinToolBridge";
    public const string GitHubUrl = "https://github.com/" + GitHubRepository;
    public const string LatestReleaseUrl = GitHubUrl + "/releases/latest";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/" + GitHubRepository + "/releases/latest";
    private const string SettingsKey = @"Software\WinToolBridge";
    private const string DisableCheckValue = "DisableAutomaticUpdateCheck";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WinToolBridge/" + LocalBridgeServer.SERVER_VERSION);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public static bool IsAutomaticCheckEnabled()
    {
        try {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
            return key?.GetValue(DisableCheckValue) is not int disabled || disabled == 0;
        }
        catch (Exception ex) {
            Debug.WriteLine($"Unable to read update preference: {ex.Message}");
            return true;
        }
    }

    public static void SetAutomaticCheckEnabled(bool enabled)
    {
        if (!enabled) {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
            key.SetValue(DisableCheckValue, 1, RegistryValueKind.DWord);
            return;
        }

        bool isEmpty;
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey, true)) {
            if (key == null) return;
            key.DeleteValue(DisableCheckValue, false);
            isEmpty = key.ValueCount == 0 && key.SubKeyCount == 0;
        }
        if (isEmpty) Registry.CurrentUser.DeleteSubKey(SettingsKey, false);
    }

    public static async Task<UpdateCheckResult?> CheckAsync(CancellationToken token)
    {
        using var response = await Client.GetAsync(LatestReleaseApiUrl, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False) {
            throw new InvalidDataException("Unexpected GitHub release response.");
        }

        var latestVersion = ParseVersion(tag.GetString()!);
        var currentVersion = ParseVersion(LocalBridgeServer.SERVER_VERSION);
        return new UpdateCheckResult(latestVersion.ToString(3), latestVersion > currentVersion);
    }

    private static Version ParseVersion(string text)
    {
        var value = text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
        value = value.Split('+')[0];
        if (!Version.TryParse(value, out var version) || version.Build < 0) {
            throw new InvalidDataException("Invalid release version.");
        }
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    public static void OpenGitHub() => OpenPage(GitHubUrl);

    public static void OpenLatestRelease() => OpenPage(LatestReleaseUrl);

    private static void OpenPage(string url)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}

public sealed record UpdateCheckResult(string LatestVersion, bool HasUpdate);
