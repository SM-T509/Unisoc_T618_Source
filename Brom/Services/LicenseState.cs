using System;

namespace Brom.Services;

/// <summary>
/// License state behind the sidebar block.
///
/// License validity is based on a fixed date window. There is no online
/// activation and no developer mode switch in this build.
/// </summary>
public static class LicenseState
{
    public const string Holder = "Brom";

    public static readonly DateTime ValidFrom = new DateTime(2026, 10, 13);
    public static readonly DateTime ValidTo = new DateTime(2099, 12, 30);

    /// <summary>Raised after license state changes.</summary>
    public static event Action Changed;

    /// <summary>License is valid when today falls within the date window.</summary>
    public static bool IsValid =>
        DateTime.UtcNow >= ValidFrom && DateTime.UtcNow <= ValidTo;

    /// <summary>Caption above the holder name.</summary>
    public static string StatusCaption => "LICENSE HOLDER";

    /// <summary>Third line of the sidebar block.</summary>
    public static string ExpiryText =>
        $"Valid: {ValidFrom:MMMM d, yyyy} to {ValidTo:MMMM d, yyyy}";

    /// <summary>Text for the About box.</summary>
    public static string AboutText =>
        $"License Holder: {Holder}\nValid: {ValidFrom:MMMM d, yyyy} to {ValidTo:MMMM d, yyyy}";
}
