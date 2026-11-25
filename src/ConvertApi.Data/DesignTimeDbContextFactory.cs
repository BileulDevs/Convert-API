using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ConvertApi.Data;

/// <summary>Lets <c>dotnet ef</c> build the model without starting the API. Never connects.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ConvertApiDbContext>
{
    public ConvertApiDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ConvertApiDbContext>()
            .UseNpgsql("Host=localhost;Database=convertapi")
            .Options);
}
