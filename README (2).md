# AccountCore Parser API

AccountCore Parser API is a .NET 8 Web API for processing uploaded bank statement PDFs and
storing the parsed transactions in MongoDB Atlas.

## Project goals

- Accept bank statement PDFs from clients.
- Parse the contents into structured transaction data.
- Persist the results to MongoDB for later accounting workflows.

## Technologies

- ASP.NET Core 8
- MongoDB (via Atlas)
- RESTful JSON API

## Project structure

- **AccountCore.API** – ASP.NET Core Web API project that exposes endpoints for uploading and parsing bank statements.
- **AccountCore.Services.Parser** – contains the business logic for parsing PDFs and coordinating persistence operations.
- **AccountCore.DAL.Parser** – data access layer responsible for reading from and writing to MongoDB.
- **AccountCore.DTO.Parser** – shared data transfer objects and configuration settings used across projects.

### Data flow

1. A controller in **AccountCore.API** receives the request.
2. The controller invokes the appropriate service in **AccountCore.Services.Parser**.
3. The service calls **AccountCore.DAL.Parser** to persist or retrieve data.
4. **AccountCore.DAL.Parser** interacts with MongoDB to complete the operation.

```
Controller → Service → DAL → MongoDB
```

## API endpoints

### `POST /api/parser/parse`

Uploads a bank statement PDF and returns the parsed data.

#### Sample request

```bash
curl -X POST http://localhost:5233/api/parser/parse \
  -F bank=MyBank \
  -F userId=123 \
  -F file=@statement.pdf
```

### `GET /api/parser/usage`

Returns the number of parsed statements for the user during the current billing window and remaining credits.

#### Sample request

```bash
curl "http://localhost:5233/api/parser/usage?userId=123"
```

#### Sample response

```json
{
  "count": 3,
  "remaining": 2
}
```

## Environment setup

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download).
2. Configure MongoDB:
   - Provide `MongoDB:ConnectionString` and `MongoDB:Database` in
     `appsettings.json`, `appsettings.Development.json`, or environment
     variables (`MongoDB__ConnectionString`, `MongoDB__Database`).
   - Example:

     ```json
     {
       "MongoDB": {
         "ConnectionString": "<your-connection-string>",
         "Database": "Contable_DEV"
       }
     }
     ```
3. Configure usage limits:
   - Provide `Usage:MonthlyLimit` in your configuration or as an environment
     variable (`Usage__MonthlyLimit`).
   - Example:

     ```json
     {
       "Usage": {
         "MonthlyLimit": 5
       }
     }
     ```
4. Run the API:

   ```bash
   dotnet run
   ```

   The application listens on `http://localhost:5233` by default.

## Running tests

Execute the unit tests with:

```bash
dotnet test
```

## Contributing

1. Fork the repository and create a feature branch.
2. Make your changes and ensure `dotnet test` passes.
3. Submit a pull request describing your improvements.

