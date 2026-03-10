using Microsoft.EntityFrameworkCore;

namespace DistributedJobSystem.Api;

public class JobDbContext : DbContext
{
    public JobDbContext(DbContextOptions<JobDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id).HasColumnName("id");
            entity.Property(j => j.Type).HasColumnName("type").HasMaxLength(128);
            entity.Property(j => j.Status).HasColumnName("status").HasMaxLength(64);
            entity.Property(j => j.CreatedAt).HasColumnName("created_at");
            entity.Property(j => j.StartedAt).HasColumnName("started_at");
            entity.Property(j => j.FinishedAt).HasColumnName("finished_at");
            entity.Property(j => j.RetryCount).HasColumnName("retry_count");
            entity.Property(j => j.Result).HasColumnName("result");
            entity.Property(j => j.ErrorMessage).HasColumnName("error_message");
        });
    }
}

public class Job
{
    public string Id { get; set; } = default!;
    public string Type { get; set; } = default!;
    public string Status { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int RetryCount { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
}
