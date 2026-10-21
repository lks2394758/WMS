using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wms.Domain.Catalog;
using Wms.Domain.Inventory;

namespace Wms.Persistence.Sqlite;

/// <summary>
/// The demonstration schema. Keys, unique indexes, foreign keys and check constraints mirror the domain rules so a
/// bad write fails in the database too; the ledger and audit tables are append-only (triggers in the migration).
/// Timestamps are stored as UTC ticks (INTEGER) so they compare and sort correctly in SQL.
/// </summary>
internal sealed class WmsDbContext(DbContextOptions<WmsDbContext> options) : DbContext(options)
{
    /// <summary>Trigger names created by the <c>InitialSqliteSchema</c> migration.</summary>
    internal static class Triggers
    {
        public const string TransactionsNoUpdate = "TR_InventoryTransactions_NoUpdate";
        public const string TransactionsNoDelete = "TR_InventoryTransactions_NoDelete";
        public const string LinesNoUpdate = "TR_InventoryTransactionLines_NoUpdate";
        public const string LinesNoDelete = "TR_InventoryTransactionLines_NoDelete";
        public const string AuditNoUpdate = "TR_AuditEntries_NoUpdate";
        public const string AuditNoDelete = "TR_AuditEntries_NoDelete";
    }

    public DbSet<ProductRow> Products => Set<ProductRow>();
    public DbSet<LocationRow> Locations => Set<LocationRow>();
    public DbSet<BarcodeRow> Barcodes => Set<BarcodeRow>();
    public DbSet<BalanceRow> Balances => Set<BalanceRow>();
    public DbSet<InventoryTransactionRow> InventoryTransactions => Set<InventoryTransactionRow>();
    public DbSet<InventoryTransactionLineRow> InventoryTransactionLines => Set<InventoryTransactionLineRow>();
    public DbSet<TransactionSequenceRow> TransactionSequences => Set<TransactionSequenceRow>();
    public DbSet<AuditEntryRow> AuditEntries => Set<AuditEntryRow>();
    public DbSet<IdempotencyRow> IdempotencyRecords => Set<IdempotencyRow>();
    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<RefreshTokenRow> RefreshTokens => Set<RefreshTokenRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProductRow>(e =>
        {
            e.ToTable("Products", t => t.HasCheckConstraint("CK_Products_Capacity", "\"Capacity\" IS NULL OR \"Capacity\" > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Sku).HasMaxLength(Product.MaxSkuLength).IsRequired();
            e.Property(x => x.NormalizedSku).HasMaxLength(Product.MaxSkuLength).IsRequired();
            e.HasIndex(x => x.NormalizedSku).IsUnique();
            e.HasIndex(x => x.Sku); // page order
            e.Property(x => x.Name).HasMaxLength(Product.MaxNameLength).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<LocationRow>(e =>
        {
            e.ToTable("Locations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(Location.MaxCodeLength).IsRequired();
            e.HasIndex(x => new { x.WarehouseId, x.Code }).IsUnique();
            e.HasIndex(x => x.Code); // page order
        });

        modelBuilder.Entity<BarcodeRow>(e =>
        {
            e.ToTable("Barcodes");
            e.HasKey(x => x.Value);
            e.Property(x => x.Value).HasMaxLength(Barcode.MaxLength);
            e.HasIndex(x => x.ProductId);
            e.HasOne<ProductRow>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BalanceRow>(e =>
        {
            e.ToTable("InventoryBalances", t =>
                t.HasCheckConstraint("CK_InventoryBalances_NonNegative", "\"AvailableQuantity\" >= 0")); // rule 2
            e.HasKey(x => new { x.WarehouseId, x.LocationId, x.ProductId });
            e.Property(x => x.PageKey).HasMaxLength(65).IsRequired();
            e.HasIndex(x => x.PageKey).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<LocationRow>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductRow>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryTransactionRow>(e =>
        {
            e.ToTable("InventoryTransactions", t =>
            {
                t.HasCheckConstraint("CK_InventoryTransactions_Type", "\"Type\" BETWEEN 1 AND 5");
                t.HasTrigger(Triggers.TransactionsNoUpdate);
                t.HasTrigger(Triggers.TransactionsNoDelete);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Number).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.OccurredAt);
            e.Property(x => x.Reason).HasMaxLength(InventoryTransaction.MaxReasonLength);
            e.Property(x => x.ReferenceNo).HasMaxLength(InventoryTransaction.MaxReferenceLength);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryTransactionLineRow>(e =>
        {
            e.ToTable("InventoryTransactionLines", t =>
            {
                t.HasCheckConstraint("CK_InventoryTransactionLines_Quantity", "\"Quantity\" >= 0");
                t.HasCheckConstraint(
                    "CK_InventoryTransactionLines_Snapshots",
                    "(\"FromAfter\" IS NULL OR \"FromAfter\" >= 0) AND (\"ToAfter\" IS NULL OR \"ToAfter\" >= 0)");
                t.HasTrigger(Triggers.LinesNoUpdate);
                t.HasTrigger(Triggers.LinesNoDelete);
            });
            e.HasKey(x => new { x.TransactionId, x.LineNo });
            e.HasIndex(x => x.ProductId);
            e.HasOne<ProductRow>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LocationRow>().WithMany().HasForeignKey(x => x.FromLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LocationRow>().WithMany().HasForeignKey(x => x.ToLocationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransactionSequenceRow>(e =>
        {
            e.ToTable("TransactionSequences", t => t.HasCheckConstraint(
                "CK_TransactionSequences_Range", $"\"LastValue\" BETWEEN 0 AND {TransactionNumber.MaxSequence}"));
            e.HasKey(x => new { x.Type, x.BusinessDate });
        });

        modelBuilder.Entity<AuditEntryRow>(e =>
        {
            e.ToTable("AuditEntries", t =>
            {
                t.HasTrigger(Triggers.AuditNoUpdate);
                t.HasTrigger(Triggers.AuditNoDelete);
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.Entity<IdempotencyRow>(e =>
        {
            e.ToTable("IdempotencyRecords");
            e.HasKey(x => new { x.UserId, x.Key });
            e.Property(x => x.Operation).HasMaxLength(32).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.ResponseJson).IsRequired();
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserRow>(e =>
        {
            e.ToTable("Users", t => t.HasCheckConstraint("CK_Users_Role", "\"Role\" BETWEEN 1 AND 4"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.NormalizedUsername).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<RefreshTokenRow>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>UTC ticks. Offsets are never stored: every time in the system is UTC (AGENTS.md).</summary>
    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
