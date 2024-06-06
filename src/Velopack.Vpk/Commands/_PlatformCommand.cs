﻿namespace Velopack.Vpk.Commands;

public abstract class PlatformCommand : OutputCommand
{
    public string TargetRuntime { get; private set; }

    protected CliOption<string> TargetRuntimeOption { get; private set; }

    protected PlatformCommand(string name, string description) : base(name, description)
    {
        TargetRuntimeOption = AddOption<string>((v) => TargetRuntime = v, "-r", "--runtime")
            .SetDescription("The target runtime to build packages for.")
            .SetArgumentHelpName("RID")
            .MustBeSupportedRid();
    }
}
