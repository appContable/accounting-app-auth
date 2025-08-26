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

## Guid storage

The application stores `Guid` values in MongoDB using the *standard* UUID representation
(binary subtype `4`). Existing collections created with the legacy representation can be
converted by running the migration utility:

```bash
MONGO_URI="<connection-string>" MONGO_DB="<database>" dotnet run --project AccountCore.Migrations/AccountCore.Migrations.csproj
```

After running the migration the API continues to read all documents using the standard
representation.
