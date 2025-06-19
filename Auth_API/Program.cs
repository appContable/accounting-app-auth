using AutoMapper;
using AuthDTO.IServices;
using AuthServices;
using AuthServices.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Auth_API.Auth;
using Microsoft.AspNetCore.Identity;
using System.ComponentModel.Design;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();


builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddAutoMapper(typeof(Program));
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddControllers().AddJsonOptions(x =>
                x.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);

Microsoft.Extensions.Configuration.ConfigurationManager configuration = builder.Configuration;

// For Entity Framework
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseMongoDB(configuration["ConnectionString"], databaseName: configuration["DatabaseId"]));

// For Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(a =>
{
    a.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    a.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(x =>
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

builder.Services.AddCors(p => p.AddPolicy("corsapp", builder =>
{
    builder.WithOrigins("*").AllowAnyMethod().AllowAnyHeader();
}));

var config = new MapperConfiguration(cfg =>
{
    cfg.AddProfile(new MappingProfile());
});

var mapper = config.CreateMapper();

builder.Services.AddSingleton(mapper);

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("corsapp");

app.UseHttpsRedirection();

app.UseAuthorization();

app.UseAuthentication();

app.MapControllers();

app.Run();
