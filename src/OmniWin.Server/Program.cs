using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OmniWin.Browser;
using OmniWin.Core;
using OmniWin.Desktop;
using OmniWin.Ocr;
using OmniWin.Safety;
using OmniWin.Server.Engines;
using OmniWin.Server.Hosting;
using OmniWin.Server.Power;
using OmniWin.SystemTools;

// stdout is reserved for MCP JSON-RPC. Everything meant for people goes to stderr.
// Startup order (contract §5): options → config → elevation check → host.
// Exit codes: 2 = bad arguments, 3 = config problem, 4 = refused to run elevated.

// --validate-config <file>: check a config file and exit. Prints one JSON object on stdout; starts nothing and changes nothing.
if (args.Length == 2 && args[0] == "--validate-config")
{
    var problems = ConfigStore.Check(args[1]);
    Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { ok = problems.Count == 0, errors = problems }));
    return problems.Count == 0 ? 0 : 3;
}

if (!ServerOptions.TryParse(args, out var options, out var argError))
{
    await Console.Error.WriteLineAsync($"omniwin: {argError}\n{ServerOptions.Usage}");
    return 2;
}

var paths = options.DataDir is null ? OmniWinPaths.Default() : OmniWinPaths.FromOverride(options.DataDir);

OmniWinConfig config;
try
{
    config = ConfigStore.LoadOrCreate(paths);
}
catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync($"omniwin: config error: {ex.Message}");
    return 3;
}

if (config.ConfirmMode == OmniWinConfig.ConfirmModeTestAutoAllow && !options.TestMode)
{
    await Console.Error.WriteLineAsync("omniwin: confirm_mode \"test_auto_allow\" is only allowed with --test-mode");
    return 3;
}

var platform = new PlatformInfo(Platform.IsElevated(), Platform.DpiAwareness());
if (platform.Elevated && !(options.AllowElevated && config.AllowElevated))
{
    await Console.Error.WriteLineAsync(
        "omniwin: refusing to run as administrator. Start it from a normal (non-elevated) process, or pass " +
        "--allow-elevated and set \"allow_elevated\": true in config.json.");
    return 4;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
// The SDK logs every request at Information; keep stderr for things worth reading.
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(platform);
builder.Services.AddSingleton<IMetrics, RollingMetrics>();
builder.Services.AddSingleton<KillSwitch>();
builder.Services.AddSingleton<IKillSwitch>(services => services.GetRequiredService<KillSwitch>());
builder.Services.AddSingleton<IUserActivity>(services => services.GetRequiredService<KillSwitch>());
builder.Services.AddSingleton<SafetyGate>();
builder.Services.AddSingleton<ISafetyGate>(services => services.GetRequiredService<SafetyGate>());
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<IAuditLog>(services => services.GetRequiredService<AuditLog>());
builder.Services.AddSingleton(services =>
{
    var engine = ActivatorUtilities.CreateInstance<DesktopEngine>(services);
    engine.UseMachineLane();   // one input lane for every OmniWin process in this session (the AI client's server and the app's)
    return engine;
});
builder.Services.AddSingleton<IDesktopEngine>(services => services.GetRequiredService<DesktopEngine>());
builder.Services.AddSingleton<IForegroundService>(services => services.GetRequiredService<DesktopEngine>());
builder.Services.AddSingleton<ILabelSource>(services => services.GetRequiredService<DesktopEngine>());
builder.Services.AddSingleton<IInputLane>(services => services.GetRequiredService<DesktopEngine>());
builder.Services.AddSingleton<ISelectionSource>(services => services.GetRequiredService<DesktopEngine>());
// Bridged engines (Windows-MCP, agent-desktop): started lazily, only when engine_call is used.
builder.Services.AddSingleton(services => new EngineManager(config, services.GetRequiredService<ISafetyGate>(),
    services.GetRequiredService<IKillSwitch>(), services.GetRequiredService<IMetrics>(), services.GetRequiredService<IInputLane>()));
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<KillSwitch>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<KillSwitch>().ActivityStatus);
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<SafetyGate>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<AuditLog>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<DesktopEngine>());
// WS3: system tools and OCR. The OCR word store is also what desktop_act uses to click oN refs.
builder.Services.AddSingleton<OcrRefStore>();
builder.Services.AddSingleton<IOcrRefResolver>(services => services.GetRequiredService<OcrRefStore>());
builder.Services.AddSingleton(services => new SystemToolsService(
    services.GetRequiredService<ISafetyGate>(), services.GetRequiredService<IKillSwitch>(), services.GetRequiredService<IUserActivity>(),
    services.GetRequiredService<IMetrics>(), services.GetRequiredService<IDesktopEngine>(), services.GetRequiredService<IForegroundService>(),
    new SystemToolsOptions { AppLaunchAllowPaths = config.AppLaunchAllowPaths }));
