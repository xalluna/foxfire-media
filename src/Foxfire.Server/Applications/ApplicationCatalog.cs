using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Foxfire.Server.Security;

namespace Foxfire.Server.Applications;

/// <summary>An application declared in configuration, joined with its key(s) from the environment.</summary>
public sealed record ConfiguredApplication(
    string Name,
    string DisplayName,
    Uri? WebhookUrl,
    IReadOnlyList<string> AllowedOrigins,
    Secret ApiKey,
    Secret? SecondaryApiKey)
{
    public override string ToString() => $"ConfiguredApplication {{ Name = {Name} }}";
}

/// <summary>
/// The validated set of applications. Built once at startup; any problem aborts startup with
/// messages that name the application but never a secret value.
/// </summary>
public sealed partial class ApplicationCatalog
{
    public const string ApplicationsSection = "Applications";
    public const string ApiKeysSection = "ApiKeys";
    public const string SecondaryApiKeysSection = "ApiKeysSecondary";

    private readonly FrozenDictionary<string, ConfiguredApplication> _byName;

    private ApplicationCatalog(IReadOnlyList<ConfiguredApplication> applications)
    {
        Applications = applications;
        _byName = applications.ToFrozenDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ConfiguredApplication> Applications { get; }

    public ConfiguredApplication? Find(string name) => _byName.GetValueOrDefault(name);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{1,31}$")]
    private static partial Regex NamePattern();

    public static ApplicationCatalog Load(IConfiguration configuration, bool allowInsecureWebhooks)
    {
        var errors = new List<string>();

        errors.AddRange(FindKeysOutsideEnvironment(configuration));

        var declared = configuration.GetSection(ApplicationsSection).Get<List<ApplicationConfig>>() ?? [];
        var primaryKeys = ReadKeys(configuration.GetSection(ApiKeysSection));
        var secondaryKeys = ReadKeys(configuration.GetSection(SecondaryApiKeysSection));

        var apps = new List<ConfiguredApplication>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < declared.Count; i++)
        {
            var config = declared[i];
            var label = string.IsNullOrWhiteSpace(config.Name) ? $"Applications[{i}]" : $"application '{config.Name}'";

            if (string.IsNullOrWhiteSpace(config.Name) || !NamePattern().IsMatch(config.Name))
            {
                errors.Add($"{label}: Name is required and must match ^[A-Za-z][A-Za-z0-9_]{{1,31}}$ (it is used in the ApiKeys__<Name> variable).");
                continue;
            }

            if (!seenNames.Add(config.Name))
            {
                errors.Add($"{label}: Name is declared more than once (names are case-insensitive).");
                continue;
            }

            var origins = new List<string>();
            foreach (var origin in config.AllowedOrigins ?? [])
            {
                var normalized = OriginRules.Normalize(origin);
                if (normalized is null)
                {
                    errors.Add($"{label}: AllowedOrigins entry '{origin}' must be an exact http(s)://host[:port] origin with no path, query or wildcard.");
                }
                else if (!origins.Contains(normalized, StringComparer.Ordinal))
                {
                    origins.Add(normalized);
                }
            }

            Uri? webhook = null;
            if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
            {
                if (!Uri.TryCreate(config.WebhookUrl, UriKind.Absolute, out webhook) ||
                    (webhook.Scheme != Uri.UriSchemeHttps && !(allowInsecureWebhooks && webhook.Scheme == Uri.UriSchemeHttp)) ||
                    !string.IsNullOrEmpty(webhook.UserInfo))
                {
                    errors.Add($"{label}: WebhookUrl must be an absolute https URL without credentials.");
                    webhook = null;
                }
            }

            if (!primaryKeys.Remove(config.Name, out var key))
            {
                errors.Add($"{label}: no API key found. Set the environment variable {ApiKeysSection}__{config.Name}.");
                continue;
            }

            if (SecretPolicy.Validate(key) is { } keyProblem)
            {
                errors.Add($"{label}: API key {keyProblem}.");
            }

            secondaryKeys.Remove(config.Name, out var secondary);
            if (secondary is not null && SecretPolicy.Validate(secondary) is { } secondaryProblem)
            {
                errors.Add($"{label}: secondary API key {secondaryProblem}.");
            }

            var displayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Name : config.DisplayName.Trim();
            apps.Add(new ConfiguredApplication(config.Name, displayName, webhook, origins, key, secondary));
        }

        foreach (var orphan in primaryKeys.Keys)
        {
            errors.Add($"{ApiKeysSection}__{orphan} is set but no application named '{orphan}' is declared in {ApplicationsSection}.");
        }

        foreach (var orphan in secondaryKeys.Keys)
        {
            errors.Add($"{SecondaryApiKeysSection}__{orphan} is set but no application named '{orphan}' is declared in {ApplicationsSection}.");
        }

        errors.AddRange(FindDuplicateKeys(apps));

        if (errors.Count > 0)
        {
            throw new FoxfireConfigurationException(errors);
        }

        return new ApplicationCatalog(apps);
    }

    private static Dictionary<string, Secret> ReadKeys(IConfigurationSection section)
    {
        var keys = new Dictionary<string, Secret>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrEmpty(child.Value))
            {
                keys[child.Key] = new Secret(child.Value);
            }
        }

        return keys;
    }

    private static IEnumerable<string> FindDuplicateKeys(List<ConfiguredApplication> apps)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var app in apps)
        {
            foreach (var (secret, kind) in new[] { (app.ApiKey, "API key"), (app.SecondaryApiKey, "secondary API key") })
            {
                if (secret is null)
                {
                    continue;
                }

                var hash = Convert.ToHexString(secret.Sha256());
                if (seen.TryGetValue(hash, out var owner))
                {
                    yield return $"application '{app.Name}': {kind} is identical to the key of {owner}. Every key must be unique.";
                }
                else
                {
                    seen[hash] = $"application '{app.Name}'";
                }
            }
        }
    }

    /// <summary>
    /// API keys may only come from environment variables (or in-memory sources used by tests).
    /// Any key supplied by a configuration file, stream or the command line aborts startup, and an
    /// ApiKey field on an application entry is refused wherever it comes from.
    /// </summary>
    internal static IEnumerable<string> FindKeysOutsideEnvironment(IConfiguration configuration)
    {
        foreach (var provider in SecretSourceGuard.Providers(configuration))
        {
            foreach (var index in provider.GetChildKeys([], ApplicationsSection).Distinct())
            {
                if (provider.TryGet($"{ApplicationsSection}:{index}:ApiKey", out _) ||
                    provider.TryGet($"{ApplicationsSection}:{index}:ApiKeys", out _))
                {
                    yield return $"{ApplicationsSection}[{index}] contains an ApiKey. API keys must come from the environment (set {ApiKeysSection}__<Name>), never from {SecretSourceGuard.Describe(provider)}.";
                }
            }
        }

        foreach (var violation in SecretSourceGuard.FindViolations(configuration, [ApiKeysSection, SecondaryApiKeysSection]))
        {
            yield return violation;
        }
    }

    private sealed class ApplicationConfig
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? WebhookUrl { get; set; }
        public List<string>? AllowedOrigins { get; set; }
    }
}
