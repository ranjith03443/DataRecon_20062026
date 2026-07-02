using DataReconciliation.AI.Clients;
using DataReconciliation.AI.Validators;
using DataReconciliation.Application.Interfaces;
using DataReconciliation.Application.Services;
using DataReconciliation.Application.Workflow;
using DataReconciliation.Infrastructure.Middleware;
using DataReconciliation.Infrastructure.Caching;
using DataReconciliation.Infrastructure.FileStorage;
using DataReconciliation.Infrastructure.Persistence;
using DataReconciliation.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Serilog.Filters;

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ────────────────────────────────────────────────────────────────
const string logOutputTemplate =
    "[{Timestamp:yyyy-MM-dd HH:mm:ss zzz} {Level:u3}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: logOutputTemplate)
    .WriteTo.File(
        path: Path.Combine(builder.Environment.ContentRootPath, "logs", "app-.log"),
        outputTemplate: logOutputTemplate,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.FromSource("DataReconciliation.Application.Transformations"))
        .WriteTo.File(
            path: Path.Combine(builder.Environment.ContentRootPath, "logs", "transformation-.log"),
            outputTemplate: logOutputTemplate,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30))
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(Matching.FromSource("DataReconciliation.Application.Transformations.Validation"))
        .WriteTo.File(
            path: Path.Combine(builder.Environment.ContentRootPath, "logs", "validation-.log"),
            outputTemplate: logOutputTemplate,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30))
    .CreateLogger();

builder.Host.UseSerilog();

// ─── MVC ────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews();

// ─── EF Core / SQLite ────────────────────────────────────────────────────────
var dbPath = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "datareconciliation.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(dbPath));

// ─── Caching ─────────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AICacheService>();

// ─── Repositories ─────────────────────────────────────────────────────────────
builder.Services.AddScoped<IWorkflowJobRepository, WorkflowJobRepository>();
builder.Services.AddScoped<IWorkflowStepExecutionRepository, WorkflowStepExecutionRepository>();
builder.Services.AddScoped<IWorkflowArtifactRepository, WorkflowArtifactRepository>();
builder.Services.AddScoped<IAIInferenceAuditRepository, AIInferenceAuditRepository>();
builder.Services.AddScoped<IErrorAuditLogRepository, ErrorAuditLogRepository>();

// ─── Infrastructure Services ──────────────────────────────────────────────────
builder.Services.AddScoped<IArtifactPersistenceService, ArtifactPersistenceService>();
builder.Services.AddScoped<IFileIngestionService, FileIngestionService>();

// ─── AI Services ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<IAIResponseValidator, AIResponseValidator>();

