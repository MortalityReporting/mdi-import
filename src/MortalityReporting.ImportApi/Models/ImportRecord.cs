using System.Text;

namespace MortalityReporting.ImportApi.Models;

public enum ImportTemplateKind
{
    MdiAndEdrs,
    Dcr,
    Toxicology
}

public sealed class ImportRecord
{
    public ImportRecord(ImportTemplateKind kind = ImportTemplateKind.MdiAndEdrs)
    {
        Kind = kind;
    }

    public ImportTemplateKind Kind { get; }
    public bool HasCaseSpecificData { get; set; }
    public Dictionary<string, string?> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<Dictionary<string, string?>>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(params string[] names)
    {
        foreach (string name in names)
        {
            string normalizedName = Normalize(name);
            KeyValuePair<string, string?> match = Fields.FirstOrDefault(pair => Normalize(pair.Key) == normalizedName);
            if (!string.IsNullOrWhiteSpace(match.Key) && !string.IsNullOrWhiteSpace(match.Value)) return match.Value.Trim();
        }
        return null;
    }

    public static string Normalize(string value)
    {
        StringBuilder builder = new();
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    public bool HasData => Fields.Values.Any(value => !string.IsNullOrWhiteSpace(value)) || Tables.Values.Any(table => table.Count > 0);
}
