## First-failure discipline checklist

- [ ] Captured first failing job/step
- [ ] Fixed only first failure
- [ ] Windows authority run is green (`dotnet test -c Release -p:ContinuousIntegrationBuild=true`)
