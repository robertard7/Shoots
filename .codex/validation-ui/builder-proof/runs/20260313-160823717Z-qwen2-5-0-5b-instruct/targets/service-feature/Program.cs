using ProofFeatureService;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", target = "builder-proof-service-feature" }));
app.MapGet("/greet/{name}", (string name) => Results.Ok(new GreetingPayload(name, $"Hello {name}!")));

app.Run();

public partial class Program
{
}