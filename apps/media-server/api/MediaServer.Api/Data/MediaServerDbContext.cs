using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Data;

public sealed class MediaServerDbContext(DbContextOptions<MediaServerDbContext> options)
    : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var appUser = modelBuilder.Entity<AppUser>();
        appUser.HasKey(user => user.Id);
        appUser.Property(user => user.HostUserId).IsRequired();
        appUser.HasIndex(user => user.HostUserId).IsUnique();
        appUser.HasIndex(user => user.Email);
        appUser.Property(user => user.Role).HasConversion<int>();
    }
}
