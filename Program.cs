using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WifiWatchdog;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var settings = WatchdogSettings.Load(settingsPath);
settings.Validate();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WifiWatchdog");
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<WatchdogLog>();
builder.Services.AddHostedService<WifiWatchdogWorker>();

using var host = builder.Build();
await host.RunAsync();
