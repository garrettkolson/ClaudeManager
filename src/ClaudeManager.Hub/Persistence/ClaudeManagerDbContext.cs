using ClaudeManager.Hub.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClaudeManager.Hub.Persistence;

public class ClaudeManagerDbContext : DbContext
{
    public DbSet<MachineAgentEntity> MachineAgents { get; set; } = default!;
    public DbSet<ClaudeSessionEntity> ClaudeSessions { get; set; } = default!;
    public DbSet<StreamedLineEntity> StreamedLines { get; set; } = default!;
    public DbSet<WikiEntryEntity>  WikiEntries { get; set; } = default!;
    public DbSet<SweAfJobEntity>    SweAfJobs    { get; set; } = default!;
    public DbSet<SweAfConfigEntity> SweAfConfigs { get; set; } = default!;
    public DbSet<SweAfHostEntity>   SweAfHosts   { get; set; } = default!;
    public DbSet<GpuHostEntity>        GpuHosts       { get; set; } = default!;
    public DbSet<HubSecretEntity>      HubSecrets     { get; set; } = default!;
    public DbSet<LlmDeploymentEntity>  LlmDeployments { get; set; } = default!;

    public ClaudeManagerDbContext(DbContextOptions<ClaudeManagerDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<MachineAgentEntity>()
            .HasMany(a => a.Sessions)
            .WithOne(s => s.Machine)
            .HasForeignKey(s => s.MachineId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<ClaudeSessionEntity>()
            .HasMany(s => s.OutputLines)
            .WithOne(l => l.Session)
            .HasForeignKey(l => l.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<ClaudeSessionEntity>()
            .HasIndex(s => s.MachineId);

        mb.Entity<ClaudeSessionEntity>()
            .HasIndex(s => s.LastActivityAt);

        mb.Entity<ClaudeSessionEntity>()
            .HasIndex(s => s.Status);

        mb.Entity<StreamedLineEntity>()
            .HasIndex(l => l.SessionId);

        mb.Entity<GpuHostEntity>()
            .HasIndex(h => h.HostId)
            .IsUnique();

        mb.Entity<LlmDeploymentEntity>()
            .HasIndex(d => d.HostId);

        mb.Entity<LlmDeploymentEntity>()
            .HasIndex(d => d.Status);
    }
}
