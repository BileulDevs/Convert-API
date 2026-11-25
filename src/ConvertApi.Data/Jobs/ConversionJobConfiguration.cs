using ConvertApi.Core.Formats;
using ConvertApi.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ConvertApi.Data.Jobs;

internal sealed class ConversionJobConfiguration : IEntityTypeConfiguration<ConversionJob>
{
    private const int StatusLength = 16;

    public void Configure(EntityTypeBuilder<ConversionJob> job)
    {
        job.ToTable("Jobs");
        job.HasKey(j => j.Id);

        // Read-only properties are mapped explicitly: EF fills them through the constructor.
        job.Property(j => j.Id).ValueGeneratedNever();
        job.Property(j => j.OwnerId);
        job.Property(j => j.SourceFileKey).HasMaxLength(200);
        job.Property(j => j.SourceFormat).HasMaxLength(FormatId.MaxLength);
        job.Property(j => j.TargetFormat).HasMaxLength(FormatId.MaxLength);
        job.Property(j => j.CreatedAt);

        // Stored as text: reordering the enum can never reinterpret existing rows.
        job.Property(j => j.Status).HasConversion<string>().HasMaxLength(StatusLength);
        job.Property(j => j.FailureReason).HasMaxLength(2000);
        job.Property(j => j.CallbackUrl).HasMaxLength(Core.Webhooks.CallbackUrl.MaxLength);
        job.Ignore(j => j.IsTerminal);

        // EF needs an IList: map the private list behind the read-only Transitions property.
        job.Ignore(j => j.Transitions);
        job.ComplexCollection<List<JobTransition>, JobTransition>("_transitions", transition =>
        {
            transition.ToJson("Transitions");
            transition.Property(t => t.From).HasConversion<string>();
            transition.Property(t => t.To).HasConversion<string>();
        });

        // PostgreSQL's xmin system column: two workers saving the same job cannot both win.
        job.Property<uint>("Version").IsRowVersion();

        // Listing a key's jobs newest first, paged by id: version 7 ids sort by creation
        // time and never tie, so a cursor neither skips nor repeats rows.
        job.HasIndex(j => new { j.OwnerId, j.Id });

        // Workers claim among jobs that are waiting or running, oldest first. It also keeps the
        // per-key quota count small: only active rows, however many finished jobs pile up.
        job.HasIndex(j => j.Id)
            .HasDatabaseName("IX_Jobs_Active")
            .HasFilter($"\"Status\" IN ('{nameof(JobStatus.Queued)}', '{nameof(JobStatus.Processing)}')");

        // The expiry sweep only looks at results that are still kept.
        job.HasIndex(j => j.ExpiresAt).HasFilter($"\"Status\" = '{nameof(JobStatus.Succeeded)}'");
    }
}
