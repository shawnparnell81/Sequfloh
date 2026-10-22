namespace Sequfloh.Application.Abstractions;

public class AppException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class NotFoundException(string what) : AppException($"{what} was not found.", 404);
public sealed class ForbiddenException(string message = "You do not have permission for this action.") : AppException(message, 403);
public sealed class ConflictException(string message) : AppException(message, 409);
