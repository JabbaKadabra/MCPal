namespace MCPal.Bridge;

/// <summary>Verbs: "run" (default) connects to the MCPal server; "check" validates the config, starts the local servers and prints their tools; "status" prints the status file.</summary>
internal sealed record BridgeCommandLine(string Verb, string[] HostArgs)
{
    public const string Run = "run";
    public const string Check = "check";
    public const string Status = "status";

    public bool IsKnownVerb => Verb is Run or Check or Status;

    public static BridgeCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Only the first argument can be the verb, so option values such as the path after --config never are.
        return args.Length > 0 && !args[0].StartsWith('-')
            ? new BridgeCommandLine(args[0], args[1..])
            : new BridgeCommandLine(Run, args);
    }
}
