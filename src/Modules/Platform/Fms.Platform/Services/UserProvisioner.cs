using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

public sealed record SignedInUser(Guid UserId, bool IsActive);

public interface IUserProvisioner
{
    /// <summary>Finds the user for this sign-in in the current tenant, creating it on first sign-in. Must run inside a tenant scope.</summary>
    Task<SignedInUser> EnsureAsync(string provider, string subject, string? email, string? displayName, CancellationToken ct = default);
}

public sealed class UserProvisioner(PlatformDbContext db, TimeProvider clock) : IUserProvisioner
{
    private static readonly TimeSpan LastSignInGranularity = TimeSpan.FromMinutes(5);

    public async Task<SignedInUser> EnsureAsync(string provider, string subject, string? email, string? displayName, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Provider == provider && u.Subject == subject, ct);

        if (user is null)
        {
            user = new User { Provider = provider, Subject = subject, Email = Trim(email, 320), DisplayName = Trim(displayName, 200), CreatedAt = now, LastSignInAt = now };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two first requests raced; the unique index let one win. Use the winner.
                db.Entry(user).State = EntityState.Detached;
                user = await db.Users.AsNoTracking().FirstAsync(u => u.Provider == provider && u.Subject == subject, ct);
            }
        }
        else if (now - user.LastSignInAt > LastSignInGranularity)
        {
            user.LastSignInAt = now;
            await db.SaveChangesAsync(ct);
        }

        return new SignedInUser(user.Id, user.IsActive);
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
