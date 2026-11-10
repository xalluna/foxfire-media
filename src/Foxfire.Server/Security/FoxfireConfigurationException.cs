namespace Foxfire.Server.Security;

/// <summary>Startup configuration is invalid. Messages are written to never contain secret values.</summary>
public sealed class FoxfireConfigurationException(IReadOnlyList<string> problems)
    : Exception("FoxfireMedia configuration is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => " - " + p)))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
