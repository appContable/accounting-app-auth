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

## Setup

### Guid representation

MongoDB's .NET driver defaults to the legacy `CSharpLegacy` GUID format, which is
incompatible with most modern drivers. The API explicitly switches to the standard
representation so that `Guid` values are stored using binary subtype `4` and remain
compatible across services.

In `Program.cs` configure the Mongo client before registering it with the DI container:

```csharp
var connectionString = builder.Configuration["MongoDB:ConnectionString"] ?? "mongodb://localhost:27017";
var mongoSettings = MongoClientSettings.FromConnectionString(connectionString);
mongoSettings.GuidRepresentation = GuidRepresentation.Standard;
MongoDefaults.GuidRepresentation = GuidRepresentation.Standard;
builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(mongoSettings));
```

#### Migrating existing data

Collections created with the legacy representation need to be migrated. Use the
provided utility to update existing documents:

```bash
MONGO_URI="<connection-string>" MONGO_DB="<database>" dotnet run --project AccountCore.Migrations/AccountCore.Migrations.csproj
```

After migration the API will read all documents using the standard representation.

## Contributing

When adding models or repository methods that work with `Guid` fields, ensure MongoDB
uses the standard UUID representation (`GuidRepresentation.Standard`, binary subtype
`4`).

- **Models** – Decorate every `Guid` property with
  `[BsonGuidRepresentation(GuidRepresentation.Standard)]` so that values are
  serialized correctly.

  ```csharp
  using MongoDB.Bson;
  using MongoDB.Bson.Serialization.Attributes;

  public class ExampleEntity
  {
      [BsonId]
      [BsonGuidRepresentation(GuidRepresentation.Standard)]
      public Guid Id { get; set; }
  }
  ```

- **Repositories** – Configure the Mongo client to use the same representation before
  accessing collections.

  ```csharp
  var settings = MongoClientSettings.FromConnectionString(connectionString);
  settings.GuidRepresentation = GuidRepresentation.Standard;
  var client = new MongoClient(settings);
  var collection = client.GetDatabase("mydb").GetCollection<ExampleEntity>("entities");
  ```

Following these guidelines keeps all `Guid` values compatible across the application.
