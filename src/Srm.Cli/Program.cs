using System.CommandLine;
using Srm.Cli.Commands;
using Srm.Runtime;

var policiesDir = PolicyPathResolver.ResolvePoliciesDir(AppContext.BaseDirectory);

var root = new RootCommand("SRM — Secure Runtime Manager")
{
    RunCommand.Build(policiesDir),
    ListCommand.Build(),
    StopCommand.Build(),
    LogsCommand.Build(),
    ValidateCommand.Build(policiesDir),
    EvidenceCommand.Build(),
    DiagCommand.Build(),
    AuditCommand.Build(policiesDir),
    CleanupAccountCommand.Build(),
    TransferCommand.Build(),
};

return await root.InvokeAsync(args);
