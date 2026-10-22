using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sequfloh.Application.Abstractions;

namespace Sequfloh.Api.Infrastructure;

/// <summary>Maps known failures to problem details; unexpected errors never leak internals.</summary>
public sealed class ErrorHandler(ILogger<ErrorHandler> log, IProblemDetailsService problems) : IExceptionHandler
{
    /// <summary>Status for a request the browser abandoned (nginx's "client closed request"); nothing failed on the server.</summary>
    public const int ClientClosedRequest = 499;

    // the database driver can wrap the cancellation (for example in a DbUpdateException)
    static bool Cancelled(Exception? ex) { for (; ex is not null; ex = ex.InnerException) if (ex is OperationCanceledException) return true; return false; }

    static IEnumerable<Exception> Chain(Exception? e) { for (; e is not null; e = e.InnerException) yield return e; }

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        // The browser went away (page closed, request cancelled): not a server error, and there is no one left to send a body to.
        if (http.RequestAborted.IsCancellationRequested && Cancelled(ex))
        {
            log.LogInformation("Request {Method} {Path} was cancelled by the client", http.Request.Method, http.Request.Path);
            if (!http.Response.HasStarted) http.Response.StatusCode = ClientClosedRequest;
            return true;
        }
        var (status, title, errors) = ex switch
        {
            AppException a => (a.StatusCode, a.Message, null),
            DbUpdateConcurrencyException => (409, "This record was changed by someone else. Reload and try again.", null),
            _ => (500, "An unexpected error occurred.", (Dictionary<string, string[]>?)null)
        };
        if (status == 500)
        {
            var chain = string.Join(" <- ", Chain(ex).Select(e => $"{e.GetType().FullName}: {e.Message}"));
            // the full text (types, messages, inner exceptions, stack) goes into the message itself so hosts that keep only one line per entry still show it
            log.LogError("Unhandled error on {Method} {Path} (trace {TraceId}): {Chain}{NewLine}{Detail}", http.Request.Method, http.Request.Path, http.TraceIdentifier, chain, Environment.NewLine, ex.ToString());
        }
        else if (ex is Sequfloh.Imports.FileUnreadableException fu) log.LogWarning(fu.Cause, "Upload on {Path} could not be read (trace {TraceId})", http.Request.Path, http.TraceIdentifier);
        else if (status >= 500) log.LogWarning("{Path} failed with {Status} (trace {TraceId}): {Message}", http.Request.Path, status, http.TraceIdentifier, ex.Message);
        http.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title };
        pd.Extensions["traceId"] = http.TraceIdentifier;
        if (errors is not null) pd.Extensions["errors"] = errors;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd, Exception = ex });
    }
}
