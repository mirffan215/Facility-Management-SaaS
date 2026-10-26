using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using A = Fms.Work.Domain.WorkOrderAction;
using S = Fms.Work.Domain.WorkOrderStatus;

namespace Fms.UnitTests;

public sealed class WorkOrderRulesTests
{
    private static readonly Guid Requester = Guid.NewGuid(), Tech = Guid.NewGuid(), Boss = Guid.NewGuid(), Checker = Guid.NewGuid();
    private static readonly WorkflowSettings Strict = new(true, true);
    private static readonly WorkflowSettings Relaxed = new(false, false);

    private static WorkOrder Order(S status, Guid? assignee = null, Guid? completedBy = null) =>
        new() { Status = status, RequestedBy = Requester, AssignedTo = assignee, CompletedBy = completedBy };

    private static Actor Person(Guid id, params string[] permissions) => new(id, permissions.ToHashSet());

    private static readonly Actor Requesting = Person(Requester, Permissions.WorkOrdersRequest);
    private static readonly Actor Technician = Person(Tech, Permissions.WorkOrdersRead, Permissions.WorkOrdersWork, Permissions.WorkOrdersRequest);
    private static readonly Actor Manager = Person(Boss, Permissions.All.Where(p => p.StartsWith("workorders.")).ToArray());
    private static readonly Actor Verifier = Person(Checker, Permissions.WorkOrdersRead, Permissions.WorkOrdersVerify);

    [Fact]
    public void Nobody_approves_their_own_request_even_with_every_permission()
    {
        var own = new WorkOrder { Status = S.Requested, RequestedBy = Boss };
        Assert.False(WorkOrderRules.Can(A.Approved, own, Manager, Strict));
        Assert.False(WorkOrderRules.Can(A.Rejected, own, Manager, Strict));
        Assert.True(WorkOrderRules.Can(A.Approved, Order(S.Requested), Manager, Strict));
    }

    [Fact]
    public void Whoever_did_the_work_cannot_verify_it()
    {
        var done = Order(S.Completed, assignee: Tech, completedBy: Tech);
        Assert.False(WorkOrderRules.Can(A.Verified, done, Person(Tech, Permissions.WorkOrdersVerify), Strict));
        Assert.False(WorkOrderRules.Can(A.Verified, Order(S.Completed, assignee: Boss, completedBy: Tech), Person(Boss, Permissions.WorkOrdersVerify), Strict)); // the assignee
        Assert.False(WorkOrderRules.Can(A.Verified, Order(S.Completed, assignee: Boss, completedBy: Tech), Person(Tech, Permissions.WorkOrdersVerify), Strict)); // who completed it
        Assert.True(WorkOrderRules.Can(A.Verified, done, Verifier, Strict));
        Assert.False(WorkOrderRules.Can(A.Verified, done, Technician, Strict)); // no verify permission
    }

    [Fact]
    public void Closing_needs_verification_unless_the_tenant_turned_it_off()
    {
        Assert.False(WorkOrderRules.Can(A.Closed, Order(S.Completed, Tech, Tech), Manager, Strict));
        Assert.True(WorkOrderRules.Can(A.Closed, Order(S.Completed, Tech, Tech), Manager, Relaxed));
        Assert.True(WorkOrderRules.Can(A.Closed, Order(S.Verified, Tech, Tech), Manager, Strict));
        Assert.True(WorkOrderRules.Can(A.Closed, Order(S.Verified, Tech, Tech), Verifier, Strict));
        Assert.False(WorkOrderRules.Can(A.Closed, Order(S.Verified, Tech, Tech), Technician, Strict));
    }

    [Fact]
    public void Only_the_assignee_or_a_manager_works_the_job()
    {
        var assigned = Order(S.Assigned, Tech);
        Assert.True(WorkOrderRules.Can(A.Started, assigned, Technician, Strict));
        Assert.True(WorkOrderRules.Can(A.Started, assigned, Manager, Strict));
        Assert.False(WorkOrderRules.Can(A.Started, Order(S.Assigned, Guid.NewGuid()), Technician, Strict));
        Assert.False(WorkOrderRules.Can(A.Started, assigned, Verifier, Strict));
        Assert.False(WorkOrderRules.Can(A.Started, Order(S.Approved), Manager, Strict)); // not assigned yet
    }

    [Theory]
    [InlineData(S.Requested, A.Approved)]
    [InlineData(S.Approved, A.Assigned)]
    [InlineData(S.Assigned, A.Started)]
    [InlineData(S.InProgress, A.Held)]
    [InlineData(S.InProgress, A.Completed)]
    [InlineData(S.OnHold, A.Resumed)]
    [InlineData(S.Completed, A.Verified)]
    [InlineData(S.Verified, A.Closed)]
    [InlineData(S.Closed, A.Reopened)]
    public void Each_step_of_the_lifecycle_is_open_to_a_manager(S from, A action) =>
        Assert.True(WorkOrderRules.Can(action, Order(from, Tech, Tech), Person(Boss, Permissions.All.Where(p => p.StartsWith("workorders.")).ToArray()), Strict),
            $"{action} from {from}");

