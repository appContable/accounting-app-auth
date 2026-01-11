---
description: Repository Information Overview
alwaysApply: true
---

# AccountCore Information

## Summary
AccountCore is a .NET 8.0-based backend system designed for financial statement processing and authentication management. It features a robust PDF parsing engine for various Argentine banks (Bbva, Galicia, Santander, Supervielle) and a categorization system for financial transactions. The system uses MongoDB for data persistence and JWT for secure authentication.

## Structure
The repository follows a clean architecture approach, separating concerns into specialized layers:
- **AccountCore.API**: The main entry point, containing ASP.NET Core Controllers, Middlewares, and configuration.
- **AccountCore.Services**: Contains business logic, including `Parser` (PDF extraction) and `Auth` (user management) services.
- **AccountCore.DAL**: Data Access Layer providing models and repositories for MongoDB interaction.
- **AccountCore.DTO**: Data Transfer Objects used for communication between layers and external consumers.

## Language & Runtime
**Language**: C#  
**Version**: .NET 8.0  
**Build System**: MSBuild / dotnet CLI  
**Package Manager**: NuGet (with Central Package Management)

## Dependencies
**Main Dependencies**:
- **Microsoft.AspNetCore.App**: Framework for Web API.
- **MongoDB.Driver (3.4.3)**: Official driver for MongoDB connectivity.
- **UglyToad.PdfPig (1.7.0-custom-5)**: Library for PDF text extraction.
- **AutoMapper (14.0.0)**: Object-to-object mapping.
- **Microsoft.AspNetCore.Authentication.JwtBearer (8.0.17)**: JWT-based authentication.
- **Swashbuckle.AspNetCore (9.0.3)**: OpenAPI/Swagger documentation.

## Build & Installation
```bash
# Restore dependencies
dotnet restore

# Build the solution
dotnet build

# Run the API
dotnet run --project AccountCore.API
```

## Main Files & Resources
- **AccountCore.API/Program.cs**: Application bootstrap, DI container configuration, and middleware pipeline.
- **AccountCore.API/appsettings.json**: Main configuration file (MongoDB connection, JWT secrets, Email API keys).
- **Directory.Packages.props**: Centralized NuGet package version management.
- **AccountCore.Services/Parser/PdfParserService.cs**: Core logic for PDF processing.
- **AccountCore.API/Controllers/TestController.cs**: Diagnostic endpoints for manual verification of parsing and categorization.

## Testing
**Framework**: None (No automated test projects detected).
**Testing Approach**: Manual verification via dedicated API endpoints.
- **Endpoints**: `api/Test/parse-pdf`, `api/Test/test-categorization`, `api/Test/health`.
- **Configuration**: `Testing:EnableTestEndpoints` in `appsettings.json` enables these routes in Development/Staging.

**Run Command**:
```bash
# Health check (after running the API)
curl http://localhost:5000/api/Test/health
```
