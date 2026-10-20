using WindowSocks.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WindowSocks");
builder.Services.AddSingleton<Controller>();
builder.Services.AddHostedService<PipeWorker>();
builder.Services.AddHostedService<MonitorWorker>();
await builder.Build().RunAsync();
