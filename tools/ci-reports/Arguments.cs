namespace CiReports;

/// <summary>A usage error (exit code 2) or a report that cannot be written (1); the message is shown as it is.</summary>
internal sealed class ReportException(string message, int exitCode = 1) : Exception(message)
{
    internal int ExitCode { get; } = exitCode;
}

/// <summary>`--name value` options of a command.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _values;

    private Arguments(Dictionary<string, List<string>> values) => _values = values;

    internal static Arguments Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> names,
        IReadOnlyCollection<string>? repeatable = null)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index].StartsWith("--", StringComparison.Ordinal) ? args[index][2..] : null;
            if (name is null || !names.Contains(name))
                throw new ReportException($"error: unknown argument {args[index]}", 2);
            if (index + 1 >= args.Count)
                throw new ReportException($"error: --{name} needs a value", 2);
            var list = values.TryGetValue(name, out var existing) ? existing : values[name] = [];
            if (list.Count > 0 && repeatable?.Contains(name) != true)
                throw new ReportException($"error: --{name} given twice", 2);
            list.Add(args[++index]);
        }
        return new Arguments(values);
    }

    internal string? Optional(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    internal string Required(string name) =>
        Optional(name) ?? throw new ReportException($"error: --{name} is required", 2);

    internal IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var list) ? list : [];
}