builder.Services.AddSingleton<ISystemTools>(services => services.GetRequiredService<SystemToolsService>());
builder.Services.AddSingleton(services => new OcrService(
    services.GetRequiredService<ISafetyGate>(), services.GetRequiredService<IKillSwitch>(), services.GetRequiredService<IMetrics>(),
    services.GetRequiredService<IDesktopEngine>(), services.GetRequiredService<OcrRefStore>(), new OcrOptions()));
builder.Services.AddSingleton<IOcrFallback>(services => services.GetRequiredService<OcrService>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<SystemToolsService>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<OcrService>());
// The browser engine starts lazily: constructing it launches nothing. The first browser tool call starts OmniWin's own browser.
builder.Services.AddSingleton<BrowserEngine>();
builder.Services.AddSingleton<IBrowserEngine>(services => services.GetRequiredService<BrowserEngine>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<BrowserEngine>());
// Look-only views (screen_view, browser_view): they read pixels, send no input and act on nothing.
builder.Services.AddSingleton<IBrowserViewer>(services => services.GetRequiredService<BrowserEngine>());
builder.Services.AddSingleton(services => new ScreenViewService(
    services.GetRequiredService<ISafetyGate>(), services.GetRequiredService<IKillSwitch>(), services.GetRequiredService<IMetrics>(),
    services.GetRequiredService<IDesktopEngine>(), new ScreenViewOptions(), services.GetRequiredService<ILabelSource>()));
builder.Services.AddSingleton<IScreenViewer>(services => services.GetRequiredService<ScreenViewService>());
// Run log and workflows (WS0). Engines other than the desktop are optional here: whichever is registered is used.
builder.Services.AddSingleton<OmniWin.Workflows.RunLog>();
builder.Services.AddSingleton<IRunLog>(services => services.GetRequiredService<OmniWin.Workflows.RunLog>());
builder.Services.AddSingleton(services => new OmniWin.Workflows.StepRecorder(
    services.GetRequiredService<IRunLog>(), services.GetRequiredService<IDesktopEngine>(), services.GetService<IBrowserEngine>()));
builder.Services.AddSingleton(services => new OmniWin.Workflows.WorkflowStore(services.GetRequiredService<OmniWinPaths>().WorkflowsDir));
builder.Services.AddSingleton(services => new OmniWin.Workflows.WorkflowService(
    services.GetRequiredService<OmniWin.Workflows.StepRecorder>(), services.GetRequiredService<OmniWin.Workflows.WorkflowStore>(),
    services.GetRequiredService<ISafetyGate>(), services.GetRequiredService<IKillSwitch>(), services.GetRequiredService<IMetrics>(),
    services.GetRequiredService<IDesktopEngine>(), services.GetService<IBrowserEngine>(), services.GetService<ISystemTools>()));
builder.Services.AddSingleton<IComponentStatus>(services => new OmniWin.Workflows.WorkflowsComponent(
    services.GetRequiredService<OmniWin.Workflows.WorkflowStore>(), services.GetRequiredService<IRunLog>()));
