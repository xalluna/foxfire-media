using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Foxfire.Server.Data;

public sealed class FoxfireDbContext(DbContextOptions<FoxfireDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>ASP.NET Core Data Protection key ring (admin cookie encryption). Not API keys.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FoxfireDbContext).Assembly);
    }
}
