using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Fireblocks.Data;

[PluginDatabase("BTCPayServer.Plugins.Fireblocks_Migrations")]
public sealed class FireblocksDbContext(DbContextOptions<FireblocksDbContext> options)
    : BasePluginDbContext<FireblocksDbContext>(options)
{
    public FireblocksDbContext() : this(new DbContextOptions<FireblocksDbContext>())
    {
    }

    public DbSet<FlowPaymentAttempt> PaymentAttempts => Set<FlowPaymentAttempt>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // The host configures Npgsql. Tests may configure another EF provider; the
        // base class would otherwise add Npgsql a second time.
        if (!optionsBuilder.IsConfigured)
            base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("Fireblocks");
        var attempt = modelBuilder.Entity<FlowPaymentAttempt>();
        attempt.ToTable("fireblocks_payment_attempts");
        attempt.HasKey(a => a.Id);
        attempt.Property(a => a.Id).HasColumnName("id").HasMaxLength(64);
        attempt.Property(a => a.StoreId).HasColumnName("store_id").HasMaxLength(100).IsRequired();
        attempt.Property(a => a.InvoiceId).HasColumnName("invoice_id").HasMaxLength(100).IsRequired();
        attempt.Property(a => a.AttemptNumber).HasColumnName("attempt_number");
        attempt.Property(a => a.CreatedAt).HasColumnName("created_at");
        attempt.Property(a => a.ExpiresAt).HasColumnName("expires_at");
        attempt.Property(a => a.ProviderExpiresAt).HasColumnName("provider_expires_at");
        attempt.Property(a => a.ApiEndpoint).HasColumnName("api_endpoint").HasMaxLength(2048).IsRequired();
        attempt.Property(a => a.EnvironmentId).HasColumnName("environment_id").HasMaxLength(200).IsRequired();
        attempt.Property(a => a.RequestedUsd).HasColumnName("requested_usd").HasColumnType("numeric(38,18)");
        attempt.Property(a => a.CreationRequest).HasColumnName("creation_request").IsRequired();
        attempt.Property(a => a.State).HasColumnName("state").HasConversion<int>();
        attempt.Property(a => a.FlowId).HasColumnName("flow_id").HasMaxLength(128);
        attempt.Property(a => a.PaymentUrl).HasColumnName("payment_url").HasMaxLength(2048);
        attempt.Property(a => a.Error).HasColumnName("error").HasMaxLength(512);
        attempt.HasIndex(a => new { a.StoreId, a.InvoiceId, a.AttemptNumber })
            .HasDatabaseName("ux_fireblocks_attempts_invoice_number")
            .IsUnique();
        attempt.HasIndex(a => a.FlowId)
            .HasDatabaseName("ix_fireblocks_attempts_flow_id")
            .IsUnique()
            .HasFilter("flow_id IS NOT NULL");
    }
}
