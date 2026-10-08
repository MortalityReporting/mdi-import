# Mortality Reporting Import API

A .NET 9 API for importing mortality reporting data in CSV or XLSX format and submitting it to a FHIR endpoint.

The response contract was modeled after the Java reference implementation at
`https://github.com/MortalityReporting/raven-import-and-submit-api`; Java source
is not included or required here.

## Setup, Build, and Run

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the API
dotnet run --project src/MortalityReporting.ImportApi
```

The API will start on the configured port (default: 5000 for HTTP, 5001 for HTTPS).

## Environment Variables

| Variable | Description |
|----------|-------------|
| `INCOMING_API_PATH` | Endpoint path for file uploads (default: `/import`) |
| `INCOMING_API_USERNAME` | Username for Basic authentication on incoming requests |
| `INCOMING_API_PASSWORD` | Password for Basic authentication on incoming requests |
| `FHIR_API_ENDPOINT` | FHIR server endpoint URL for data submission |
| `FHIR_API_USERNAME` | Username for Basic authentication on FHIR endpoint |
| `FHIR_API_PASSWORD` | Password for Basic authentication on FHIR endpoint |
| `FHIR_TIMEOUT_SECONDS` | Timeout for FHIR requests (default: 30) |
| `MAX_FILE_SIZE_BYTES` | Maximum allowed file size (default: 50MB) |

## Configuration Options

The API supports the following configurable options:

- **Incoming Path**: Configurable via `INCOMING_API_PATH` environment variable
- **FHIR Endpoint**: Configurable via `FHIR_API_ENDPOINT` environment variable
- **Resource references**: Bundle `fullUrl` and resource references use relative values such as `Patient/patient-123`.
- **Basic Authentication**: Required on both incoming requests and outgoing FHIR submissions
- **File Upload Field**: Multipart field name is `file`

## Supported File Formats

The API accepts files in the following formats:

- CSV (`.csv`)
- Excel (`.xlsx`)

Files must be uploaded using the multipart field name `file`.

## Supported Templates

The API supports the following mortality reporting templates:

### MDI Death Certificate XLSX
- `MDI-To-EDRS-Template.xlsx` - schema-based Excel template for MDI death certificate profiles

### MDI Death Certificate CSV
- `MDI-To-EDRS-Template.csv` - flat CSV template for MDI death certificate profiles; its columns are intentionally different from the XLSX template

### Death Certificate Reporting (DCR)
- `DCR-Template.xlsx` - Excel template for DCR format

### Toxicology
- `Tox-To-MDI-Template.xlsx` - Excel template for toxicology data

### Death Certificate Review
- `DCR-Template.xlsx` - Excel template for death certificate review profiles

## API Response Shape

The API preserves the observed Java reference response shape with the following structure:

```json
[
  {
    "fhirBundle": { "resourceType": "Bundle", "type": "document" },
    "fields": {
      "field1": { "status": "mapped", "value": "value1", "errors": [], "fhirResource": {} },
      "field2": { "status": "not mapped", "value": "", "errors": [], "fhirResource": {} }
    },
    "fhirResponse": {
      "name": "Jane Doe",
      "status": "Success",
      "statusCode": 201,
      "state": "",
      "location": "https://fhir-server.com/Bundle/123"
    }
  }
]
```

Each record in the response array contains:
- `fhirBundle`: The generated FHIR Bundle JSON for the record
- `fields`: Per-field mapping status, value, and mapping errors
- `fhirResponse`: FHIR submission status and HTTP status code

## Status Codes

| Code | Description |
|------|-------------|
| 201 | Successfully processed file with records created |
| 204 | No content (empty file or no records) |
| 400 | Bad request (invalid content type, missing file, etc.) |
| 401 | Unauthorized (invalid or missing Basic auth credentials) |
| 413 | Payload too large (exceeds maximum file size) |
| 415 | Unsupported media type (not CSV or XLSX) |
| 422 | Unprocessable entity (parsing errors in file) |
| 503 | Service unavailable (FHIR server error) |

## Health Check

A health check endpoint is available at `/health` which returns:
```json
{
  "status": "ok"
}
```

## Testing

Run the test suite with:
```bash
dotnet test tests/MortalityReporting.ImportApi.Tests
```

## Implementation Notes

- This is a .NET 9 implementation
- No Java source code is included in this repository
- The API maintains compatibility with the Java reference implementation's response format
- Multipart file uploads require the field name `file`
- Both incoming and outgoing requests require Basic authentication when credentials are configured
- DCR submitter/contact data maps to a Practitioner and funeral-home data maps to an Organization.
- Toxicology laboratory metadata maps to an Organization and general comments map to `DiagnosticReport.conclusion`.
- XLSX residence, death, injury, and autopsy address labels map to FHIR Address/Location resources; autopsy status maps to the VRDR autopsy indicator profile.
- Patient resources always include `identifier`; Social Security Number is used when available, otherwise the identifier carries `data-absent-reason=unknown`.

## Delegation loop

`.github/workflows/delegation-loop.yml` runs when import templates, source code,
tests, Markdown, solution/project files, or the delegation script changes. It
delegates build/test validation to the .NET test suite and runs Markdown
whitespace checks when documentation changes. Run the same loop locally with:

```bash
./tools/delegation-loop.sh --all
```

To simulate one delegated scope locally, provide changed paths explicitly:

```bash
DELEGATION_CHANGED_FILES=$'import-templates/MDI-To-EDRS-Template.xlsx' ./tools/delegation-loop.sh
```
