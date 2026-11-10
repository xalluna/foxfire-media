using System.Text.Json;

namespace Foxfire.Server.Embed;

public static class EmbedServiceExtensions
{
    public static IServiceCollection AddFoxfireEmbeds(this IServiceCollection services)
    {
        services.AddOptions<EmbedOptions>().BindConfiguration(EmbedOptions.Section);
        services.AddSingleton<EmbedAssets>();
        return services;
    }
}

/// <summary>
/// Locates the hashed embed-player bundle through Vite's build manifest (wwwroot/build-manifest.json).
/// When no web build is present (e.g. tests) the page renders without the player script.
/// </summary>
public sealed class EmbedAssets
{
    public const string EntryName = "src/embed/main.ts";

    /// <summary>Written by Vite (build.manifest) at the web root; read here, never served.</summary>
    public const string ManifestFileName = "build-manifest.json";

    public EmbedAssets(IWebHostEnvironment environment)
    {
        var manifest = environment.WebRootPath is { } root ? Path.Combine(root, ManifestFileName) : null;
        if (manifest is null || !File.Exists(manifest))
        {
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!doc.RootElement.TryGetProperty(EntryName, out var entry))
        {
            return;
        }

        Script = entry.TryGetProperty("file", out var file) ? "/" + file.GetString() : null;
        Styles = entry.TryGetProperty("css", out var css) && css.ValueKind == JsonValueKind.Array
            ? [.. css.EnumerateArray().Select(c => "/" + c.GetString())]
            : [];
    }

    public string? Script { get; }

    public IReadOnlyList<string> Styles { get; } = [];
}
