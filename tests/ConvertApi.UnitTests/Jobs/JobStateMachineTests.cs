using ConvertApi.Core.Jobs;

namespace ConvertApi.UnitTests.Jobs;

public sealed class JobStateMachineTests
{
    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Processing)]
    [InlineData(JobStatus.Queued, JobStatus.Canceled)]
    [InlineData(JobStatus.Queued, JobStatus.Failed)]
    [InlineData(JobStatus.Processing, JobStatus.Processing)]
    [InlineData(JobStatus.Processing, JobStatus.Succeeded)]
    [InlineData(JobStatus.Processing, JobStatus.Failed)]
    [InlineData(JobStatus.Processing, JobStatus.Canceled)]
    [InlineData(JobStatus.Succeeded, JobStatus.Expired)]
    public void CanTransition_AllowsTheLifecycleSteps(JobStatus from, JobStatus to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
    }

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Queued)]
    [InlineData(JobStatus.Queued, JobStatus.Succeeded)]
    [InlineData(JobStatus.Queued, JobStatus.Expired)]
    [InlineData(JobStatus.Processing, JobStatus.Queued)]
    [InlineData(JobStatus.Processing, JobStatus.Expired)]
    [InlineData(JobStatus.Succeeded, JobStatus.Processing)]
    [InlineData(JobStatus.Succeeded, JobStatus.Failed)]
    [InlineData(JobStatus.Succeeded, JobStatus.Canceled)]
    [InlineData(JobStatus.Failed, JobStatus.Processing)]
    [InlineData(JobStatus.Failed, JobStatus.Expired)]
    [InlineData(JobStatus.Canceled, JobStatus.Processing)]
    [InlineData(JobStatus.Expired, JobStatus.Succeeded)]
    public void CanTransition_RejectsEverythingElse(JobStatus from, JobStatus to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void IsTerminal_MatchesTheStatusesWithNoOutgoingTransition()
    {
        var statuses = Enum.GetValues<JobStatus>();

        foreach (var status in statuses)
        {
            var hasWayOut = statuses.Any(target => JobStateMachine.CanTransition(status, target));
            Assert.Equal(!hasWayOut, JobStateMachine.IsTerminal(status));
        }
    }

    [Fact]
    public void CanTransition_RejectsAnUndefinedStatus()
    {
        const JobStatus undefined = (JobStatus)99;

        Assert.False(JobStateMachine.CanTransition(undefined, JobStatus.Processing));
        Assert.False(JobStateMachine.CanTransition(JobStatus.Queued, undefined));
    }

    [Theory]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Canceled)]
    [InlineData(JobStatus.Expired)]
    public void IsTerminal_IsTrueWhenNoTransitionLeavesTheStatus(JobStatus status)
    {
        Assert.True(JobStateMachine.IsTerminal(status));
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Processing)]
    [InlineData(JobStatus.Succeeded)]
    public void IsTerminal_IsFalseWhileAJobCanStillMove(JobStatus status)
    {
        Assert.False(JobStateMachine.IsTerminal(status));
    }
}
