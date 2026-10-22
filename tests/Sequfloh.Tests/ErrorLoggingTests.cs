using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sequfloh.Api.Infrastructure;

namespace Sequfloh.Tests;

/// <summary>Unexpected errors are logged with their type, message, inner errors and stack, so the host's log shows the cause.</summary>
public sealed class ErrorLoggingTests
{
    sealed class Capture : ILogger<ErrorHandler>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f) => Lines.Add(f(state, ex));
    }

    [Fact]
    public async Task Unhandled_error_log_line_carries_the_whole_exception()
    {
        var log = new Capture();
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var handler = new ErrorHandler(log, services.GetRequiredService<Microsoft.AspNetCore.Http.IProblemDetailsService>());
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "POST"; http.Request.Path = "/api/imported-files";
        Exception ex;
        try { throw new InvalidOperationException("outer problem", new FormatException("inner problem")); } catch (Exception e) { ex = e; }
        await handler.TryHandleAsync(http, ex, default);
        var line = Assert.Single(log.Lines);
        Assert.Contains("POST /api/imported-files", line);
        Assert.Contains("System.InvalidOperationException: outer problem", line);
        Assert.Contains("System.FormatException: inner problem", line);
        Assert.Contains("ErrorLoggingTests", line); // stack trace
        Assert.Equal(500, http.Response.StatusCode);
    }
}
