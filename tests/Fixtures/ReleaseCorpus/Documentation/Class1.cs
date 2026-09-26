namespace Fixture.Documentation;

/// <summary>An intentionally obsolete first-name public type.</summary>
[Obsolete("The fixture intentionally includes an obsolete first-name public type.")]
public sealed class AObsoleteType
{
}

/// <summary>
/// A documented public type used by the package-consumer regression.
/// </summary>
public sealed class DocumentedType
{
    /// <summary>
    /// Returns the stable fixture marker.
    /// </summary>
    public static string Marker => "documentation";
}
