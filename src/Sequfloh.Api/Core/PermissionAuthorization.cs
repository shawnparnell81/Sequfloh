using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sequfloh.Application.Security;

namespace Sequfloh.Api.Auth;

/// <summary>Use [RequirePermission("parts.edit")] on endpoints. Checked on the server against the database on every request.</summary>
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute(PermissionPolicyProvider.Prefix + permission);

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..]))
            .Build();
    }
}

public sealed class PermissionHandler(IAccessService access) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) &&
            await access.HasPermissionAsync(userId, requirement.Permission))
            context.Succeed(requirement);
    }
}
