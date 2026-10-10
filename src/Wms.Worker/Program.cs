using Wms.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a console app during development and as a Windows Service in production (guide §11).
builder.Services.AddWindowsService(options => options.ServiceName = "Wms.Worker");
builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
host.Run();
