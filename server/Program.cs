using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5001, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http3;
        listenOptions.UseHttps();
    });
});
var app = builder.Build();

app.MapGet("/", async () =>
{
    return "Hello World!";
});

Console.WriteLine(Environment.ProcessId);
app.Run();