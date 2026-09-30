namespace MCPal.Agent;

/// <summary>Verbs: "run" (default) connects to the cloud; "check" validates the config, starts the local servers and prints their tools.</summary>
internal sealed record AgentCommandLine(string Verb, string[] HostArgs)
{
    public const string Run = "run";
    public const string Check = "check";

    public bool IsKnownVerb => Verb is Run or Check;

    public static AgentCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Only the first argument can be the verb, so option values such as the path after --config never are.
        return args.Length > 0 && !args[0].StartsWith('-')
            ? new AgentCommandLine(args[0], args[1..])
            : new AgentCommandLine(Run, args);
    }
}
