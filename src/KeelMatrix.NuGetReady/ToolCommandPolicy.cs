namespace KeelMatrix.NuGetReady;

internal static class ToolCommandPolicy
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "COM¹",
        "COM²",
        "COM³",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
        "LPT¹",
        "LPT²",
        "LPT³"
    };

    public static bool IsValid(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 100 || command.Any(char.IsControl) || command.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (command is "." or ".." ||
            command[0] == '.' ||
            command[^1] == '.' ||
            command[^1] == ' ' ||
            command.Contains('/') ||
            command.Contains('\\') ||
            command.Contains(':') ||
            command.Any(character => character is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        var stem = command;
        var extensionSeparator = stem.IndexOf('.', StringComparison.Ordinal);
        if (extensionSeparator >= 0)
        {
            stem = stem[..extensionSeparator];
        }

        return !ReservedDeviceNames.Contains(stem.TrimEnd('.', ' '));
    }
}