// Stage B: the Laya intent model, only when it is switched on in config.json AND its files are on disk (it also has to pass its
// offline gate before the owner turns it on). Without it omniwin_do uses the phrase table alone.
if (OmniWin.Server.Router.LayaClassifier.Available(config))
{
    builder.Services.AddSingleton<OmniWin.Server.Router.IIntentClassifier>(services =>
        new OmniWin.Server.Router.LayaClassifier(config, services.GetRequiredService<IKillSwitch>()));
}

// Contract 2.6: learned recipes (cards and an append-only events file beside the workflows).
builder.Services.AddSingleton(services => new OmniWin.Server.Learning.RecipeStore(services.GetRequiredService<OmniWinPaths>().RecipesDir));
builder.Services.AddSingleton(services => new OmniWin.Server.Learning.LearningService(
    services.GetRequiredService<OmniWin.Workflows.WorkflowService>(), services.GetRequiredService<IRunLog>(),
    services.GetRequiredService<OmniWin.Server.Learning.RecipeStore>(), config));
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<OmniWin.Server.Learning.LearningService>());
// The optional decision model. No adapter is built in yet (the owner has not chosen a model), so the slot is empty: with
// decision.provider "none" nothing is constructed or called, and any other provider name reports "no adapter in this build".
builder.Services.AddSingleton(services =>
{
    var paths = services.GetRequiredService<OmniWinPaths>();
    return new OmniWin.Server.Learning.DecisionService(config, services.GetRequiredService<OmniWin.Server.Learning.LearningService>(),
        services.GetService<OmniWin.Server.Learning.IDecisionModel>(), paths.RecipesDir,
        () => File.Exists(paths.ConfigFile) ? File.GetLastWriteTimeUtc(paths.ConfigFile) : null);
});
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<OmniWin.Server.Learning.DecisionService>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<EngineManager>());
builder.Services.AddSingleton<IComponentStatus>(services => services.GetRequiredService<OmniWin.Server.Router.RouterService>());
// The fast router behind omniwin_do (phrase table; the Laya classifier is added by the router bootstrap when it is enabled).
builder.Services.AddSingleton(services => new OmniWin.Server.Router.RouterService(
    services.GetRequiredService<ISystemTools>(), services.GetRequiredService<IDesktopEngine>(),
    new OmniWin.Server.Router.WorkflowCatalog(services.GetRequiredService<OmniWin.Workflows.WorkflowService>()), config, services.GetRequiredService<IMetrics>(),
    services.GetService<OmniWin.Server.Router.IIntentClassifier>(),
    services.GetRequiredService<OmniWin.Server.Learning.LearningService>(),
    services.GetRequiredService<OmniWin.Server.Learning.DecisionService>()));
// Contract 2.6: selected-text tools (selection_get, selection_replace, polish_prompts).
builder.Services.AddSingleton(services => new OmniWin.Server.Tools.SelectionService(
    services.GetRequiredService<ISelectionSource>(), services.GetRequiredService<IDesktopEngine>(), services.GetRequiredService<ISystemTools>(),
    services.GetRequiredService<ISafetyGate>(), services.GetRequiredService<IKillSwitch>(), services.GetRequiredService<OmniWinPaths>()));
builder.Services.AddSingleton<ServerState>();

var mcp = builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "omniwin", Version = ServerState.Version })
    .WithStdioServerTransport()
    .WithToolsFromAssembly(serializerOptions: CoreJson.Options);

// Optional tools exist only when switched on, so a model is not buried in tool definitions it cannot use.
if (config.Engines.Enabled && EngineCatalog.AnyUsable(config))
{
    mcp.WithTools<EngineCallTool>(CoreJson.Options);
}

// Power tools (shell, files, registry, process): registered only when the owner switched them on. Off by default.
if (config.PowerTools.Enabled)
{
    mcp.WithTools<PowerTools>(CoreJson.Options);
}

mcp.WithRequestFilters(filters => filters
        .AddCallToolFilter(ToolCallAudit.Filter)
        // After the audit filter, so the audit line sees the step= token the run log adds to a result.
        .AddCallToolFilter(RunLogFilter.Filter));

await builder.Build().RunAsync();
return 0;
