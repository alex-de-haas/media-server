using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediaServer.Api.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the model without running the
/// app host or needing the injected HOSTY_* environment. The connection string here is only used
/// for migration scaffolding; the runtime path comes from <c>HOSTY_APP_DATA_DIR</c>.
/// </summary>
public sealed class MediaServerDbContextFactory : IDesignTimeDbContextFactory<MediaServerDbContext>
{
    public MediaServerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MediaServerDbContext>()
            .UseSqlite("Data Source=media-server.design.db")
            .Options;

        return new MediaServerDbContext(options);
    }
}