    [Theory]
    [InlineData(S.Requested, A.Assigned)]
    [InlineData(S.Requested, A.Started)]
    [InlineData(S.Approved, A.Started)]
    [InlineData(S.Approved, A.Completed)]
    [InlineData(S.Assigned, A.Completed)]
    [InlineData(S.InProgress, A.Verified)]
    [InlineData(S.InProgress, A.Closed)]
    [InlineData(S.OnHold, A.Completed)]
    [InlineData(S.Completed, A.Started)]
    [InlineData(S.Cancelled, A.Approved)]
    public void Steps_cannot_be_skipped(S from, A action) =>
        Assert.False(WorkOrderRules.Can(action, Order(from, Tech, Tech), Manager, Strict), $"{action} from {from}");

    [Fact]
    public void Closed_and_cancelled_work_is_locked_except_for_a_managers_reopen_of_closed_work()
    {
        foreach (var status in new[] { S.Closed, S.Cancelled })
        {
            var all = WorkOrderRules.Allowed(Order(status, Tech), Manager, Strict);
            Assert.DoesNotContain(A.Edited, all);
            Assert.DoesNotContain(A.Assigned, all);
            Assert.DoesNotContain(A.Cancelled, all);
            Assert.Equal(status == S.Closed, all.Contains(A.Reopened));
        }

        Assert.DoesNotContain(A.Reopened, WorkOrderRules.Allowed(Order(S.Closed, Tech), Technician, Strict));
    }

    [Fact]
    public void A_requester_can_edit_and_cancel_only_their_own_request_while_it_waits()
    {
        Assert.True(WorkOrderRules.Can(A.Edited, Order(S.Requested), Requesting, Strict));
        Assert.True(WorkOrderRules.Can(A.Cancelled, Order(S.Requested), Requesting, Strict));
        Assert.False(WorkOrderRules.Can(A.Cancelled, Order(S.Approved), Requesting, Strict));
        Assert.False(WorkOrderRules.Can(A.Edited, new WorkOrder { Status = S.Requested, RequestedBy = Guid.NewGuid() }, Requesting, Strict));
        Assert.False(WorkOrderRules.Can(A.Approved, Order(S.Requested), Requesting, Strict));
    }

    [Fact]
    public void An_anonymous_caller_may_do_nothing() =>
        Assert.Empty(WorkOrderRules.Allowed(Order(S.Requested), new Actor(null, Permissions.All.ToHashSet()), Strict));

    [Theory]
    [InlineData(WorkOrderType.Emergency, true, S.Approved)]
    [InlineData(WorkOrderType.Corrective, true, S.Requested)]
    [InlineData(WorkOrderType.Corrective, false, S.Approved)]
    [InlineData(WorkOrderType.Planned, true, S.Requested)]
    public void New_requests_start_where_the_settings_say(WorkOrderType type, bool requireApproval, S expected) =>
        Assert.Equal(expected, WorkOrderRules.InitialStatus(type, new WorkflowSettings(requireApproval, true)));

    [Fact]
    public void Overdue_means_past_due_and_still_unfinished()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(WorkOrderRules.IsOverdue(new WorkOrder { Status = S.InProgress, DueAt = now.AddHours(-1) }, now));
        Assert.False(WorkOrderRules.IsOverdue(new WorkOrder { Status = S.InProgress, DueAt = now.AddHours(1) }, now));
        Assert.False(WorkOrderRules.IsOverdue(new WorkOrder { Status = S.Completed, DueAt = now.AddHours(-1) }, now));
        Assert.False(WorkOrderRules.IsOverdue(new WorkOrder { Status = S.Closed, DueAt = now.AddHours(-1) }, now));
        Assert.False(WorkOrderRules.IsOverdue(new WorkOrder { Status = S.Assigned, DueAt = null }, now));
    }

    [Fact]
    public void Numbers_display_with_a_prefix_and_padding() => Assert.Equal("WO-000042", WorkOrderRules.DisplayNumber(42));

    [Fact]
    public void Reasons_are_required_where_the_record_needs_an_explanation()
    {
        foreach (var a in new[] { A.Rejected, A.Held, A.Reopened, A.Cancelled }) Assert.True(WorkOrderRules.RequiresReason(a));
        foreach (var a in new[] { A.Approved, A.Started, A.Resumed, A.Verified, A.Closed, A.Assigned }) Assert.False(WorkOrderRules.RequiresReason(a));
    }

    [Fact]
    public void System_roles_only_use_permissions_in_the_catalog() =>
        Assert.All(Fms.Platform.Security.SystemRoles.Definitions.Values, role => Assert.All(role, p => Assert.Contains(p, Permissions.All)));
}
