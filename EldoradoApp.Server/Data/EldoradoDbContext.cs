using Microsoft.EntityFrameworkCore;

namespace EldoradoApp.Server.Data;

public sealed class EldoradoDbContext(DbContextOptions<EldoradoDbContext> options) : DbContext(options)
{
    public DbSet<CustomerAccount> Customers => Set<CustomerAccount>();
    public DbSet<LicenseEntitlement> Licenses => Set<LicenseEntitlement>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<DeviceActivation> DeviceActivations => Set<DeviceActivation>();
    public DbSet<BotConfiguration> BotConfigurations => Set<BotConfiguration>();
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();
    public DbSet<AutomationAuditEvent> AutomationAuditEvents => Set<AutomationAuditEvent>();
    public DbSet<ServerPolicy> ServerPolicies => Set<ServerPolicy>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        model.Entity<CustomerAccount>(entity =>
        {
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.DisplayName).HasMaxLength(160);
            entity.HasIndex(x => x.Email);
        });

        model.Entity<LicenseEntitlement>(entity =>
        {
            entity.Property(x => x.KeyId).HasMaxLength(32);
            entity.Property(x => x.KeyDigest).HasMaxLength(64);
            entity.Property(x => x.DeviceTagDigest).HasMaxLength(64);
            entity.Property(x => x.Note).HasMaxLength(2000);
            entity.HasIndex(x => x.KeyId).IsUnique();
            entity.HasIndex(x => x.KeyDigest).IsUnique();
            entity.HasOne(x => x.Customer).WithMany(x => x.Licenses).HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Subscription>(entity =>
        {
            entity.Property(x => x.ExternalReference).HasMaxLength(200);
            entity.HasIndex(x => new { x.LicenseId, x.EndsAtUtc });
            entity.HasOne(x => x.License).WithMany(x => x.Subscriptions).HasForeignKey(x => x.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DeviceActivation>(entity =>
        {
            entity.Property(x => x.MachineDigest).HasMaxLength(64);
            entity.Property(x => x.DeviceName).HasMaxLength(200);
            entity.Property(x => x.TokenDigest).HasMaxLength(64);
            entity.Property(x => x.TokenPrefix).HasMaxLength(32);
            entity.HasIndex(x => x.TokenDigest).IsUnique();
            entity.HasIndex(x => new { x.LicenseId, x.MachineDigest }).IsUnique();
            entity.HasOne(x => x.License).WithMany(x => x.Activations).HasForeignKey(x => x.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<BotConfiguration>(entity =>
        {
            entity.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
            entity.HasIndex(x => x.DeviceActivationId).IsUnique();
            entity.HasOne(x => x.DeviceActivation).WithOne(x => x.Configuration).HasForeignKey<BotConfiguration>(x => x.DeviceActivationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<OrderRecord>(entity =>
        {
            entity.Property(x => x.ExternalOrderId).HasMaxLength(200);
            entity.Property(x => x.Currency).HasMaxLength(8);
            entity.Property(x => x.Status).HasMaxLength(64);
            entity.Property(x => x.Note).HasMaxLength(4000);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.HasIndex(x => x.ExternalOrderId).IsUnique();
            entity.HasOne(x => x.Customer).WithMany(x => x.Orders).HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(x => x.License).WithMany(x => x.Orders).HasForeignKey(x => x.LicenseId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<AutomationAuditEvent>(entity =>
        {
            entity.Property(x => x.Kind).HasMaxLength(120);
            entity.Property(x => x.RequestId).HasMaxLength(200);
            entity.Property(x => x.BuyerId).HasMaxLength(200);
            entity.Property(x => x.Detail).HasMaxLength(4000);
            entity.HasIndex(x => new { x.DeviceActivationId, x.OccurredAtUtc });
            entity.HasOne(x => x.DeviceActivation).WithMany(x => x.AuditEvents).HasForeignKey(x => x.DeviceActivationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ServerPolicy>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(32);
            entity.Property(x => x.MinimumClientVersion).HasMaxLength(64);
            entity.Property(x => x.Message).HasMaxLength(2000);
        });
    }
}
