using Ironbrain.Calendar;
using Ironbrain.MailCal;
using Ironbrain.MailCal.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// MCP logs must not go to stdout (stdio transport).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var configPath = Environment.GetEnvironmentVariable("IRONBRAIN_MAILCAL_CONFIG");
var configBuilder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory());
if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
    configBuilder.AddJsonFile(configPath, optional: false, reloadOnChange: false);
else
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var defaultPath = Path.Combine(home, ".config", "ironbrain", "mailcal.json");
    if (File.Exists(defaultPath))
        configBuilder.AddJsonFile(defaultPath, optional: false, reloadOnChange: false);
    else
        configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
}

configBuilder.AddEnvironmentVariables(prefix: "IRONBRAIN_");
configBuilder.AddEnvironmentVariables();
var configuration = configBuilder.Build();
var catalog = AccountCatalog.FromConfiguration(configuration);

builder.Services.AddSingleton<IConfiguration>(configuration);
builder.Services.AddMailCal(catalog);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<MailCalTools>();

await builder.Build().RunAsync();
