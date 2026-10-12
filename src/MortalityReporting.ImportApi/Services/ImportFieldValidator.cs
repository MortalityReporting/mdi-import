using System.Globalization;
using MortalityReporting.ImportApi.Models;

namespace MortalityReporting.ImportApi.Services;

public static class ImportFieldValidator
{
    private static readonly HashSet<string> DateFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "birthdate", "decedentdob", "reportdate", "founddate", "cdeathdate", "eventdate", "prndate", "examdate", "cinjdate", "cinjdateearly", "cinjdatelate", "lkadate", "athospdate", "surgdate", "dateofinjury", "decedentdateofdeath", "datepronounceddead", "certifieddate"
    };

    private static readonly HashSet<string> TimeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "reporttime", "foundtime", "cdeathtime", "eventtime", "prntime", "cinjtime", "lkatime", "athosptime", "timeofinjury", "decedenttimeofdeath", "timepronounceddead"
    };

    public static IReadOnlyList<string> Validate(string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        string normalized = ImportRecord.Normalize(fieldName);
        if (DateFields.Contains(normalized) && !TryDate(value)) return [$"'{value}' is not a valid date."];
        if (TimeFields.Contains(normalized) && !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _)) return [$"'{value}' is not a valid time."];
        if (normalized is "gender" or "decedentsexatdeath" or "decedentsex")
        {
            string[] allowed = ["male", "female", "other", "unknown", "asked but unknown"];
            if (!allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) return [$"'{value}' is not a supported gender value."];
        }
        if (normalized is "manner" or "mannerofdeath")
        {
            string[] allowed = ["natural death", "accidental death", "suicide", "homicide", "patient awaiting investigation", "death, manner undetermined"];
            if (!allowed.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) return [$"'{value}' is not a supported manner of death."];
        }
        return [];
    }

    private static bool TryDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _) ||
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);
}
