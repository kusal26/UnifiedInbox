using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Api.Security;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api;

public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger, IServiceProvider services) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        var unauthorized = exception is UnauthorizedAccessException;
        var (status, code) = exception switch
        {
            InboxException inbox => (inbox.StatusCode, inbox.Code),
            ArgumentException => (400, "invalid_request"),
            UnauthorizedAccessException => (403, "forbidden"),
            DbUpdateConcurrencyException => (409, "conflict"),
            DbUpdateException => (409, "conflict"),
            _ => (500, "internal_error")
        };
        if (status == 500) logger.LogError(exception, "Unhandled request failure {TraceId}", context.TraceIdentifier);
        // A membership-guard denial is an authorization failure, not an application error: audit it
        // in a fresh tenant scope because the request transaction that produced it was rolled back.
        if (unauthorized && AuthorizationAudit.TryActor(context, out var tenantId, out var userId))
        {
            await AuthorizationAudit.RecordAfterRollbackAsync(services, tenantId, userId,
                AuthorizationAudit.Route(context), context.Request.Method, AuthorizationAudit.PolicyName(context));
        }
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status switch { 400 => "Invalid request", 401 => "Unauthorized", 403 => "Forbidden", 409 => "Conflicting update", 503 => "Service unavailable", _ => "Unexpected server error" },
                Detail = status == 500 ? null : exception.Message,
                Type = $"https://unifiedinbox.app/problems/{code}",
                Extensions = { ["traceId"] = context.TraceIdentifier, ["code"] = code }
            },
            Exception = exception
        });
    }
}
