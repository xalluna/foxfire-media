using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Foxfire.Server.Data;

/// <summary>Used only by <c>dotnet ef</c> tooling so migrations can be scaffolded without booting the app.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FoxfireDbContext>
{
    public FoxfireDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FoxfireDbContext>()
            .UseNpgsql("Host=localhost;Database=foxfire_design")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new FoxfireDbContext(options);
    }
}
