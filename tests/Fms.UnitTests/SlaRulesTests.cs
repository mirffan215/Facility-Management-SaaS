using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Services;

namespace Fms.UnitTests;

public sealed class SlaRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero); // a Sunday
    private static readonly SlaTarget Target = new() { Priority = WorkPriority.High, ResponseMinutes = 60, ResolutionMinutes = 240 };

    private static WorkOrder Running(BusinessCalendar? calendar = null)
    {
        var wo = new WorkOrder { Status = WorkOrderStatus.Approved };
        SlaRules.Start(wo, Target, calendar ?? BusinessCalendar.AlwaysOpen, Guid.NewGuid(), null, T0);
        return wo;
    }

    [Fact]
    public void Clocks_start_at_authorisation_and_use_the_targets()
    {
        var wo = Running();
        Assert.Equal(T0.AddMinutes(60), wo.ResponseDueAt);
        Assert.Equal(T0.AddMinutes(240), wo.ResolutionDueAt);
        Assert.Equal(SlaState.OnTrack, SlaRules.StatusOf(wo, T0.AddMinutes(10)).State);
    }

    [Fact]
    public void A_work_order_with_no_policy_has_no_sla() =>
        Assert.Equal(SlaState.None, SlaRules.StatusOf(new WorkOrder(), T0).State);

    [Fact]
    public void Work_becomes_at_risk_at_three_quarters_of_the_window_and_breached_after_it()
    {
        var wo = Running();
        wo.RespondedAt = T0.AddMinutes(5);
        Assert.Equal(SlaState.OnTrack, SlaRules.StatusOf(wo, T0.AddMinutes(179)).State);
        Assert.Equal(SlaState.AtRisk, SlaRules.StatusOf(wo, T0.AddMinutes(180)).State);
        Assert.Equal(SlaState.AtRisk, SlaRules.StatusOf(wo, T0.AddMinutes(240)).State);
        Assert.Equal(SlaState.Breached, SlaRules.StatusOf(wo, T0.AddMinutes(241)).State);
    }

    [Fact]
    public void Missing_the_response_time_is_a_breach_even_if_resolution_is_still_far()
    {
        var wo = Running();
        var late = SlaRules.StatusOf(wo, T0.AddMinutes(61));
        Assert.Equal(SlaState.Breached, late.State);
        Assert.True(late.ResponseBreached);

        wo.RespondedAt = T0.AddMinutes(90); // responded, but late: still on record as breached
        Assert.True(SlaRules.StatusOf(wo, T0.AddMinutes(100)).ResponseBreached);
        wo.RespondedAt = T0.AddMinutes(30);
        Assert.False(SlaRules.StatusOf(wo, T0.AddMinutes(100)).ResponseBreached);
        Assert.True(SlaRules.StatusOf(wo, T0.AddMinutes(100)).ResponseMet);
    }

    [Fact]
    public void Finishing_in_time_is_met_and_finishing_late_is_breached_forever()
    {
        var wo = Running();
        wo.RespondedAt = T0.AddMinutes(5);
        wo.CompletedAt = T0.AddMinutes(200);
        wo.Status = WorkOrderStatus.Completed;
        Assert.Equal(SlaState.Met, SlaRules.StatusOf(wo, T0.AddDays(30)).State);
        wo.CompletedAt = T0.AddMinutes(300);
        Assert.Equal(SlaState.Breached, SlaRules.StatusOf(wo, T0.AddDays(30)).State);
    }

    [Fact]
    public void Cancelled_work_has_no_sla_state()
    {
        var wo = Running();
        wo.Status = WorkOrderStatus.Cancelled;
        Assert.Equal(SlaState.None, SlaRules.StatusOf(wo, T0.AddDays(5)).State);
    }

    [Fact]
    public void Time_on_hold_does_not_count()
    {
        var wo = Running();
        wo.RespondedAt = T0.AddMinutes(5);
        var hold = T0.AddMinutes(100); // 140 minutes of resolution time left

        SlaRules.Pause(wo, BusinessCalendar.AlwaysOpen, hold);
        Assert.Equal(140, wo.ResolutionRemainingMinutes);
        Assert.Equal(SlaState.Paused, SlaRules.StatusOf(wo, T0.AddDays(10)).State); // however long it waits

        var resume = T0.AddDays(2);
        SlaRules.Resume(wo, BusinessCalendar.AlwaysOpen, resume);
        Assert.Equal(resume.AddMinutes(140), wo.ResolutionDueAt);
        Assert.Null(wo.SlaPausedAt);
        Assert.Equal(SlaState.OnTrack, SlaRules.StatusOf(wo, resume.AddMinutes(1)).State);
    }

    [Fact]
    public void Pausing_twice_or_after_completion_changes_nothing()
    {
        var wo = Running();
        SlaRules.Pause(wo, BusinessCalendar.AlwaysOpen, T0.AddMinutes(10));
        var firstRemaining = wo.ResolutionRemainingMinutes;
        SlaRules.Pause(wo, BusinessCalendar.AlwaysOpen, T0.AddMinutes(50));
        Assert.Equal(firstRemaining, wo.ResolutionRemainingMinutes);

        var done = Running();
        done.CompletedAt = T0.AddMinutes(30);
        SlaRules.Pause(done, BusinessCalendar.AlwaysOpen, T0.AddMinutes(40));
        Assert.Null(done.SlaPausedAt);
    }

    [Fact]
    public void Holds_use_working_time_not_wall_time()
    {
        var week = new Dictionary<DayOfWeek, WorkingDay?>
        {
            [DayOfWeek.Sunday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)), [DayOfWeek.Monday] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(17)),
        };
        var calendar = new BusinessCalendar("UTC", week);
        var wo = Running(calendar); // due Sunday 12:00 (240 working minutes from 08:00)
        SlaRules.Pause(wo, calendar, T0.AddHours(2)); // Sunday 10:00: 120 working minutes left
        Assert.Equal(120, wo.ResolutionRemainingMinutes);
        SlaRules.Resume(wo, calendar, new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.Zero)); // Monday 16:00: 60 left that day, 60 more next working day
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero), wo.ResolutionDueAt); // next Sunday 09:00
    }

    // ---------------- who is told ----------------

    private static readonly Guid Requester = Guid.NewGuid(), Tech = Guid.NewGuid(), Boss = Guid.NewGuid(), Checker = Guid.NewGuid(), Approver = Guid.NewGuid();
    private static readonly NotificationPlanner.People People = new([Approver], [Boss], [Checker]);

    private static WorkOrder Order(WorkOrderStatus status, Guid? assignee = null) => new() { Status = status, RequestedBy = Requester, AssignedTo = assignee };

    [Fact]
    public void Approvers_hear_about_new_requests_that_need_approval_and_dispatchers_about_ones_that_do_not()
    {
        Assert.Equal([Approver], NotificationPlanner.Recipients(WorkOrderAction.Created, Order(WorkOrderStatus.Requested), Requester, People));
        Assert.Equal([Boss], NotificationPlanner.Recipients(WorkOrderAction.Created, Order(WorkOrderStatus.Approved), Requester, People));
    }

    [Fact]
    public void The_person_who_acted_is_never_told_about_their_own_action()
    {
        Assert.DoesNotContain(Boss, NotificationPlanner.Recipients(WorkOrderAction.Approved, Order(WorkOrderStatus.Approved), Boss, People));
        Assert.Equal([Tech], NotificationPlanner.Recipients(WorkOrderAction.Assigned, Order(WorkOrderStatus.Assigned, Tech), Requester, People));
        Assert.Empty(NotificationPlanner.Recipients(WorkOrderAction.Started, Order(WorkOrderStatus.InProgress, Tech), Requester, People));
    }

    [Theory]
    [InlineData(WorkOrderAction.Rejected)]
    [InlineData(WorkOrderAction.Closed)]
    [InlineData(WorkOrderAction.Started)]
    public void The_requester_follows_the_progress_of_their_request(WorkOrderAction action) =>
        Assert.Contains(Requester, NotificationPlanner.Recipients(action, Order(WorkOrderStatus.Approved, Tech), Boss, People));

    [Fact]
    public void Finished_work_goes_to_verifiers_and_edits_notify_nobody()
    {
        var done = NotificationPlanner.Recipients(WorkOrderAction.Completed, Order(WorkOrderStatus.Completed, Tech), Tech, People);
        Assert.Contains(Checker, done);
        Assert.Contains(Requester, done);
        Assert.Empty(NotificationPlanner.Recipients(WorkOrderAction.Edited, Order(WorkOrderStatus.Approved, Tech), Boss, People));
    }

    [Fact]
    public void Sla_alerts_go_to_dispatchers_and_the_assignee_once_each() =>
        Assert.Equal([Boss, Tech], NotificationPlanner.SlaRecipients(Order(WorkOrderStatus.Assigned, Tech), new NotificationPlanner.People([], [Boss, Boss], [])).OrderBy(g => g == Tech).ToArray());

    [Fact]
    public void Notices_are_bilingual_and_carry_a_link()
    {
        var wo = new WorkOrder { Number = 7, Title = "Leak", RequestedBy = Requester };
        var n = NoticeText.For(Boss, WorkOrderAction.Assigned, wo, "because");
        Assert.Equal("workorder.Assigned", n.Type);
        Assert.Equal($"/work-orders/{wo.Id}", n.LinkPath);
        Assert.Contains("WO-000007 was assigned", n.EmailSubject);
        Assert.Contains("تم إسناده", n.EmailBody);
        Assert.Contains("Reason: because", n.EmailBody);
        Assert.Contains("السبب: because", n.EmailBody);
        Assert.Contains("تجاوز مهلة الحل", NoticeText.ForSla(Boss, SlaAlert.ResolutionBreached, wo).EmailBody);
    }

    [Fact]
    public void Every_action_that_can_notify_has_words_in_both_languages()
    {
        var wo = new WorkOrder { Number = 1, Title = "x" };
        foreach (var action in new[] { WorkOrderAction.Created, WorkOrderAction.Approved, WorkOrderAction.Rejected, WorkOrderAction.Assigned, WorkOrderAction.Started, WorkOrderAction.Held,
                     WorkOrderAction.Resumed, WorkOrderAction.Completed, WorkOrderAction.Closed, WorkOrderAction.Reopened, WorkOrderAction.Cancelled })
        {
            var n = NoticeText.For(Boss, action, wo, null);
            Assert.DoesNotContain("was updated", n.EmailSubject);
            Assert.Matches("[؀-ۿ]", n.EmailBody);
        }
    }

    [Fact]
    public void The_new_permissions_are_in_the_catalog() => Assert.Contains(Permissions.WorkOrdersApprove, Permissions.All);
}
