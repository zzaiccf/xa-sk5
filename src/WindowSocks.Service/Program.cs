using WindowSocks.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WindowSocks");
builder.Services.AddSingleton<Controller>();
builder.Services.AddHostedService<PipeWorker>();
builder.Services.AddSingleton<IHostedService>(services => new PipeWorker(services.GetRequiredService<Controller>(), services.GetRequiredService<ILogger<PipeWorker>>(), services.GetRequiredService<IHostApplicationLifetime>(), WindowSocks.Core.PipeTransport.SandboxName));
builder.Services.AddHostedService<MonitorWorker>();
builder.Services.AddHostedService<SandboxMonitorWorker>();
await builder.Build().RunAsync();
