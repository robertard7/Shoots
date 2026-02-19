$ErrorActionPreference = 'Stop'

dotnet restore src/Runtime/Shoots.Runtime.sln
dotnet build src/Runtime/Shoots.Runtime.sln -c Release --no-restore
dotnet test src/Runtime/Shoots.Runtime.Tests/Shoots.Runtime.Tests.csproj -c Release -m:1
dotnet test src/Host/Shoots.Host.Tests/Shoots.Host.Tests.csproj -c Release -m:1

dotnet restore ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj
dotnet test ui/Shoots.Ui.Tests/Shoots.Ui.Tests.csproj -c Release -m:1 --logger "trx;LogFileName=ui-tests.trx"
