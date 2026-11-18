using System.Text.RegularExpressions;

namespace CodyNET.Assembler;

public enum AssemblerDiagnosticSeverity
{
    Note,
    Warning,
    Error
}

/// <summary>
/// A single message reported by 64tass, located in a source file.
/// </summary>
/// <param name="File">Path of the file as reported by 64tass (the preprocessed file or an include).</param>
/// <param name="Line">1-based line number in <paramref name="File"/>.</param>
/// <param name="Column">1-based column number.</param>
public sealed record AssemblerDiagnostic(
    string File,
    int Line,
    int Column,
    AssemblerDiagnosticSeverity Severity,
    string Message)
{
    // 64tass format: "<file>:<line>:<col>: <severity>: <message>"
    // The file part is matched lazily so Windows drive letters ("X:\...") are kept intact.
    private static readonly Regex DiagnosticRegex = new(
        @"^(?<file>.+?):(?<line>\d+):(?<col>\d+): (?<sev>fatal error|error|warning|note): (?<msg>.*)$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts all located diagnostics from 64tass output. The source excerpt and caret lines
    /// that 64tass prints after each message are ignored.
    /// </summary>
    public static IReadOnlyList<AssemblerDiagnostic> ParseTassOutput(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var result = new List<AssemblerDiagnostic>();
        foreach (Match m in DiagnosticRegex.Matches(output.Replace("\r", "")))
        {
            var severity = m.Groups["sev"].Value switch
            {
                "note" => AssemblerDiagnosticSeverity.Note,
                "warning" => AssemblerDiagnosticSeverity.Warning,
                _ => AssemblerDiagnosticSeverity.Error
            };

            result.Add(new AssemblerDiagnostic(
                m.Groups["file"].Value,
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                severity,
                m.Groups["msg"].Value.Trim()));
        }

        return result;
    }
}

/// <summary>
/// Thrown when 64tass rejects the source. <see cref="Diagnostics"/> holds the parsed,
/// located messages (may be empty if 64tass reported nothing with a location).
/// </summary>
public sealed class AssemblerException : InvalidOperationException
{
    public IReadOnlyList<AssemblerDiagnostic> Diagnostics { get; }

    public AssemblerException(string message, IReadOnlyList<AssemblerDiagnostic> diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }
}
