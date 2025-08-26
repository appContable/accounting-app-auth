using AutoMapper;
using AccountCore.DTO.Auth.IServices;
using AccountCore.Services.Auth;
using AccountCore.Services.Auth.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using AccountCore.DTO.Parser.Settings;
using AccountCore.Services.Parser;
using AccountCore.Services.Parser.Interfaces;
using AccountCore.Services.Parser.Repositories;
using System.Text;
using System.Text.Json.Serialization;
using AccountCore.API.Auth;
using AccountCore.API.Helpers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(x =>
    x.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.OperationFilter<FileUploadOperationFilter>();
    o.EnableAnnotations();
});

builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<UsageSettings>(builder.Configuration.GetSection("Usage"));
//builder.Services.Configure<BankRulesSettings>(builder.Configuration.GetSection("BankRules"));

var connectionString = builder.Configuration["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
var mongoSettings = MongoClientSettings.FromConnectionString(connectionString);

builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(mongoSettings));
builder.Services.AddScoped<IMongoDatabase>(sp =>
{
    var client = sp.GetRequiredService<IMongoClient>();
    var dbName = builder.Configuration["MongoDB:Database"] ?? "parserdb";
    return client.GetDatabase(dbName);
});

builder.Services.AddScoped<IParseUsageRepository, ParseUsageRepository>();
builder.Services.AddScoped<IUserCategoryRuleRepository, UserCategoryRuleRepository>();
builder.Services.AddScoped<IBankCategoryRuleRepository, BankCategoryRuleRepository>();
builder.Services.AddScoped<IBankRulesProvider, MongoBankRulesProvider>();
builder.Services.AddScoped<ICategorizationService, CategorizationService>();
builder.Services.AddScoped<IPdfParsingService, PdfParserService>();

Microsoft.Extensions.Configuration.ConfigurationManager configuration = builder.Configuration;

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMongoDB(configuration["MongoDB:ConnectionString"],
                      configuration["MongoDB:Database"]));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(configuration["JWT:Secret"]))
    };
});

builder.Services.AddCors(p => p.AddPolicy("corsapp", policy =>
{
    policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
}));

var config = new MapperConfiguration(cfg =>
{
    cfg.AddProfile(new MappingProfile());
});
var mapper = config.CreateMapper();
builder.Services.AddSingleton(mapper);

var app = builder.Build();

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

