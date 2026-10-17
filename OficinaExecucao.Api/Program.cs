using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MongoDB.Driver;
using OficinaExecucao.Api.Consumers;
using OficinaExecucao.Api.Infra;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

const string ServiceName = "oficina-execution-service";

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// -- Logs estruturados (JSON); writeToProviders para o exportador OTLP de logs tambem receber
builder.Host.UseSerilog((context, _, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()),
    writeToProviders: true);

// -- MongoDB (colecao "execucoes", documento por OrdemServicoId)
MongoMapeamento.Registrar();
builder.Services.AddSingleton<IMongoDatabase>(_ =>
    new MongoClient(config["Mongo:ConnectionString"] ?? "mongodb://localhost:27017")
        .GetDatabase(config["Mongo:Database"] ?? "oficina_execucao"));
builder.Services.AddSingleton<IExecucaoRepository, MongoExecucaoRepository>();
builder.Services.AddHealthChecks().AddCheck<MongoHealthCheck>("mongodb");

builder.Services.AddSingleton<ExecucaoMetrics>();

// -- Mensageria (MassTransit 8 + RabbitMQ), conforme docs/fase4/contratos-mensageria.md
builder.Services.AddMassTransit(x =>
{
    x.SetKebabCaseEndpointNameFormatter();
    x.AddConsumer<IniciarExecucaoConsumer>();
    x.AddConsumer<CancelarExecucaoConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(config["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(config["RabbitMq:Username"] ?? "oficina");
            h.Password(config["RabbitMq:Password"] ?? "oficina123");
        });
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(2)));
        cfg.ConfigureEndpoints(context);
    });
});

// -- JWT (mesmos parametros do OS Service)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = config["Jwt:Issuer"],
        ValidAudience = config["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            config["Jwt:SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey nao configurado."))),
        ClockSkew = TimeSpan.Zero
    });
builder.Services.AddAuthorization();

// -- Observabilidade (New Relic via OTLP) somente com license key configurada
var newRelicLicenseKey = config["NewRelic:LicenseKey"];
if (!string.IsNullOrWhiteSpace(newRelicLicenseKey))
{
    var otlpEndpoint = config["NewRelic:OtlpEndpoint"] ?? "https://otlp.nr-data.net:4317";

    void ConfigurarOtlp(OpenTelemetry.Exporter.OtlpExporterOptions otlp)
    {
        otlp.Endpoint = new Uri(otlpEndpoint);
        otlp.Headers = $"api-key={newRelicLicenseKey}";
    }

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName));
        logging.IncludeScopes = true;
        logging.IncludeFormattedMessage = true;
        logging.AddOtlpExporter(ConfigurarOtlp);
    });

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(ServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource("MassTransit")
            .AddOtlpExporter(ConfigurarOtlp))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(ExecucaoMetrics.MeterName, "MassTransit")
            .AddOtlpExporter(ConfigurarOtlp));
}

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Oficina Mecânica - Execution Service",
        Version = "v1",
        Description = "Fila de execução e acompanhamento da produção das ordens de serviço. Requer JWT."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Informe o token JWT emitido pelo OS Service"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });

    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();

// Swagger sob /execucao para nao colidir com os outros servicos no Ingress
app.UseSwagger(c => c.RouteTemplate = "execucao/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.RoutePrefix = "execucao/swagger";
    c.SwaggerEndpoint("/execucao/swagger/v1/swagger.json", "Execution Service v1");
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Necessario para WebApplicationFactory nos testes de integracao
public partial class Program { }
