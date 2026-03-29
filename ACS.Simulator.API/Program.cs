using ACS.Simulator.API.Hubs;
using ACS.Simulator.API.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var runtimeConfigPath = Path.Combine(AppContext.BaseDirectory, "data", "runtime-config.json");

    builder.Configuration.AddJsonFile(runtimeConfigPath, optional: true, reloadOnChange: true);
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "ACS.Simulator";
    });
    builder.Host.UseSystemd();

    // Logging
    builder.Host.UseSerilog((ctx, lc) => lc
        .WriteTo.Console()
        .WriteTo.File("logs/simulator-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
        .ReadFrom.Configuration(ctx.Configuration));

    // Config
    var simulatorConfig = new SimulatorConfig();
    builder.Configuration.GetSection("Simulator").Bind(simulatorConfig);
    builder.Services.AddSingleton(simulatorConfig);
    builder.Services.AddSingleton<SimulatorRuntimeConfigStore>();

    // Core services
    builder.Services.AddSingleton<FleetPersistenceService>();
    builder.Services.AddSingleton<SimulatorService>();
    builder.Services.AddSingleton<ScenarioRunnerService>();
    builder.Services.AddHostedService<SimulatorBroadcastService>();

    // HTTP client for ACS API proxy
    builder.Services.AddHttpClient("AcsApi");

    // Controllers
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "ACS Simulator API", Version = "v1" });
    });

    // SignalR
    builder.Services.AddSignalR();

    // CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    });

    var app = builder.Build();

    app.UseCors("AllowAll");
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "ACS Simulator API v1"));
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();
    app.MapHub<SimulatorHub>("/hubs/simulator");
    app.MapFallbackToFile("index.html");

    Log.Information("ACS Simulator API starting...");

    // Restore saved AGV fleet from disk
    var simulatorService = app.Services.GetRequiredService<SimulatorService>();
    await simulatorService.RestoreFleetAsync();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Simulator API failed to start");
}
finally
{
    Log.CloseAndFlush();
}
