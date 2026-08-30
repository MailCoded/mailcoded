using System.Runtime.InteropServices;
using Mailcoded.Core.Protocol;

namespace Mailcoded.Cli.Commands;

internal static class VersionCommand
{
    /// <summary>Bumped with the product; the CLI output shape is versioned separately.</summary>
    public const string ProductVersion = "0.1.0";

    public static int Run(CliOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteString("version", ProductVersion);
            writer.WriteNumber("protocol_version", ProtocolConstants.Version);
            writer.WriteNumber("output_schema_version", CliOutput.SchemaVersion);
            writer.WriteString("runtime", RuntimeInformation.FrameworkDescription);
            writer.WriteString("platform", RuntimeInformation.RuntimeIdentifier);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"mailcoded {ProductVersion}");
        output.Line($"protocol {ProtocolConstants.Version}, output schema {CliOutput.SchemaVersion}");
        output.Line($"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.RuntimeIdentifier}");
        return ExitCodes.Ok;
    }
}
