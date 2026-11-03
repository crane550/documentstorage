using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocumentStorage.Data;

public sealed class CatalogDesignFactory : IDesignTimeDbContextFactory<CatalogDb>
{
    public CatalogDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<CatalogDb>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Catalog") ??
            "Host=127.0.0.1;Port=55432;Database=documentstorage_dev;Username=postgres").Options);
}
