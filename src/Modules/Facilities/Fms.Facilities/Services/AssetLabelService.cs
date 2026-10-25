using Microsoft.Extensions.Options;
using QRCoder;

namespace Fms.Facilities.Services;

public sealed class FacilitiesOptions
{
    public const string SectionName = "Facilities";

    /// <summary>
    /// Public address of the web app, e.g. https://fms.example.com. A QR label then encodes {PublicBaseUrl}/a/{tag}, so a phone's
    /// camera opens the asset. Empty gives fms://asset/{tag}, readable only by the FMS mobile client.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

public sealed class FacilitiesOptionsValidator : IValidateOptions<FacilitiesOptions>
{
    public ValidateOptionsResult Validate(string? name, FacilitiesOptions o) =>
        string.IsNullOrWhiteSpace(o.PublicBaseUrl) || (Uri.TryCreate(o.PublicBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.Query))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Facilities:PublicBaseUrl must be an absolute http(s) URL without a query string.");
}

/// <summary>Builds the QR label for an asset's tag code.</summary>
public sealed class AssetLabelService(IOptions<FacilitiesOptions> options)
{
    public string PayloadFor(string tagCode)
    {
        var baseUrl = options.Value.PublicBaseUrl?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(baseUrl) ? $"fms://asset/{tagCode}" : $"{baseUrl}/a/{tagCode}";
    }

    /// <summary>A scalable QR image (SVG) with a quiet zone, ready to print.</summary>
    public string QrSvg(string tagCode)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(PayloadFor(tagCode), QRCodeGenerator.ECCLevel.Q);
        return new SvgQRCode(data).GetGraphic(new System.Drawing.Size(256, 256), drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }
}
