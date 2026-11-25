using ConvertApi.Core.Jobs;

namespace ConvertApi.UnitTests.Jobs;

public sealed class ConversionJobTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.Parse("7f9c2a51-3d4b-4e6f-8a1b-2c3d4e5f6a7b");
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private const string SourceKey = "uploads/0190f3b2c8a47c1e9d2b3a4f5e6d7c8b";

    private static ConversionJob NewJob() => ConversionJob.Create(Owner, SourceKey, "mp4", "mp3", T0);

    private static ConversionJob SucceededJob()
    {
        var job = NewJob();
        job.Start(T0);
        job.Succeed(Retention, T0.AddSeconds(30));
        return job;
    }

    [Fact]
    public void Create_QueuesTheJob()
    {
        var job = NewJob();

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(Owner, job.OwnerId);
        Assert.Equal("mp4", job.SourceFormat);
        Assert.Equal("mp3", job.TargetFormat);
        Assert.Equal(SourceKey, job.SourceFileKey);
        Assert.Equal(T0, job.CreatedAt);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.StartedAt);
        Assert.Null(job.CompletedAt);
        Assert.Null(job.ExpiresAt);
        Assert.Null(job.FailureReason);
        Assert.Empty(job.Transitions);
        Assert.Null(job.CallbackUrl);
    }

    [Fact]
    public void Create_KeepsASafeCallbackUrl()
    {
        var callback = new Uri("https://example.com/hook");

        var job = ConversionJob.Create(Owner, SourceKey, "mp4", "mp3", T0, callback);

        Assert.Equal(callback, job.CallbackUrl);
    }

    [Fact]
    public void Create_RefusesAnUnsafeCallbackUrl()
    {
        Assert.Throws<ArgumentException>(
            () => ConversionJob.Create(Owner, SourceKey, "mp4", "mp3", T0, new Uri("https://10.0.0.1/hook")));
    }

    [Fact]
    public void TimesAreStoredInUtc_WhateverOffsetTheCallerUses()
    {
        var paris = T0.ToOffset(TimeSpan.FromHours(1));

        var job = ConversionJob.Create(Owner, SourceKey, "mp4", "mp3", paris);
        job.Start(paris);

        Assert.Equal(TimeSpan.Zero, job.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, job.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, job.Transitions[0].At.Offset);
        Assert.Equal(T0, job.CreatedAt);
    }

    [Fact]
    public void Create_UsesAVersion7IdStampedWithTheCreationTime()
    {
        var job = NewJob();
        var later = ConversionJob.Create(Owner, SourceKey, "mp4", "mp3", T0.AddMilliseconds(1));

        Assert.Equal(7, job.Id.Version);
        Assert.True(job.Id.CompareTo(later.Id) < 0);
    }

    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData(".MP4", "mp4")]
    [InlineData("  Mp4 ", "mp4")]
    public void Create_NormalizesFormats(string input, string expected)
    {
        var job = ConversionJob.Create(Owner, SourceKey, input, "mp3", T0);

        Assert.Equal(expected, job.SourceFormat);
    }

    [Fact]
    public void Create_AllowsTheSameSourceAndTarget_ForReencodingOrResizing()
    {
        var job = ConversionJob.Create(Owner, SourceKey, "png", "png", T0);

        Assert.Equal(job.SourceFormat, job.TargetFormat);
    }

    [Fact]
    public void Create_RejectsAnEmptyOwner()
    {
        Assert.Throws<ArgumentException>(() => ConversionJob.Create(Guid.Empty, SourceKey, "mp4", "mp3", T0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsABlankSourceFileKey(string key)
    {
        Assert.Throws<ArgumentException>(() => ConversionJob.Create(Owner, key, "mp4", "mp3", T0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..mp4")]
    [InlineData("mp 4")]
    [InlineData("a/b")]
    [InlineData("../etc")]
    [InlineData("abcdefghijklmnopq")]
    public void Create_RejectsAnInvalidFormat(string format)
    {
        Assert.Throws<ArgumentException>(() => ConversionJob.Create(Owner, SourceKey, format, "mp3", T0));
        Assert.Throws<ArgumentException>(() => ConversionJob.Create(Owner, SourceKey, "mp4", format, T0));
    }

    [Fact]
    public void Create_AcceptsAFormatOfTheMaximumLength()
    {
        var job = ConversionJob.Create(Owner, SourceKey, "abcdefghijklmnop", "mp3", T0);

        Assert.Equal(16, job.SourceFormat.Length);
    }

    [Fact]
    public void Start_MovesTheJobToProcessing()
    {
        var job = NewJob();

        job.Start(T0.AddSeconds(5));

        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(T0.AddSeconds(5), job.StartedAt);
    }

    [Fact]
    public void Start_AgainAfterAWorkerCrash_CountsANewAttempt()
    {
        var job = NewJob();
        job.Start(T0);

        job.Start(T0.AddMinutes(2));

        Assert.Equal(JobStatus.Processing, job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Equal(T0.AddMinutes(2), job.StartedAt);
    }

    [Fact]
    public void Succeed_CompletesTheJobAndSetsItsExpiry()
    {
        var job = SucceededJob();

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(T0.AddSeconds(30), job.CompletedAt);
        Assert.Equal(T0.AddSeconds(30) + Retention, job.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Succeed_RejectsANonPositiveRetention(int hours)
    {
        var job = NewJob();
        job.Start(T0);

        Assert.Throws<ArgumentOutOfRangeException>(() => job.Succeed(TimeSpan.FromHours(hours), T0));
        Assert.Equal(JobStatus.Processing, job.Status);
    }

    [Fact]
    public void Succeed_BeforeStarting_IsRejected()
    {
        var job = NewJob();

        var error = Assert.Throws<InvalidJobTransitionException>(() => job.Succeed(Retention, T0));

        Assert.Equal(job.Id, error.JobId);
        Assert.Equal(JobStatus.Queued, error.From);
        Assert.Equal(JobStatus.Succeeded, error.To);
    }

    [Fact]
    public void ADuplicateCompletion_LeavesTheFirstResultUntouched()
    {
        var job = SucceededJob();

        Assert.Throws<InvalidJobTransitionException>(() => job.Succeed(Retention, T0.AddMinutes(5)));
        Assert.Throws<InvalidJobTransitionException>(() => job.Fail("late failure", T0.AddMinutes(5)));

        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(T0.AddSeconds(30), job.CompletedAt);
        Assert.Equal(T0.AddSeconds(30) + Retention, job.ExpiresAt);
        Assert.Null(job.FailureReason);
    }

    [Fact]
    public void Start_AfterSuccess_IsRejected_SoADuplicateMessageDoesNotRunTwice()
    {
        var job = SucceededJob();

        Assert.Throws<InvalidJobTransitionException>(() => job.Start(T0.AddMinutes(5)));
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public void Fail_RecordsTheReasonAndKeepsTheAttemptHistory()
    {
        var job = NewJob();
        job.Start(T0);

        job.Fail("ffmpeg exited with code 1", T0.AddSeconds(10));

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("ffmpeg exited with code 1", job.FailureReason);
        Assert.Equal(T0.AddSeconds(10), job.CompletedAt);
        Assert.Equal(T0, job.StartedAt);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public void Fail_WhileStillQueued_IsAllowedForStuckJobs()
    {
        var job = NewJob();

        job.Fail("timed out in the queue", T0);

        Assert.Equal(JobStatus.Failed, job.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fail_RejectsABlankReason(string reason)
    {
        var job = NewJob();

        Assert.Throws<ArgumentException>(() => job.Fail(reason, T0));
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public void Cancel_StopsAQueuedJob()
    {
        var job = NewJob();

        job.Cancel(T0);

        Assert.Equal(JobStatus.Canceled, job.Status);
        Assert.Equal(T0, job.CompletedAt);
    }

    [Fact]
    public void Cancel_StopsARunningJob()
    {
        var job = NewJob();
        job.Start(T0);

        job.Cancel(T0);

        Assert.Equal(JobStatus.Canceled, job.Status);
    }

    [Fact]
    public void Cancel_AfterSuccess_IsRejected()
    {
        var job = SucceededJob();

        Assert.Throws<InvalidJobTransitionException>(() => job.Cancel(T0));
    }

    [Fact]
    public void Expire_RemovesASucceededResultAtExpiry()
    {
        var job = SucceededJob();

        job.Expire(job.ExpiresAt!.Value);

        Assert.Equal(JobStatus.Expired, job.Status);
        Assert.True(job.IsTerminal);
    }

    [Fact]
    public void Expire_BeforeTheExpiryDate_IsAllowedForADeletionRequest()
    {
        var job = SucceededJob();

        job.Expire(T0.AddMinutes(1));

        Assert.Equal(JobStatus.Expired, job.Status);
    }

    [Fact]
    public void Expire_WithoutAResult_IsRejected()
    {
        var queued = NewJob();
        var running = NewJob();
        running.Start(T0);
        var failed = NewJob();
        failed.Fail("boom", T0);
        var canceled = NewJob();
        canceled.Cancel(T0);

        foreach (var job in new[] { queued, running, failed, canceled })
        {
            Assert.Throws<InvalidJobTransitionException>(() => job.Expire(T0));
        }
    }

    [Fact]
    public void ARejectedTransition_LeavesTheJobUnchanged()
    {
        var job = NewJob();
        job.Cancel(T0);

        Assert.Throws<InvalidJobTransitionException>(() => job.Start(T0.AddMinutes(1)));

        Assert.Equal(JobStatus.Canceled, job.Status);
        Assert.Equal(0, job.Attempts);
        Assert.Null(job.StartedAt);
        Assert.Equal(T0, job.CompletedAt);
        Assert.Single(job.Transitions);
    }

    [Fact]
    public void Transitions_RecordEveryStatusChangeInOrder()
    {
        var job = NewJob();
        job.Start(T0.AddSeconds(1));
        job.Start(T0.AddSeconds(2));
        job.Succeed(Retention, T0.AddSeconds(3));

        Assert.Equal(
            [
                new JobTransition(JobStatus.Queued, JobStatus.Processing, T0.AddSeconds(1)),
                new JobTransition(JobStatus.Processing, JobStatus.Processing, T0.AddSeconds(2)),
                new JobTransition(JobStatus.Processing, JobStatus.Succeeded, T0.AddSeconds(3)),
            ],
            job.Transitions);
    }
}
