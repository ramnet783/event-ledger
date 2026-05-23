using AccountService.Models;
using Microsoft.EntityFrameworkCore;

namespace AccountService.Data;

public class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>()
            .HasKey(a => a.AccountId);

        modelBuilder.Entity<Transaction>()
            .HasKey(t => t.Id);

        // Enforces idempotency at DB level — second insert with same eventId fails fast
        modelBuilder.Entity<Transaction>()
            .HasIndex(t => t.EventId)
            .IsUnique();

        modelBuilder.Entity<Transaction>()
            .HasIndex(t => t.AccountId);

        modelBuilder.Entity<Account>()
            .HasMany(a => a.Transactions)
            .WithOne()
            .HasForeignKey(t => t.AccountId);
    }
}
