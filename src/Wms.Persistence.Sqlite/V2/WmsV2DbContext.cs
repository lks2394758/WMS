using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Wms.Persistence.Sqlite.V2;

/// <summary>
/// The API v2 database (<c>App_Data/wms-v2-demo.db</c>), fully separate from <see cref="WmsDbContext"/> and the v1 file.
/// The schema of all 16 business tables is defined by the hand-written migration <c>InitialV2Schema</c> (SQL with
/// checks, partial unique indexes and triggers); this model maps the tables B1 reads and writes. Timestamps are UTC
/// ticks (INTEGER) as in v1.
/// </summary>
internal sealed class WmsV2DbContext(DbContextOptions<WmsV2DbContext> options) : DbContext(options)
{
    public DbSet<V2UserRow> Users => Set<V2UserRow>();
    public DbSet<V2LoginSessionRow> LoginSessions => Set<V2LoginSessionRow>();
    public DbSet<V2SystemSettingsRow> SystemSettings => Set<V2SystemSettingsRow>();
    public DbSet<V2UnitRow> Units => Set<V2UnitRow>();
    public DbSet<V2ActivityLogRow> ActivityLogs => Set<V2ActivityLogRow>();
    public DbSet<V2CategoryRow> Categories => Set<V2CategoryRow>();
    public DbSet<V2ProductRow> Products => Set<V2ProductRow>();
    public DbSet<V2ProductCategoryRow> ProductCategories => Set<V2ProductCategoryRow>();
    public DbSet<V2BinRow> Bins => Set<V2BinRow>();
    public DbSet<V2CountTaskRow> CountTasks => Set<V2CountTaskRow>();
    public DbSet<V2CountResultRow> CountResults => Set<V2CountResultRow>();
    public DbSet<V2AlertRow> Alerts => Set<V2AlertRow>();
    public DbSet<V2TransactionRow> InventoryTransactions => Set<V2TransactionRow>();
    public DbSet<V2TransactionLineRow> InventoryTransactionLines => Set<V2TransactionLineRow>();
    public DbSet<V2TransactionLineItemRow> TransactionLineItems => Set<V2TransactionLineItemRow>();
    public DbSet<V2SerializedItemRow> SerializedItems => Set<V2SerializedItemRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Declaring the triggers makes EF Core avoid RETURNING on these tables (as for the v1 ledger).
        modelBuilder.Entity<V2UserRow>(e =>
        {
            e.ToTable("Users", t =>
            {
                t.HasTrigger("TR_Users_RolesInsert");
                t.HasTrigger("TR_Users_RolesUpdate");
                t.HasTrigger(V2Schema.NoDeleteTriggerName("Users"));
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2LoginSessionRow>(e =>
        {
            e.ToTable("LoginSessions", t =>
            {
                t.HasTrigger("TR_LoginSessions_RoleAssignedInsert");
                t.HasTrigger("TR_LoginSessions_RoleAssignedUpdate");
                t.HasTrigger("TR_LoginSessions_Immutable");
                t.HasTrigger(V2Schema.NoDeleteTriggerName("LoginSessions"));
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<V2UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<V2SystemSettingsRow>(e =>
        {
            e.ToTable("SystemSettings", t => t.HasTrigger(V2Schema.NoDeleteTriggerName("SystemSettings")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2UnitRow>(e =>
        {
            e.ToTable("Units", t => t.HasTrigger("TR_Units_SystemNoDelete"));
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2ActivityLogRow>(e =>
        {
            e.ToTable("ActivityLogs", t =>
            {
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("ActivityLogs", "UPDATE"));
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("ActivityLogs", "DELETE"));
            });
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<V2CategoryRow>(e =>
        {
            e.ToTable("Categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2ProductRow>(e =>
        {
            e.ToTable("Products", t =>
            {
                t.HasTrigger("TR_Products_CreationFixed");
                t.HasTrigger("TR_Products_HistoricalSkusReservedInsert");
                t.HasTrigger("TR_Products_HistoricalBarcodesReservedInsert");
                t.HasTrigger(V2Schema.NoDeleteTriggerName("Products"));
            });
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.NormalizedSku).IsUnique();
            e.HasIndex(x => x.NormalizedBarcode).IsUnique();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2ProductCategoryRow>(e =>
        {
            e.ToTable("ProductCategories");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<V2BinRow>(e =>
        {
            e.ToTable("Bins", t => t.HasTrigger(V2Schema.NoDeleteTriggerName("Bins")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2CountTaskRow>(e =>
        {
            e.ToTable("CountTasks", t => t.HasTrigger("TR_CountTasks_CreationFixed"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<V2CountResultRow>(e =>
        {
            e.ToTable("CountResults", t => t.HasTrigger(V2Schema.AppendOnlyTriggerName("CountResults", "UPDATE")));
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<V2AlertRow>(e =>
        {
            e.ToTable("Alerts", t => t.HasTrigger("TR_Alerts_Immutable"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        // B3a ledger (append-only by trigger) and serial items.
        modelBuilder.Entity<V2TransactionRow>(e =>
        {
            e.ToTable("InventoryTransactions", t =>
            {
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("InventoryTransactions", "UPDATE"));
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("InventoryTransactions", "DELETE"));
            });
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<V2TransactionLineRow>(e =>
        {
            e.ToTable("InventoryTransactionLines", t =>
            {
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("InventoryTransactionLines", "UPDATE"));
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("InventoryTransactionLines", "DELETE"));
            });
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<V2TransactionLineItemRow>(e =>
        {
            e.ToTable("TransactionLineItems", t =>
            {
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("TransactionLineItems", "UPDATE"));
                t.HasTrigger(V2Schema.AppendOnlyTriggerName("TransactionLineItems", "DELETE"));
            });
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<V2SerializedItemRow>(e =>
        {
            e.ToTable("SerializedItems", t =>
            {
                t.HasTrigger("TR_SerializedItems_HistoricalReservedInsert");
                t.HasTrigger("TR_SerializedItems_ProductFixed");
                t.HasTrigger(V2Schema.NoDeleteTriggerName("SerializedItems"));
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
