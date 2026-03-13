var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", target = "builder-proof" }));
app.MapGet("/version", () => Results.Ok(new { version = 1 }));

app.Run();

public partial class Program
{
}