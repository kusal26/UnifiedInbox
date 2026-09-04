using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace UnifiedInbox.Api.Security;

/// <summary>
/// ASP.NET authorization-result handler: when a policy denies an authenticated caller it records an
/// authorization audit row and returns a consistent RFC 7807 <c>forbidden</c> problem (with a stable
/// code and trace id). Authorized and challenged requests fall through to the default handler.
/// </summary>
public sealed class AuthorizationAuditHandler(IProblemDetailsService problems) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        // Resolve and cache the named policy while the endpoint is still selected, so membership
        // denials thrown later by services can still be audited with the correct policy name.
        AuthorizationAudit.PolicyName(context);

        if (!authorizeResult.Forbidden)
        {
            await fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        if (AuthorizationAudit.TryActor(context, out var tenantId, out var userId))
        {
            await AuthorizationAudit.RecordInRequestScopeAsync(context, tenantId, userId,
                AuthorizationAudit.Route(context), context.Request.Method, AuthorizationAudit.PolicyName(context));
        }

        const int status = StatusCodes.Status403Forbidden;
        context.Response.StatusCode = status;
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = "Forbidden",
                Detail = "You are not permitted to perform this action.",
                Type = "https://unifiedinbox.app/problems/forbidden",
                Extensions = { ["traceId"] = context.TraceIdentifier, ["code"] = "forbidden" },
            },
        });
    }
}
