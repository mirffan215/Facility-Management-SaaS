using Fms.BuildingBlocks.Security;
using Fms.Projects.Domain;
using Fms.Projects.Services;

namespace Fms.Api;

public static class ProjectsEndpoints
{
    public static void MapProjects(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects");

        g.MapGet("", async (ProjectService s, ProjectStatus? status, string? q, int? page, int? pageSize, CancellationToken ct) => Results.Ok(await s.ListAsync(status, q, page, pageSize, ct))).RequirePermission(Permissions.ProjectsRead);
        g.MapGet("/{id:guid}", async (Guid id, ProjectService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.ProjectsRead);
        g.MapGet("/{id:guid}/costs", async (Guid id, ProjectService s, CancellationToken ct) => (await s.CostsAsync(id, ct)).ToHttp()).RequirePermission(Permissions.ProjectsRead);
        g.MapPost("", async (SaveProjectRequest r, ProjectService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/projects/{x.Id}", x))).RequirePermission(Permissions.ProjectsManage);
        g.MapPut("/{id:guid}", async (Guid id, SaveProjectRequest r, ProjectService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/{id:guid}/status", async (Guid id, ChangeStatusRequest r, ProjectService s, CancellationToken ct) => (await s.ChangeStatusAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);

        g.MapPost("/{id:guid}/links", async (Guid id, LinkRequest r, ProjectService s, CancellationToken ct) => (await s.LinkAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/projects/{id}", x))).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/links/{linkId:guid}/unlink", async (Guid linkId, UnlinkRequest r, ProjectService s, CancellationToken ct) => (await s.UnlinkAsync(linkId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);

        g.MapPost("/{id:guid}/phases", async (Guid id, SavePhaseRequest r, PlanService s, CancellationToken ct) => (await s.AddPhaseAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/projects/{id}", x))).RequirePermission(Permissions.ProjectsManage);
        g.MapPut("/phases/{phaseId:guid}", async (Guid phaseId, SavePhaseRequest r, PlanService s, CancellationToken ct) => (await s.UpdatePhaseAsync(phaseId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/phases/{phaseId:guid}/delete", async (Guid phaseId, DeleteRequest r, PlanService s, CancellationToken ct) => (await s.DeletePhaseAsync(phaseId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);

        g.MapPost("/{id:guid}/tasks", async (Guid id, SaveTaskRequest r, PlanService s, CancellationToken ct) => (await s.AddTaskAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/projects/{id}", x))).RequirePermission(Permissions.ProjectsManage);
        g.MapPut("/tasks/{taskId:guid}", async (Guid taskId, SaveTaskRequest r, PlanService s, CancellationToken ct) => (await s.UpdateTaskAsync(taskId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/tasks/{taskId:guid}/delete", async (Guid taskId, DeleteRequest r, PlanService s, CancellationToken ct) => (await s.DeleteTaskAsync(taskId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);

        g.MapPost("/{id:guid}/milestones", async (Guid id, SaveMilestoneRequest r, PlanService s, CancellationToken ct) => (await s.AddMilestoneAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/projects/{id}", x))).RequirePermission(Permissions.ProjectsManage);
        g.MapPut("/milestones/{milestoneId:guid}", async (Guid milestoneId, SaveMilestoneRequest r, PlanService s, CancellationToken ct) => (await s.UpdateMilestoneAsync(milestoneId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/milestones/{milestoneId:guid}/achieve", async (Guid milestoneId, AchieveMilestoneRequest r, PlanService s, CancellationToken ct) => (await s.AchieveMilestoneAsync(milestoneId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/milestones/{milestoneId:guid}/delete", async (Guid milestoneId, DeleteRequest r, PlanService s, CancellationToken ct) => (await s.DeleteMilestoneAsync(milestoneId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);

        g.MapPost("/{id:guid}/budget", async (Guid id, SaveBudgetLineRequest r, PlanService s, CancellationToken ct) => (await s.AddBudgetAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/projects/{id}", x))).RequirePermission(Permissions.ProjectsManage);
        g.MapPut("/budget/{lineId:guid}", async (Guid lineId, SaveBudgetLineRequest r, PlanService s, CancellationToken ct) => (await s.UpdateBudgetAsync(lineId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
        g.MapPost("/budget/{lineId:guid}/delete", async (Guid lineId, DeleteRequest r, PlanService s, CancellationToken ct) => (await s.DeleteBudgetAsync(lineId, r, ct)).ToHttp()).RequirePermission(Permissions.ProjectsManage);
    }
}
