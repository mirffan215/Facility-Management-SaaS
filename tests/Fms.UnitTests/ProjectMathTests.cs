using Fms.Projects.Domain;

namespace Fms.UnitTests;

public sealed class ProjectMathTests
{
    [Theory]
    [InlineData(1000, 0, false, 0, 1000)]
    [InlineData(1000, 400, false, 400, 600)]
    [InlineData(1000, 1000, false, 1000, 0)]
    [InlineData(1000, 1200, false, 1000, 0)]
    [InlineData(1000, 400, true, 0, 0)]
    public void An_order_is_spent_as_it_arrives_and_committed_for_the_rest_and_a_cancelled_one_is_neither(double ordered, double received, bool cancelled, double spent, double committed)
    {
        var (s, c) = ProjectMath.ForOrder((decimal)ordered, (decimal)received, cancelled);
        Assert.Equal((decimal)spent, s);
        Assert.Equal((decimal)committed, c);
    }

    [Fact]
    public void Rows_combine_budget_and_usage_per_category_including_spend_with_no_budget()
    {
        var rows = ProjectMath.Rows(
            [(BudgetCategory.Construction, 800m), (BudgetCategory.Construction, 200m), (BudgetCategory.Design, 100m)],
            [(BudgetCategory.Construction, 300m, 50m), (BudgetCategory.Equipment, 70m, 0m), (BudgetCategory.Construction, 100m, 0m)]);
        Assert.Equal([BudgetCategory.Design, BudgetCategory.Construction, BudgetCategory.Equipment], rows.Select(r => r.Category));
        var c = rows.Single(r => r.Category == BudgetCategory.Construction);
        Assert.Equal((1000m, 400m, 50m, 550m), (c.Budget, c.Spent, c.Committed, c.Remaining));
        Assert.Equal(-70m, rows.Single(r => r.Category == BudgetCategory.Equipment).Remaining); // money spent where nothing was budgeted shows as over
        Assert.Equal(100m, rows.Single(r => r.Category == BudgetCategory.Design).Remaining);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 3, 67)]
    [InlineData(3, 3, 100)]
    public void Progress_is_the_share_of_tasks_done(int done, int total, int expected) => Assert.Equal(expected, ProjectMath.PercentDone(done, total));

    [Theory]
    [InlineData(ProjectStatus.Planning, new[] { ProjectStatus.Active, ProjectStatus.Cancelled })]
    [InlineData(ProjectStatus.Active, new[] { ProjectStatus.OnHold, ProjectStatus.Completed, ProjectStatus.Cancelled })]
    [InlineData(ProjectStatus.OnHold, new[] { ProjectStatus.Active, ProjectStatus.Cancelled })]
    [InlineData(ProjectStatus.Completed, new ProjectStatus[0])]
    [InlineData(ProjectStatus.Cancelled, new ProjectStatus[0])]
    public void The_lifecycle_only_moves_forward_and_closed_projects_stay_closed(ProjectStatus from, ProjectStatus[] expected) =>
        Assert.Equal(expected, ProjectMath.NextStatuses(from));
}
