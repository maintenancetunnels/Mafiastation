namespace Mafiastation.AiPilotLab;

public sealed class CommandLineArguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; }

    private CommandLineArguments(string command)
    {
        Command = command;
    }

    public static CommandLineArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new CommandLineArguments("help");

        var parsed = new CommandLineArguments(args[0].Trim().ToLowerInvariant());
        for (var index = 1; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                throw new ArgumentException($"Unexpected argument: {token}");

            var key = token[2..];
            var value = "true";
            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++index];

            if (!parsed._options.TryGetValue(key, out var values))
            {
                values = new List<string>();
                parsed._options.Add(key, values);
            }
            values.Add(value);
        }

        return parsed;
    }

    public bool Has(string key) => _options.ContainsKey(key);

    public string? Get(string key)
    {
        return _options.TryGetValue(key, out var values) && values.Count > 0
            ? values[^1]
            : null;
    }

    public IReadOnlyList<string> GetMany(string key)
    {
        return _options.TryGetValue(key, out var values)
            ? values
            : Array.Empty<string>();
    }

    public string Require(string key)
    {
        return Get(key) ?? throw new ArgumentException($"Missing required --{key} option.");
    }

    public bool GetFlag(string key)
    {
        var value = Get(key);
        return value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }

    public int GetInt(string key, int defaultValue, int minimum, int maximum)
    {
        var value = Get(key);
        if (value == null)
            return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"--{key} must be an integer from {minimum} through {maximum}.");
        return parsed;
    }

    public double GetDouble(string key, double defaultValue, double minimum, double maximum)
    {
        var value = Get(key);
        if (value == null)
            return defaultValue;
        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException($"--{key} must be a number from {minimum} through {maximum}.");
        }
        return parsed;
    }
}
