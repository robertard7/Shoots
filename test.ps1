$ErrorActionPreference = 'Stop'

dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1

$job = Start-Job -ScriptBlock {
  dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Release -m:1 --logger "trx;LogFileName=ui-tests.trx"
}
if (-not (Wait-Job -Job $job -Timeout 600)) {
  Stop-Job -Job $job
  Receive-Job -Job $job -Keep
  throw "UI tests timed out after 10 minutes."
}
Receive-Job -Job $job
