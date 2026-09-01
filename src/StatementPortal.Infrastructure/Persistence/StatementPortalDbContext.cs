using Microsoft.EntityFrameworkCore;
using StatementPortal.Domain.Entities;

namespace StatementPortal.Infrastructure.Persistence;

public sealed class StatementPortalDbContext : DbContext
{
    public StatementPortalDbContext(DbContextOptions<StatementPortalDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditRecord>(entity =>
        {
            entity.ToTable("AuditRecords");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();

            entity.Property(x => x.StaffId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StaffName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Branch).HasMaxLength(128);
            entity.Property(x => x.Module).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Activity).HasMaxLength(128).IsRequired();
            entity.Property(x => x.MaskedAccountNumber).HasMaxLength(32);
            entity.Property(x => x.FailureReason).HasMaxLength(1024);
            entity.Property(x => x.IpAddress).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);

            entity.HasIndex(x => x.RequestId);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.HasIndex(x => x.StaffId);
            entity.HasIndex(x => x.Module);
            entity.HasIndex(x => x.Status);
        });
    }
}
