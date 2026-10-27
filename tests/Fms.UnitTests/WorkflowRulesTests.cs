using Fms.Work.Domain;

namespace Fms.UnitTests;

public sealed class WorkflowRulesTests
{
    [Fact]
    public void A_priority_set_packs_into_one_number_and_back()
    {
        var mask = PriorityMask.Of([WorkPriority.Low, WorkPriority.Urgent]);
        Assert.True(PriorityMask.Has(mask, WorkPriority.Low));
        Assert.True(PriorityMask.Has(mask, WorkPriority.Urgent));
        Assert.False(PriorityMask.Has(mask, WorkPriority.High));
        Assert.Equal([WorkPriority.Low, WorkPriority.Urgent], PriorityMask.ToList(mask));
        Assert.Equal(0, PriorityMask.Of(null));
        Assert.Equal(0, PriorityMask.Of([]));
        Assert.Equal(PriorityMask.All, PriorityMask.Of(Enum.GetValues<WorkPriority>()));
        Assert.Equal(0, PriorityMask.Of([(WorkPriority)99])); // an unknown value sets nothing
    }

    [Theory]
    [InlineData(WorkOrderType.Corrective, WorkPriority.Medium, true, 0, true)]
    [InlineData(WorkOrderType.Corrective, WorkPriority.Low, true, 2, false)] // Low waived (bit 1)
    [InlineData(WorkOrderType.Corrective, WorkPriority.High, true, 2, true)] // others still wait
    [InlineData(WorkOrderType.Emergency, WorkPriority.Urgent, true, 0, false)] // emergencies never wait
    [InlineData(WorkOrderType.Corrective, WorkPriority.Urgent, false, 0, false)] // approval off
    [InlineData(WorkOrderType.Planned, WorkPriority.Medium, true, 0, true)]
    public void A_request_waits_for_approval_only_when_the_rules_say_so(WorkOrderType type, WorkPriority priority, bool require, int waived, bool waits)
    {
        var settings = new WorkflowSettings(RequireApproval: require, WaivedApprovalPriorities: waived);
        Assert.Equal(waits, settings.NeedsApproval(type, priority));
        Assert.Equal(waits ? WorkOrderStatus.Requested : WorkOrderStatus.Approved, WorkOrderRules.InitialStatus(type, settings, priority));
    }

    [Theory]
    [InlineData(true, 0, WorkPriority.Low, true)] // none chosen means all
    [InlineData(true, 12, WorkPriority.High, true)] // bits 2 and 3: Medium and High
    [InlineData(true, 12, WorkPriority.Low, false)]
    [InlineData(false, 12, WorkPriority.High, false)] // verification off wins
    [InlineData(false, 0, WorkPriority.Urgent, false)]
    public void Verification_is_needed_only_for_the_chosen_priorities(bool require, int mask, WorkPriority priority, bool needed) =>
        Assert.Equal(needed, new WorkflowSettings(RequireVerification: require, VerificationPriorities: mask).NeedsVerification(priority));

    private static WorkOrder Completed(WorkPriority priority, Guid worker) =>
        new() { Status = WorkOrderStatus.Completed, Priority = priority, AssignedTo = worker, CompletedBy = worker, RequestedBy = Guid.NewGuid() };

    [Fact]
    public void Closing_needs_verification_first_only_for_priorities_that_require_it()
    {
        var manager = new Actor(Guid.NewGuid(), new HashSet<string> { Fms.BuildingBlocks.Security.Permissions.WorkOrdersManage });
        var settings = new WorkflowSettings(RequireVerification: true, VerificationPriorities: PriorityMask.Of([WorkPriority.High, WorkPriority.Urgent]));
        Assert.False(WorkOrderRules.Can(WorkOrderAction.Closed, Completed(WorkPriority.High, Guid.NewGuid()), manager, settings)); // High must be verified first
        Assert.True(WorkOrderRules.Can(WorkOrderAction.Closed, Completed(WorkPriority.Low, Guid.NewGuid()), manager, settings)); // Low may be closed straight away
        var verified = Completed(WorkPriority.High, Guid.NewGuid());
        verified.Status = WorkOrderStatus.Verified;
        Assert.True(WorkOrderRules.Can(WorkOrderAction.Closed, verified, manager, settings));
    }

    [Fact]
    public void The_fixed_safety_rules_hold_whatever_the_settings()
    {
        var me = Guid.NewGuid();
        var everything = new HashSet<string>(Fms.BuildingBlocks.Security.Permissions.All);
        var actor = new Actor(me, everything);
        var loose = new WorkflowSettings(RequireApproval: false, RequireVerification: false, WaivedApprovalPriorities: PriorityMask.All);

        // Nobody approves their own request.
        var own = new WorkOrder { Status = WorkOrderStatus.Requested, RequestedBy = me };
        Assert.False(WorkOrderRules.Can(WorkOrderAction.Approved, own, actor, loose));
        // Whoever did the work cannot verify it, even with every permission.
        var mine = new WorkOrder { Status = WorkOrderStatus.Completed, AssignedTo = me, CompletedBy = me, RequestedBy = Guid.NewGuid() };
        Assert.False(WorkOrderRules.Can(WorkOrderAction.Verified, mine, actor, loose));
        // Closed work stays locked except by reopening.
        var closed = new WorkOrder { Status = WorkOrderStatus.Closed, RequestedBy = Guid.NewGuid() };
        Assert.False(WorkOrderRules.Can(WorkOrderAction.Started, closed, actor, loose));
        Assert.False(WorkOrderRules.Can(WorkOrderAction.Edited, closed, actor, loose));
    }
}
