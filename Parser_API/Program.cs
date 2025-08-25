using MongoDB.Driver;

using ParserDTO.Settings;                 // MongoDbSettings, UsageSettings, BankRulesSettings
using ParserServices.Interfaces;          // IPdfParsingService, ICategorizationService, IBankRulesProvider, IParseUsageRepository, IUserCategoryRuleRepository, IBankCategoryRuleRepository
using ParserServices.Repositories;        // ParseUsageRepository, UserCategoryRuleRepository, BankCategoryRuleRepository
using ParserServices;                     // PdfParserService, BankRulesProvider, CategorizationService

using Parser_API.Helpers;                 // FileUploadOperationFilter
using Swashbuckle.AspNetCore.SwaggerGen;

// ⬇️ Solo si agregaste el seeder (RuleSeederHostedService.cs) en Parser_API/HostedServices
using Parser_API.HostedServices;

var builder = WebApplication.CreateBuilder(args);

// ================== CORS (Angular dev) ==================
builder.Services.AddCors(opt =>
{
    opt.AddPolicy("dev", p =>
        p.WithOrigins("http://localhost:4200", "https://localhost:4200")
         .AllowAnyHeader()
         .AllowAnyMethod()
    );
});

// ================== Options / Settings ==================
builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<UsageSettings>(builder.Configuration.GetSection("Usage"));
// Reglas built-in por banco (desde appsettings; opcional)
//builder.Services.Configure<BankRulesSettings>(builder.Configuration.GetSection("BankRules"));

// ================== Mongo ==================
builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var cs = builder.Configuration["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
    return new MongoClient(cs);
});
builder.Services.AddScoped<IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    var dbName = builder.Configuration["MongoDB:Database"] ?? "parserdb";
    return client.GetDatabase(dbName);
});

// ================== DI (Repos & Services) ==================
// Uso / tracking
builder.Services.AddScoped<IParseUsageRepository, ParseUsageRepository>();

// Reglas de categorización
builder.Services.AddScoped<IUserCategoryRuleRepository, UserCategoryRuleRepository>();
builder.Services.AddScoped<IBankCategoryRuleRepository, BankCategoryRuleRepository>(); // ⬅️ NUEVO

// Proveedor de reglas y servicio de categorización
//builder.Services.AddScoped<IBankRulesProvider, BankRulesProvider>();
builder.Services.AddScoped<IBankRulesProvider, MongoBankRulesProvider>();
builder.Services.AddScoped<ICategorizationService, CategorizationService>();

// Parser de PDF
builder.Services.AddScoped<IPdfParsingService, PdfParserService>();

// (Opcional) seeding de reglas built-in al arrancar
// Comentá esta línea si no creaste el hosted service o no querés seeding.
//builder.Services.AddHostedService<RuleSeederHostedService>();

// ================== Web API & Swagger ==================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.OperationFilter<FileUploadOperationFilter>(); // para IFormFile en Swagger
    o.EnableAnnotations();
});

// (Opcional) redirección a HTTPS si configurás el puerto en appsettings
var httpsPort = builder.Configuration.GetValue<int?>("HttpsPort");
if (httpsPort.HasValue)
{
    builder.Services.AddHttpsRedirection(options => options.HttpsPort = httpsPort.Value);
}

// ================== App ==================
var app = builder.Build();

app.UseCors("dev");            // <-- CORS debe ir antes de MapControllers

app.UseSwagger();
app.UseSwaggerUI();

if (httpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();
app.MapControllers();

app.Run();
