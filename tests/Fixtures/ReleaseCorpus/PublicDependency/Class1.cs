namespace Fixture.PublicDependency;

public static class VersionMarker
{
    public static string Get() => Fixture.External.Source.PublicValue.Value;
}
