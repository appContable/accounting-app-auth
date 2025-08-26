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

### Legacy UUID compatibility

#### Legacy vs. standard subtypes

MongoDB recognizes two UUID binary subtypes.

* **Legacy (`CSharpLegacy`)** – stored as binary subtype `3` with a little-endian layout specific to the old .NET driver.
* **Standard** – stored as binary subtype `4` using the RFC 4122 byte order shared by modern drivers.

Mixing subtypes in the same collection results in unreadable `Guid` values across services.

#### Hotfix: enable legacy mode

If existing collections still rely on the legacy format, the API can temporarily
switch back for compatibility:

```csharp
var mongoSettings = MongoClientSettings.FromConnectionString(connectionString);
mongoSettings.GuidRepresentation = GuidRepresentation.CSharpLegacy;
MongoDefaults.GuidRepresentation = GuidRepresentation.CSharpLegacy;
BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V2;
```

Restart the service after applying this change.

#### Migrating existing data

Collections created with the legacy representation need to be migrated. Use the
provided utility to update existing documents:

```bash
MONGO_URI="<connection-string>" MONGO_DB="<database>" dotnet run --project AccountCore.Migrations/AccountCore.Migrations.csproj
```

After migration the API will read all documents using the standard representation.

#### Switch back to standard mode

Once the migration completes, revert the hotfix configuration:

```csharp
mongoSettings.GuidRepresentation = GuidRepresentation.Standard;
MongoDefaults.GuidRepresentation = GuidRepresentation.Standard;
BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
```

Redeploy the service so all new `Guid` values use the standard subtype.

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
