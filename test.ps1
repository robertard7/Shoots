$ErrorActionPreference = 'Stop'

function Invoke-DotNetTestWithTimeout {
  param(
    [Parameter(Mandatory = $true)][string]$Command,
    [int]$TimeoutSeconds = 600
  )

  $job = Start-Job -ScriptBlock {
    param($cmd)
    Invoke-Expression $cmd
  } -ArgumentList $Command

  if (-not (Wait-Job -Job $job -Timeout $TimeoutSeconds)) {
    Stop-Job -Job $job
    Receive-Job -Job $job -Keep
    throw "dotnet test timeout exceeded ${TimeoutSeconds}s: $Command"
  }

  Receive-Job -Job $job
}

Invoke-DotNetTestWithTimeout -Command 'dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1'
Invoke-DotNetTestWithTimeout -Command 'dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Release -m:1 --logger "trx;LogFileName=ui-tests.trx"'
