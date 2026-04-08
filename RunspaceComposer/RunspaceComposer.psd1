@{
    RootModule        = 'Microsoft.PowerShell.RunspaceComposer.dll'
    ModuleVersion     = '1.0.0'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'Joshua Kidder'
    Description       = 'Managed RunspacePool wrapper with versioned variable broadcasting and per-runspace state synchronization.'
    PowerShellVersion = '7.6'
    CLRVersion        = '11.0'

    CmdletsToExport   = @()
    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags       = @('Runspace', 'RunspacePool', 'Threading', 'Parallel')
            ProjectUri = ''
        }
    }
}
