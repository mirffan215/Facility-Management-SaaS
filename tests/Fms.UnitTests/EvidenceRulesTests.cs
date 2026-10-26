using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using S = Fms.Work.Domain.WorkOrderStatus;

namespace Fms.UnitTests;

public sealed class EvidenceRulesTests
{
    private static readonly Guid Requester = Guid.NewGuid(), Tech = Guid.NewGuid(), Boss = Guid.NewGuid(), Other = Guid.NewGuid();
    private static WorkOrder Order(S status) => new() { Status = status, RequestedBy = Requester, AssignedTo = Tech };
    private static Actor Person(Guid id, params string[] permissions) => new(id, permissions.ToHashSet());
    private static readonly Actor Technician = Person(Tech, Permissions.WorkOrdersWork, Permissions.WorkOrdersRead);
    private static readonly Actor Manager = Person(Boss, Permissions.WorkOrdersManage, Permissions.WorkOrdersRead);

    [Fact]
    public void Only_the_assignee_with_work_permission_or_a_manager_does_the_work()
    {
        Assert.True(EvidenceRules.DoesTheWork(Order(S.InProgress), Technician));
        Assert.True(EvidenceRules.DoesTheWork(Order(S.InProgress), Manager));
        Assert.False(EvidenceRules.DoesTheWork(Order(S.InProgress), Person(Other, Permissions.WorkOrdersWork))); // not the assignee
        Assert.False(EvidenceRules.DoesTheWork(Order(S.InProgress), Person(Tech, Permissions.WorkOrdersRead))); // the assignee, but no work permission
        Assert.False(EvidenceRules.DoesTheWork(Order(S.InProgress), new Actor(null, Permissions.All.ToHashSet())));
    }

    [Theory]
    [InlineData(S.InProgress, true)]
    [InlineData(S.OnHold, true)]
    [InlineData(S.Assigned, false)]
    [InlineData(S.Completed, false)]
    [InlineData(S.Closed, false)]
    public void The_checklist_is_filled_in_only_while_the_work_is_under_way(S status, bool open) => Assert.Equal(open, EvidenceRules.ChecklistOpen(status));

    [Theory]
    [InlineData(S.Approved, true)]
    [InlineData(S.Assigned, true)]
    [InlineData(S.InProgress, true)]
    [InlineData(S.Requested, false)]
    [InlineData(S.Completed, false)]
    public void A_checklist_can_be_added_until_the_work_is_finished(S status, bool open) => Assert.Equal(open, EvidenceRules.ChecklistAttachable(status));

    [Theory]
    [InlineData(S.InProgress, true)]
    [InlineData(S.OnHold, true)]
    [InlineData(S.Completed, true)]
    [InlineData(S.Verified, false)]
    [InlineData(S.Closed, false)]
    [InlineData(S.Cancelled, false)]
    [InlineData(S.Assigned, false)]
    public void Time_and_costs_close_once_the_work_has_been_checked(S status, bool open)
    {
        Assert.Equal(open, EvidenceRules.LedgerOpen(status));
        Assert.Equal(open, EvidenceRules.SignatureOpen(status));
    }

    [Fact]
    public void Files_can_be_added_by_the_worker_dispatchers_and_the_requester_while_the_order_is_open()
    {
        var wo = Order(S.Requested);
        Assert.True(EvidenceRules.MayAddFiles(wo, Person(Requester, Permissions.WorkOrdersRequest)));
        Assert.True(EvidenceRules.MayAddFiles(wo, Person(Other, Permissions.WorkOrdersAssign)));
        Assert.True(EvidenceRules.MayAddFiles(wo, Technician));
        Assert.False(EvidenceRules.MayAddFiles(wo, Person(Other, Permissions.WorkOrdersRequest))); // someone else's request
        Assert.False(EvidenceRules.MayAddFiles(wo, Person(Other, Permissions.WorkOrdersRead)));
        Assert.True(EvidenceRules.FilesOpen(S.Verified));
        Assert.False(EvidenceRules.FilesOpen(S.Closed));
        Assert.False(EvidenceRules.FilesOpen(S.Cancelled));
    }

    [Theory]
    [InlineData(2, 45.555, 91.11)]
    [InlineData(3, 0.335, 1.01)] // 1.005 rounds away from zero
    [InlineData(1, 0, 0)]
    [InlineData(0.5, 100, 50)]
    public void Cost_lines_round_to_the_cent_once_per_line(double quantity, double unitCost, double expected) =>
        Assert.Equal((decimal)expected, EvidenceRules.LineTotal(new WorkOrderCostLine { Quantity = (decimal)quantity, UnitCost = (decimal)unitCost }));

    [Theory]
    [InlineData(90, 80, 120)]
    [InlineData(45, 100, 75)]
    [InlineData(1, 100, 1.67)]
    [InlineData(60, 0, 0)]
    public void Labour_is_minutes_at_the_rate_frozen_on_the_entry(int minutes, double rate, double expected) =>
        Assert.Equal((decimal)expected, EvidenceRules.LabourCost(minutes, (decimal)rate));
}