// Switch between Python local service and direct OpenAI based on AI:Provider config
//   "PythonService" → calls your local Python FastAPI/Flask service
//   "OpenAI"        → calls OpenAI directly (requires AI:ApiKey)
//   AI:Enabled=false → AI is skipped entirely; deterministic mapping is used as-is
var aiProvider = builder.Configuration["AI:Provider"] ?? "OpenAI";
if (aiProvider.Equals("PythonService", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IAIInferenceService, PythonAIInferenceService>(client =>
    {
        var baseUrl = builder.Configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
        client.BaseAddress = new Uri(baseUrl);
    });
    Console.WriteLine($"[AI] Provider = PythonService → {builder.Configuration["AI:PythonService:BaseUrl"]}");
}
else
{
    builder.Services.AddHttpClient<IAIInferenceService, OpenAIInferenceService>();
    Console.WriteLine("[AI] Provider = OpenAI");
}


// ─── Python AI Rule Inference (Step 11) ──────────────────────────────────────
if (aiProvider.Equals("PythonService", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IAIRuleInferenceService, PythonRuleInferenceService>(client =>
    {
        var baseUrl = builder.Configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
        client.BaseAddress = new Uri(baseUrl);
    });
    // Named HttpClient for SemanticEnrichmentService (Step 5)
    builder.Services.AddHttpClient("PythonService", client =>
    {
        var baseUrl = builder.Configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
        client.BaseAddress = new Uri(baseUrl);
    });
    Console.WriteLine("[AI] Rule Inference + Semantic Enrichment → PythonService");
}

// ─── Application Services ─────────────────────────────────────────────────────
builder.Services.AddScoped<IDatasetRegistrationService, DatasetRegistrationService>();
builder.Services.AddScoped<ISourceSchemaProfilingService, SourceSchemaProfilingService>();
builder.Services.AddScoped<ITargetMetadataExtractionService, TargetMetadataExtractionService>();
builder.Services.AddScoped<ITargetSchemaGeneratorService, TargetSchemaGeneratorService>();
builder.Services.AddScoped<IMainframeArtifactGenerationService, MainframeArtifactGenerationService>();
builder.Services.AddScoped<IMainframeAssetGenerationService, MainframeAssetGenerationService>();
builder.Services.AddScoped<IReconProgramGenerationService, ReconProgramGenerationService>();
builder.Services.AddHttpClient<IMainframeAiAgentService, PythonMainframeAiAgentService>(client =>
{
    var baseUrl = builder.Configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
    client.BaseAddress = new Uri(baseUrl);
});
Console.WriteLine("[AI] Mainframe Development AI Agent → PythonService");
builder.Services.AddScoped<ISemanticSchemaEnrichmentService, SemanticSchemaEnrichmentService>();
builder.Services.AddScoped<IDeterministicMappingService, DeterministicMappingService>();
builder.Services.AddScoped<IAIMappingInferenceService, AIMappingInferenceService>();
builder.Services.AddScoped<IRelationshipResolutionService, RelationshipResolutionService>();
builder.Services.AddScoped<ICanonicalDataModelBuilderService, CanonicalDataModelBuilderService>();
builder.Services.AddScoped<IMappingConsolidationService, MappingConsolidationService>();
builder.Services.AddScoped<ITransformationEngineService, TransformationEngineService>();
builder.Services.AddScoped<ITargetFileGenerationService, TargetFileGenerationService>();
builder.Services.AddScoped<IReconciliationService, ReconciliationService>();

// ─── Deterministic Transformation Module (new governed execution framework) ─
// ─── Value Mapping Workbench Services ────────────────────────────────────────
builder.Services.AddScoped<IValueMappingDiscoveryService, ValueMappingDiscoveryService>();

// ─── Enhancement Services ────────────────────────────────────────────────────
builder.Services.AddScoped<IDeltaFileUploadService, DeltaFileUploadService>();
builder.Services.AddScoped<IReconciliationConfigService, ReconciliationConfigService>();
builder.Services.AddScoped<ITransformationOverrideService, TransformationOverrideService>();

if (aiProvider.Equals("PythonService", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IValueMappingAIAgentService, PythonValueMappingAgentService>(client =>
    {
        var baseUrl = builder.Configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
        client.BaseAddress = new Uri(baseUrl);
    });
    Console.WriteLine("[AI] Value Mapping Agent → PythonService");
}
else
{
    builder.Services.AddHttpClient<IValueMappingAIAgentService, DataReconciliation.AI.Clients.PythonValueMappingAgentService>(client =>
    {
        var baseUrl = builder.Configuration["AI:PythonService:BaseUrl"] ?? "http://localhost:8000";
        client.BaseAddress = new Uri(baseUrl);
    });
}

DataReconciliation.Infrastructure.Transformations.ServiceCollectionExtensions
    .AddDeterministicTransformationModule(builder.Services);

// ─── AI Settings ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IAISettingsService, AISettingsService>();

// ─── Report Generation ────────────────────────────────────────────────────────
builder.Services.AddScoped<IReportGenerationService, ReportGenerationService>();

// ─── Workflow Orchestrator ────────────────────────────────────────────────────
builder.Services.AddScoped<IWorkflowOrchestratorService, WorkflowOrchestratorService>();

// ─── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// ─── Migrate DB ───────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Add token-usage columns to AIInferenceAudits (safe on existing DBs — SQLite ignores duplicate columns)
    try
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        foreach (var ddl in new[]
        {
            "ALTER TABLE \"AIInferenceAudits\" ADD COLUMN \"PromptTokens\" INTEGER",
            "ALTER TABLE \"AIInferenceAudits\" ADD COLUMN \"CompletionTokens\" INTEGER",
            "ALTER TABLE \"AIInferenceAudits\" ADD COLUMN \"TotalTokens\" INTEGER",
            "ALTER TABLE \"AIInferenceAudits\" ADD COLUMN \"EstimatedCostUsd\" REAL"
        })
        {
            try { cmd.CommandText = ddl; cmd.ExecuteNonQuery(); }
            catch { /* column already exists – safe to ignore */ }
        }
        conn.Close();
    }
    catch { /* DB may not exist yet – EnsureCreated will create it with all columns */ }
}

// ─── Middleware ───────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// NOTE: UseHttpsRedirection removed — run on HTTP (http://localhost:5000)
app.UseStaticFiles();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseSerilogRequestLogging();
app.UseRouting();
app.UseCors();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Workflow}/{action=Index}/{id?}");

app.MapControllers(); // For API controllers

app.Run();
