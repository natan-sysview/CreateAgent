namespace CreateAgent.Cli;

public enum CliExitCode
{
    Success = 0,
    RunFailed = 1,
    Usage = 2,
    ProviderUnavailable = 3,
    ValidationFailed = 4,
    InternalError = 70,
    TimedOut = 124,
    Cancelled = 130,
}
