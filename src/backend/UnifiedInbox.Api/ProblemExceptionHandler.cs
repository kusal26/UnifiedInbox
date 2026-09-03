using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace UnifiedInbox.Api;

public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken token)
    {
        var status = exception switch { ArgumentException => 400, UnauthorizedAccessException => 403, DbUpdateConcurrencyException => 409, DbUpdateException => 409, _ => 500 };
        if (status == 500) logger.LogError(exception, "Unhandled request failure {TraceId}", context.TraceIdentifier);
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = new ProblemDetails { Status = status, Title = status switch { 400 => "Invalid request", 403 => "Forbidden", 409 => "Conflicting update", _ => "Unexpected server error" }, Detail = status == 500 ? null : exception.Message, Extensions = { ["traceId"] = context.TraceIdentifier } }, Exception = exception });
    }
}
