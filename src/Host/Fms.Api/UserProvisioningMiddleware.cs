using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Services;

namespace Fms.Api;

/// <summary>
/// After tenant resolution, makes sure the signed-in person has a user record in that tenant (created on first sign-in) and
/// refuses deactivated users. Runs only when the platform database is configured.
/// </summary>
public sealed class UserProvisioningMiddleware(RequestDelegate next)
{
    public const string UserIdItem = "fms.userId";

    public async Task InvokeAsync(HttpContext context, ITenantContext tenant)
    {
        var provisioner = context.RequestServices.GetService<IUserProvisioner>();
        var subject = context.User.FindFirst("sub")?.Value;
        var provider = context.User.FindFirst(FmsClaims.Provider)?.Value;

        if (provisioner is not null && tenant.TenantId is not null && subject is not null && provider is not null)
        {
            var email = context.User.FindFirst("email")?.Value ?? context.User.FindFirst("preferred_username")?.Value;
            var user = await provisioner.EnsureAsync(provider, subject, email, context.User.FindFirst("name")?.Value, context.RequestAborted);

            if (user.LicenseLimitReached)
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "User limit reached",
                    detail: "The license allows no more active users. Ask an administrator to deactivate someone or to raise the license.",
                    extensions: new Dictionary<string, object?> { ["code"] = "license.user_limit" }).ExecuteAsync(context);
                return;
            }

            if (!user.IsActive)
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Account deactivated").ExecuteAsync(context);
                return;
            }

            context.Items[UserIdItem] = user.UserId;
        }

        await next(context);
    }
}
