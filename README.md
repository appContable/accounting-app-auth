# AccountCore

AccountCore is a .NET 8 backend that combines authentication and PDF parsing services.
The solution file **AccountCore.sln** includes several projects:

- `AccountCore.API` – ASP.NET Core Web API entry point
- `AccountCore.Services.Auth` and `AccountCore.Services.Parser`
- `AccountCore.DAL.Auth` and `AccountCore.DAL.Parser`
- `AccountCore.DTO.Auth` and `AccountCore.DTO.Parser`

## Build

```bash
dotnet build AccountCore.sln
```

## Run

```bash
dotnet run --project AccountCore.API/AccountCore.API.csproj
```
