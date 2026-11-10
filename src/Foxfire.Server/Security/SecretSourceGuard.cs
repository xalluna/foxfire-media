using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Foxfire.Server.Security;

/// <summary>
/// Secrets may only come from environment variables (or in-memory sources, which only tests use).
/// A secret supplied by a configuration file, a JSON stream, or the command line (visible in process
/// listings) is reported so startup can refuse to run.
/// </summary>
public static class SecretSourceGuard
{
    public static IEnumerable<string> FindViolations(IConfiguration configuration, IEnumerable<string> secretPaths)
    {
        var paths = secretPaths.ToList();
        foreach (var provider in Providers(configuration))
        {
            if (provider is EnvironmentVariablesConfigurationProvider or MemoryConfigurationProvider)
            {
                continue;
            }

            foreach (var path in paths)
            {
                if (provider.TryGet(path, out _) || provider.GetChildKeys([], path).Any())
                {
                    yield return $"{path} was found in {Describe(provider)}. Secrets must come from the environment (e.g. {path.Replace(":", "__", StringComparison.Ordinal)}).";
                }
            }
        }
    }

    public static IEnumerable<IConfigurationProvider> Providers(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
        {
            yield break;
        }

        foreach (var provider in root.Providers)
        {
            if (provider is ChainedConfigurationProvider chained)
            {
                foreach (var inner in Providers(chained.Configuration))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return provider;
            }
        }
    }

    public static string Describe(IConfigurationProvider provider) => provider switch
    {
        FileConfigurationProvider file => $"configuration file '{file.Source.Path}'",
        CommandLineConfigurationProvider => "the command line",
        _ => $"configuration source {provider.GetType().Name}",
    };
}
