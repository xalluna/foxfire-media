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
/// <remarks>
/// The player module is shared with the admin console, so Vite emits it (and its stylesheet) as a
/// separate chunk. The entry's own <c>css</c> list does not include that chunk's styles, so the
/// manifest's <c>imports</c> graph is walked to collect every stylesheet and preloadable module.
/// </remarks>
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

        var styles = new List<string>();
        var preloads = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Collect(JsonElement chunk, bool isEntry)
        {
            // Imported chunks' styles first, so the entry's own rules win on equal specificity.
            if (chunk.TryGetProperty("imports", out var imports) && imports.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in imports.EnumerateArray().Select(i => i.GetString()).OfType<string>())
                {
                    if (visited.Add(key) && doc.RootElement.TryGetProperty(key, out var imported))
                    {
                        Collect(imported, isEntry: false);
                    }
                }
            }

            if (!isEntry && chunk.TryGetProperty("file", out var chunkFile) && chunkFile.GetString() is { } js)
            {
                preloads.Add("/" + js);
            }

            if (chunk.TryGetProperty("css", out var css) && css.ValueKind == JsonValueKind.Array)
            {
                foreach (var sheet in css.EnumerateArray().Select(c => c.GetString()).OfType<string>())
                {
                    if (!styles.Contains("/" + sheet, StringComparer.Ordinal))
                    {
                        styles.Add("/" + sheet);
                    }
                }
            }
        }

        Collect(entry, isEntry: true);
        Styles = styles;
        Preloads = preloads;
    }

    public string? Script { get; }

    /// <summary>Every stylesheet the player needs, imported chunks first.</summary>
    public IReadOnlyList<string> Styles { get; } = [];

    /// <summary>Imported JS chunks, preloaded so the player starts without a request waterfall.</summary>
    public IReadOnlyList<string> Preloads { get; } = [];
}
