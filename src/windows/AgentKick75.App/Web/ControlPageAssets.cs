// SPDX-License-Identifier: MIT
using System.Reflection;

namespace AgentKick75.App.Web;

/// <summary>
/// Reads the control page from resources embedded in the application assembly.
/// The files in wwwroot are the single source of truth for these assets.
/// </summary>
internal static class ControlPageAssets
{
    public const string TokenPlaceholder = "__AGENT_KICK75_WRITE_TOKEN__";

    public static string IndexHtml { get; } = Read("AgentKick75.Web.index.html");

    public static string StylesCss { get; } = Read("AgentKick75.Web.styles.css");

    public static string AppJavaScript { get; } = Read("AgentKick75.Web.app.js");

    private static string Read(string resourceName)
    {
        Assembly assembly = typeof(ControlPageAssets).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded control-page resource '{resourceName}'.");
        using var reader = new StreamReader(
            stream,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
