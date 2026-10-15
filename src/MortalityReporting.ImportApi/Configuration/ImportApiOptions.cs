namespace MortalityReporting.ImportApi.Configuration;

public sealed class ImportApiOptions
{
    public const string SectionName = "ImportApi";

    public string IncomingPath { get; set; } = "/import";
    public string IncomingUsername { get; set; } = "";
    public string IncomingPassword { get; set; } = "";
    public string FhirEndpoint { get; set; } = "";
    public string FhirFullUrlBase { get; set; } = "https://mdi.local/fhir";
    public string FhirUsername { get; set; } = "";
    public string FhirPassword { get; set; } = "";
    public long MaxFileSizeBytes { get; set; } = 50 * 1024 * 1024;
    public int FhirTimeoutSeconds { get; set; } = 30;
    public string[] CorsAllowedOrigins { get; set; } = Array.Empty<string>();

    public void ApplyEnvironmentOverrides()
    {
        IncomingPath = EnvironmentValue("INCOMING_API_PATH") ?? EnvironmentValue("IMPORT_API_PATH") ?? IncomingPath;
        IncomingUsername = EnvironmentValue("INCOMING_API_USERNAME") ?? IncomingUsername;
        IncomingPassword = EnvironmentValue("INCOMING_API_PASSWORD") ?? IncomingPassword;
        FhirEndpoint = EnvironmentValue("FHIR_API_ENDPOINT") ?? EnvironmentValue("FHIR_ENDPOINT") ?? FhirEndpoint;
        FhirFullUrlBase = EnvironmentValue("FHIR_FULL_URL_BASE") ?? FhirFullUrlBase;
        FhirUsername = EnvironmentValue("FHIR_API_USERNAME") ?? FhirUsername;
        FhirPassword = EnvironmentValue("FHIR_API_PASSWORD") ?? FhirPassword;
        if (long.TryParse(Environment.GetEnvironmentVariable("MAX_FILE_SIZE_BYTES"), out long maxSize)) MaxFileSizeBytes = maxSize;
        if (int.TryParse(Environment.GetEnvironmentVariable("FHIR_TIMEOUT_SECONDS"), out int timeout)) FhirTimeoutSeconds = timeout;
        string? corsAllowedOrigins = Environment.GetEnvironmentVariable("CORS_ALLOWED_ORIGINS");
        if (!string.IsNullOrWhiteSpace(corsAllowedOrigins))
        {
            CorsAllowedOrigins = corsAllowedOrigins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static string? EnvironmentValue(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(IncomingPath) || !IncomingPath.StartsWith('/'))
            throw new InvalidOperationException("ImportApi:IncomingPath must start with '/'.");
        if (string.IsNullOrWhiteSpace(FhirEndpoint) || !Uri.TryCreate(FhirEndpoint, UriKind.Absolute, out _))
            throw new InvalidOperationException("ImportApi:FhirEndpoint must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(FhirFullUrlBase) || !Uri.TryCreate(FhirFullUrlBase, UriKind.Absolute, out _))
            throw new InvalidOperationException("ImportApi:FhirFullUrlBase must be an absolute URL.");
        if (MaxFileSizeBytes <= 0) throw new InvalidOperationException("ImportApi:MaxFileSizeBytes must be positive.");
        if (FhirTimeoutSeconds <= 0) throw new InvalidOperationException("ImportApi:FhirTimeoutSeconds must be positive.");
    }

    public void ValidateForSubmission()
    {
        Validate();
        if (string.IsNullOrWhiteSpace(FhirUsername) || string.IsNullOrWhiteSpace(FhirPassword))
            throw new InvalidOperationException("FHIR Basic Authorization credentials are not configured.");
    }
}
