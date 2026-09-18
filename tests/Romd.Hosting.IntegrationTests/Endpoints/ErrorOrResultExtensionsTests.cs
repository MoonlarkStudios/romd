using System.Diagnostics;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ErrorOrResultExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorType.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Failure, StatusCodes.Status500InternalServerError)]
    [InlineData(ErrorType.Unexpected, StatusCodes.Status500InternalServerError)]
    public void ToProblem_ErrorType_MapsToEnvelopeStatusAndErrorCode(ErrorType errorType, int expectedStatus)
    {
        const string code = "Test.Code";
        const string description = "Something happened.";

        var result = new[] { CreateError(errorType, code, description) }.ToProblem();

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(expectedStatus);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(expectedStatus);
        problem.Detail.ShouldBe(description);
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe(code);
    }

    [Fact]
    public void ToProblem_NonValidationError_DoesNotSmuggleErrorCodeIntoType()
    {
        var result = new[] { Error.NotFound("Title.NotFound", "Title not found.") }.ToProblem();

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Type.ShouldNotBe("Title.NotFound");
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Title.NotFound");
        problem.Extensions.ShouldNotContainKey(ProblemResults.ErrorsExtensionName);
    }

    [Fact]
    public void ToProblem_SingleValidationError_IncludesErrorsMap()
    {
        var result = new[] { Error.Validation("Command.InvalidName", "Name cannot be empty.") }.ToProblem();

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(StatusCodes.Status400BadRequest);

        var errors = problem.Extensions[ProblemResults.ErrorsExtensionName]
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>();
        errors.ShouldNotBeNull();
        errors["Command.InvalidName"].ShouldBe(new[] { "Name cannot be empty." });
    }

    [Fact]
    public void ToProblem_MultipleValidationErrors_PreservesAllFailures()
    {
        var result = new[]
        {
            Error.Validation("Command.InvalidName", "Name cannot be empty."),
            Error.Validation("Command.InvalidName", "Name is too long."),
            Error.Validation("Command.InvalidPlatformId", "Platform ID must be positive.")
        }.ToProblem();

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(StatusCodes.Status400BadRequest);
        problem.Detail.ShouldBe("Name cannot be empty.");
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("Command.InvalidName");

        var errors = problem.Extensions[ProblemResults.ErrorsExtensionName]
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, string[]>>();
        errors.ShouldNotBeNull();
        errors.Count.ShouldBe(2);
        errors["Command.InvalidName"].ShouldBe(new[] { "Name cannot be empty.", "Name is too long." });
        errors["Command.InvalidPlatformId"].ShouldBe(new[] { "Platform ID must be positive." });
    }

    [Fact]
    public void ToProblem_EmptyErrors_ReturnsGeneralUnexpected500()
    {
        var result = Array.Empty<Error>().ToProblem();

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>()
            .StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);

        var problem = result.ShouldBeAssignableTo<IValueHttpResult<ProblemDetails>>().Value;
        problem.ShouldNotBeNull();
        problem.Detail.ShouldBe("An unexpected error occurred.");
        problem.Extensions[ProblemResults.ErrorCodeExtensionName].ShouldBe("General.Unexpected");
    }

    [Fact]
    public async Task ToProblem_Executed_WritesEnvelopeJsonWithTraceId()
    {
        var result = new[]
        {
            Error.Validation("Command.InvalidName", "Name cannot be empty."),
            Error.Validation("Command.InvalidPlatformId", "Platform ID must be positive.")
        }.ToProblem();

        using var body = await ExecuteAsync(result, out var expectedTraceId);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status400BadRequest);
        root.GetProperty("title").GetString().ShouldBe("Bad Request");
        root.GetProperty("detail").GetString().ShouldBe("Name cannot be empty.");
        root.GetProperty("errorCode").GetString().ShouldBe("Command.InvalidName");
        root.GetProperty("traceId").GetString().ShouldBe(expectedTraceId);

        var errors = root.GetProperty("errors");
        errors.GetProperty("Command.InvalidName")[0].GetString().ShouldBe("Name cannot be empty.");
        errors.GetProperty("Command.InvalidPlatformId")[0].GetString().ShouldBe("Platform ID must be positive.");
    }

    [Fact]
    public async Task ValidationProblem_Executed_WritesFieldKeyedErrorsMap()
    {
        var result = ProblemResults.ValidationProblem(
            "Catalog.InvalidQuery",
            "sortBy",
            "'bogus' is not a valid sort field.");

        using var body = await ExecuteAsync(result, out var expectedTraceId);
        var root = body.RootElement;

        root.GetProperty("status").GetInt32().ShouldBe(StatusCodes.Status400BadRequest);
        root.GetProperty("errorCode").GetString().ShouldBe("Catalog.InvalidQuery");
        root.GetProperty("traceId").GetString().ShouldBe(expectedTraceId);
        root.GetProperty("errors").GetProperty("sortBy")[0].GetString()
            .ShouldBe("'bogus' is not a valid sort field.");
    }

    private static Task<JsonDocument> ExecuteAsync(IResult result, out string expectedTraceId)
    {
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "test-trace-id",
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };

        expectedTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        return ExecuteCoreAsync(result, httpContext);
    }

    private static async Task<JsonDocument> ExecuteCoreAsync(IResult result, DefaultHttpContext httpContext)
    {
        await result.ExecuteAsync(httpContext);

        httpContext.Response.ContentType.ShouldStartWith("application/problem+json");
        httpContext.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(httpContext.Response.Body);
    }

    private static Error CreateError(ErrorType type, string code, string description) => type switch
    {
        ErrorType.Failure => Error.Failure(code, description),
        ErrorType.Unexpected => Error.Unexpected(code, description),
        ErrorType.Validation => Error.Validation(code, description),
        ErrorType.Conflict => Error.Conflict(code, description),
        ErrorType.NotFound => Error.NotFound(code, description),
        ErrorType.Unauthorized => Error.Unauthorized(code, description),
        ErrorType.Forbidden => Error.Forbidden(code, description),
        _ => Error.Unexpected(code, description)
    };
}
