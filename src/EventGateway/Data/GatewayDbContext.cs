using EventGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace EventGateway.Data;

/// <summary>EF Core database context for the Event Gateway — owns the Events table.</summary>
public class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    /// <summary>Gets the set of persisted event records.</summary>
    public DbSet<EventRecord> Events => Set<EventRecord>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>()
            .HasKey(e => e.EventId);

        modelBuilder.Entity<EventRecord>()
            .HasIndex(e => e.AccountId);

        // Composite index supports the GET /events?account= query ordered by eventTimestamp
        modelBuilder.Entity<EventRecord>()
            .HasIndex(e => new { e.AccountId, e.EventTimestamp });
    }
}
