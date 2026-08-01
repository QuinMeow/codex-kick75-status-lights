// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Ipc;
using AgentKick75.App.Web;

namespace AgentKick75.App.Hooks;

internal sealed record LoopbackHookEndpoint(int Version, string BaseUri, string Token)
{
    public const int CurrentVersion = 1;
    public const string FileName = "hook-endpoint.json";
    private const int MaximumFileBytes = 4096;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentKick75",
        FileName);

    public static LoopbackHookEndpoint Create(Uri baseUri, string token)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return new LoopbackHookEndpoint(CurrentVersion, baseUri.AbsoluteUri, token);
    }

    public string Serialize() => JsonSerializer.Serialize(this, PipeJson.Options);

    public static bool TryLoad(string path, out LoopbackHookEndpoint? endpoint)
    {
        endpoint = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumFileBytes)
            {
                return false;
            }

            string json = File.ReadAllText(path);
            LoopbackHookEndpoint? candidate = JsonSerializer.Deserialize<LoopbackHookEndpoint>(
                json,
                PipeJson.Options);
            if (candidate is null ||
                candidate.Version != CurrentVersion ||
                !ControlPageOptions.IsValidToken(candidate.Token) ||
                !Uri.TryCreate(candidate.BaseUri, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
                !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
                uri.Port <= 0 ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            endpoint = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException)
        {
            return false;
        }
    }
}
