using System.Text.Json;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Facilities.Domain;

public enum CustomFieldTarget { Location = 1, Asset = 2 }

public enum CustomFieldType { Text = 1, Number = 2, Date = 3, Boolean = 4, Choice = 5 }

/// <summary>A tenant-defined extra field on locations or assets, with English and Arabic labels.</summary>
public sealed class CustomFieldDefinition : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public CustomFieldTarget Target { get; set; }

    /// <summary>Stable machine key (lowercase letters, digits, underscore). Cannot change once values exist.</summary>
    public string Key { get; set; } = "";

    public string LabelEn { get; set; } = "";
    public string? LabelAr { get; set; }
    public CustomFieldType DataType { get; set; }
    public bool IsRequired { get; set; }

    /// <summary>For Choice fields: JSON array of allowed values, e.g. ["gas","electric"].</summary>
    public string ChoicesJson { get; set; } = "[]";

    /// <summary>For asset fields: limit the field to assets of this class. Null applies to all assets.</summary>
    public Guid? AssetClassId { get; set; }

    public int SortOrder { get; set; }
    public bool IsArchived { get; set; }
}

/// <summary>Validates a custom-field payload against the definitions that apply and returns the normalised JSON to store.</summary>
public static class CustomFieldValidator
{
    public const int MaxTextLength = 1000;

    public static bool IsValidKey(string key) =>
        key.Length is >= 1 and <= 50 && char.IsAsciiLetterLower(key[0]) && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    public static IReadOnlyList<string> ChoicesOf(CustomFieldDefinition d) =>
        JsonSerializer.Deserialize<string[]>(d.ChoicesJson) ?? [];

    /// <param name="applicable">Active definitions that apply to this entity.</param>
    /// <param name="archivedKeys">Keys of archived definitions: values already stored under them are kept untouched.</param>
    public static Result<string> Validate(
        IReadOnlyCollection<CustomFieldDefinition> applicable,
        IReadOnlyCollection<string> archivedKeys,
        IReadOnlyDictionary<string, JsonElement>? values,
        string existingJson)
    {
        var errors = new ValidationErrors();
        var input = values ?? new Dictionary<string, JsonElement>();
        var output = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (key, value) in input)
        {
            if (applicable.All(d => d.Key != key))
            {
                errors.Add($"customFields.{key}", "Unknown custom field.");
            }
        }

        foreach (var def in applicable)
        {
            var field = $"customFields.{def.Key}";
            if (!input.TryGetValue(def.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (def.IsRequired) errors.Add(field, "This field is required.");
                continue;
            }

            if (Check(def, value) is { } problem)
            {
                errors.Add(field, problem);
                continue;
            }

            output[def.Key] = value.Clone();
        }

        // Values under archived definitions are not editable but must not be lost on save.
        var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingJson) ?? [];
        foreach (var key in archivedKeys.Where(existing.ContainsKey))
        {
            output[key] = existing[key];
        }

        return errors.Any ? errors.ToResult<string>() : Result<string>.Ok(JsonSerializer.Serialize(output));
    }

    private static string? Check(CustomFieldDefinition def, JsonElement value) => def.DataType switch
    {
        CustomFieldType.Text => value.ValueKind != JsonValueKind.String ? "Must be text."
            : value.GetString()!.Length > MaxTextLength ? $"Must be at most {MaxTextLength} characters." : null,
        CustomFieldType.Number => value.ValueKind != JsonValueKind.Number ? "Must be a number." : null,
        CustomFieldType.Boolean => value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ? "Must be true or false." : null,
        CustomFieldType.Date => value.ValueKind != JsonValueKind.String || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _)
            ? "Must be a date as yyyy-MM-dd." : null,
        CustomFieldType.Choice => value.ValueKind != JsonValueKind.String || !ChoicesOf(def).Contains(value.GetString()!)
            ? "Must be one of the allowed choices." : null,
        _ => "Unsupported field type.",
    };
}
