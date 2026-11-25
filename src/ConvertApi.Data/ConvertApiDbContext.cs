using ConvertApi.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace ConvertApi.Data;

public sealed class ConvertApiDbContext(DbContextOptions<ConvertApiDbContext> options) : DbContext(options)
{
    public DbSet<ConversionJob> Jobs => Set<ConversionJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ConvertApiDbContext).Assembly);
    }
}
