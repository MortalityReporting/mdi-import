using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using MortalityReporting.ImportApi.Configuration;
using MortalityReporting.ImportApi.Models;
using MortalityReporting.ImportApi.Parsing;
using MortalityReporting.ImportApi.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ImportApiOptions options = builder.Configuration.GetSection(ImportApiOptions.SectionName).Get<ImportApiOptions>() ?? new();
options.ApplyEnvironmentOverrides();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ImportFileParser>();
builder.Services.AddSingleton<MdiFhirMapper>();
builder.Services.AddScoped<ImportService>();
builder.Services.AddHttpClient<FhirSubmissionClient>((serviceOptions, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(serviceOptions.GetRequiredService<ImportApiOptions>().FhirTimeoutSeconds);
});
builder.Services.AddScoped<IFhirSubmissionClient>(serviceProvider => serviceProvider.GetRequiredService<FhirSubmissionClient>());
builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = options.MaxFileSizeBytes);
string[] invalidCorsOrigins = options.CorsAllowedOrigins.Where(origin => NormalizeCorsOrigin(origin) is null).ToArray();
string[] corsAllowedOrigins = options.CorsAllowedOrigins
    .Select(NormalizeCorsOrigin)
    .Where(origin => origin is not null)
    .Select(origin => origin!)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
builder.Services.AddCors(cors => cors.AddPolicy("ImportApiCors", policy =>
{
    if (corsAllowedOrigins.Length > 0) policy.WithOrigins(corsAllowedOrigins);
    else if (options.CorsAllowedOrigins.Length == 0) policy.AllowAnyOrigin();
    policy.WithMethods(HttpMethods.Post).AllowAnyHeader();
}));

WebApplication app = builder.Build();
if (invalidCorsOrigins.Length > 0)
    app.Logger.LogWarning("Ignoring {Count} invalid CORS allowed origin configuration value(s).", invalidCorsOrigins.Length);
app.UseRouting();
app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

RouteHandlerBuilder importRoute = app.MapPost(options.IncomingPath, async (HttpRequest request, ImportService service, CancellationToken cancellationToken) =>
{
    if (!BasicAuthorization.IsValid(request, options))
    {
        request.HttpContext.Response.Headers.WWWAuthenticate = "Basic";
        return Results.Json(new { error = "Invalid API credentials." }, statusCode: StatusCodes.Status401Unauthorized);
    }
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Content-Type must be multipart/form-data." });

    IFormCollection form = await request.ReadFormAsync(cancellationToken);
    IFormFile? file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "A non-empty file attachment named 'file' is required." });
    if (file.Length > options.MaxFileSizeBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not ".csv" and not ".xlsx") return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    await using Stream stream = file.OpenReadStream();
    try
    {
        options.ValidateForSubmission();
        IReadOnlyList<ImportRecordResponse> response = await service.ProcessAsync(stream, file.FileName, cancellationToken);
        return response.Count == 0 ? Results.NoContent() : Results.Json(response, statusCode: StatusCodes.Status201Created);
    }
    catch (ImportParseException exception)
    {
        return Results.UnprocessableEntity(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
importRoute.WithTags("import").RequireCors("ImportApiCors");

app.Run();

static string? NormalizeCorsOrigin(string? origin)
{
    if (string.IsNullOrWhiteSpace(origin) ||
        !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out Uri? uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
        string.IsNullOrEmpty(uri.Host) ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        uri.AbsolutePath is not "" and not "/" ||
        !string.IsNullOrEmpty(uri.Query) ||
        !string.IsNullOrEmpty(uri.Fragment))
    {
        return null;
    }

    return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
}

public partial class Program;

internal static class BasicAuthorization
{
    public static bool IsValid(HttpRequest request, ImportApiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.IncomingUsername)) return true;
        if (string.IsNullOrWhiteSpace(options.IncomingPassword)) return false;
        string? header = request.Headers.Authorization.FirstOrDefault();
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
            int separator = decoded.IndexOf(':');
            if (separator < 1) return false;
            byte[] actualUsername = Encoding.UTF8.GetBytes(decoded[..separator]);
            byte[] actualPassword = Encoding.UTF8.GetBytes(decoded[(separator + 1)..]);
            return CryptographicOperations.FixedTimeEquals(actualUsername, Encoding.UTF8.GetBytes(options.IncomingUsername)) &&
                   CryptographicOperations.FixedTimeEquals(actualPassword, Encoding.UTF8.GetBytes(options.IncomingPassword));
        }
        catch (FormatException) { return false; }
    }
}
