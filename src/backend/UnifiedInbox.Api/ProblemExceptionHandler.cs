using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UnifiedInbox.Application;

namespace UnifiedInbox.Api;

public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
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
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = new ProblemDetails { Status = status, Title = status switch { 400 => "Invalid request", 401 => "Unauthorized", 403 => "Forbidden", 409 => "Conflicting update", _ => "Unexpected server error" }, Detail = status == 500 ? null : exception.Message, Extensions = { ["traceId"] = context.TraceIdentifier, ["code"] = code } }, Exception = exception });
    }
}
