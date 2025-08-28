using AutoMapper;
using AccountCore.DTO.Auth.IServices;
using AccountCore.Services.Auth;
using AccountCore.Services.Auth.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using AccountCore.DTO.Parser.Settings;
using AccountCore.Services.Parser;
using AccountCore.Services.Parser.Interfaces;
using AccountCore.Services.Parser.Repositories;
using AccountCore.DAL.Parser.Models;
using System.Text;
using System.Text.Json.Serialization;
using AccountCore.API.Auth;
using AccountCore.API.Helpers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

//
// ---- Fix GUID legacy para UserCategoryRule (Mongo driver) ----
if (!BsonClassMap.IsClassMapRegistered(typeof(UserCategoryRule)))
{
    BsonClassMap.RegisterClassMap<UserCategoryRule>(cm =>
    {
        cm.AutoMap();
        cm.MapMember(x => x.Id)
          .SetSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy));
    });
}

var builder = WebApplication.CreateBuilder(args);

// Controllers + JSON
builder.Services.AddControllers().AddJsonOptions(x =>
    x.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.OperationFilter<FileUploadOperationFilter>();
    o.EnableAnnotations();
});

// AutoMapper + Servicios Auth
builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthService, AuthService>();

// Configs
builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<UsageSettings>(builder.Configuration.GetSection("Usage"));

// ---- Mongo Driver (para Parser repos) ----
var connStr = builder.Configuration["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
var mongoSettings = MongoClientSettings.FromConnectionString(connStr);

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    return new MongoClient(mongoSettings);
});

builder.Services.AddScoped<IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    var dbName = builder.Configuration["MongoDB:Database"] ?? "parserdb";
    return client.GetDatabase(dbName);
});

// ---- Repos/servicios del Parser ----
builder.Services.AddScoped<IParseUsageRepository, ParseUsageRepository>();
builder.Services.AddScoped<IUserCategoryRuleRepository, UserCategoryRuleRepository>();
builder.Services.AddScoped<IBankCategoryRuleRepository, BankCategoryRuleRepository>();
builder.Services.AddScoped<IBankRulesProvider, MongoBankRulesProvider>();
builder.Services.AddScoped<ICategorizationService, CategorizationService>();
builder.Services.AddScoped<IPdfParsingService, PdfParserService>();

// ==============================
//   Identity con EF InMemory
//   (quitamos EF-Mongo)
// ==============================
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseInMemoryDatabase("AuthDb"));

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

// JWT
var configuration = builder.Configuration;
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(x =>
{
    x.RequireHttpsMetadata = false;
    x.SaveToken = true;
    x.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.ASCII.GetBytes(configuration["JWT:Secret"] ?? "dev-secret-change-me"))
    };
});

// CORS
builder.Services.AddCors(p => p.AddPolicy("corsapp", policy =>
{
    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
}));

// Automapper singleton
var mapperConfig = new MapperConfiguration(cfg => cfg.AddProfile(new MappingProfile()));
builder.Services.AddSingleton(mapperConfig.CreateMapper());

var app = builder.Build();

// Middleware
app.UseCors("corsapp");
app.UseSwagger();
app.UseSwaggerUI();

var httpsPort = builder.Configuration.GetValue<int?>("HttpsPort");
if (httpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.Run();
