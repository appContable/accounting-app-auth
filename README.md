# AccountCore

## Resumen del proyecto

AccountCore es un backend en **.NET 8** que combina servicios de autenticación y de parseo/categorización de extractos bancarios en PDF.

### Submódulos

- `AccountCore.API`: punto de entrada Web API.
- `AccountCore.Services.Auth`: lógica de autenticación, generación de JWT y recuperación de contraseñas.
- `AccountCore.Services.Parser`: parseo de PDFs, categorización y métricas de uso.
- `AccountCore.DAL.Auth`: repositorios y acceso a datos para usuarios.
- `AccountCore.DAL.Parser`: persistencia de reglas y registros de uso.
- `AccountCore.DTO.Auth` y `AccountCore.DTO.Parser`: objetos de transferencia utilizados por los servicios y la API.

## Requisitos previos

- [.NET SDK 8](https://dotnet.microsoft.com/download)
- Instancia de MongoDB accesible
- Opcional: variables de entorno para configurar la aplicación

## Compilación y ejecución

```bash
dotnet restore
dotnet build AccountCore.sln
dotnet run --project AccountCore.API/AccountCore.API.csproj
```

## Security Configuration

La API utiliza **JWT Bearer** para autenticación. Los roles admitidos son `admin`, `operations` y `monitor`. El secreto de firma se define en `appsettings.json`:

```json
"JWT": {
  "Secret": "<clave-super-secreta>"
}
```

También puede establecerse mediante la variable de entorno `JWT__Secret`.

El middleware CORS aplica la política `corsapp`, que permite cualquier origen, método y encabezado. Ajuste esta política en `Program.cs` según los requerimientos de despliegue.

Para habilitar HTTPS, defina `HttpsPort` en la configuración; el middleware `UseHttpsRedirection` se activa solo cuando este valor está presente.

## API Endpoints

### Auth

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| POST | `/api/Auth/authentication` | Autentica usuario y retorna JWT + refresh token | - | `AuthenticationDTO` (email o CUIT, password) | Ninguna |
| POST | `/api/Auth/SetNewPassword/{userId}/{codeBase64}` | Confirma restablecimiento de contraseña | `userId`, `codeBase64` | `SetPasswordDTO` | Ninguna |
| POST | `/api/Auth/ResetPassword` | Envía instrucciones de reseteo por email | - | `ResetPasswordRequest` (email) | Ninguna |
| POST | `/api/Auth/refresh-token` | Genera un nuevo JWT usando un refresh token válido | - | `TokenModelDTO` (token, refreshToken) | Ninguna |

### Parser

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| POST | `/api/Parser/parse` | Parsea PDF y aplica reglas de categorización | - | `multipart/form-data` con `file`, `bank` | **JWT Bearer** |
| GET | `/api/Parser/usage` | Devuelve parseos usados y restantes del mes | - | - | **JWT Bearer** |

### Rules

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| GET | `/api/Rules` | Lista reglas del usuario para un banco | `bank`, `onlyActive` (query) | - | **JWT Bearer** |
| POST | `/api/Rules/learn` | Aprende o actualiza una regla | - | `LearnRuleRequest` (bank, pattern, category) | **JWT Bearer** |
| PATCH | `/api/Rules/{id}/deactivate` | Desactiva una regla existente | `id` (path), `bank` (query) | - | **JWT Bearer** |

### User

Todas las rutas requieren JWT con rol `admin`, salvo creación de usuario.

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| GET | `/api/User` | Obtiene usuario por id | `id` (query) | - | `admin` |
| GET | `/api/User/Find` | Busca usuarios por texto | `value` (query) | - | `admin` |
| POST | `/api/User` | Crea un nuevo usuario | - | `UserPostDTO` | Ninguna |
| PUT | `/api/User/{userId}` | Actualiza usuario existente | `userId` (path) | `UserPostDTO` | `admin` |
| DELETE | `/api/User/{userId}` | Elimina usuario | `userId` (path) | - | `admin` |
| PATCH | `/api/User/Enable/{userId}` | Habilita cuenta de usuario | `userId` (path) | - | `admin` |
| PATCH | `/api/User/Disable/{userId}` | Deshabilita cuenta de usuario | `userId` (path) | - | `admin` |

### Test (Solo en desarrollo)

Endpoints disponibles únicamente cuando `Testing:EnableTestEndpoints` está habilitado.

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| POST | `/api/Test/parse-pdf` | Parsea PDF sin autenticación (testing) | - | `multipart/form-data` con `file`, `bank` | Ninguna |
| POST | `/api/Test/test-categorization` | Prueba categorización con datos de ejemplo | - | `TestCategorizationRequest` | Ninguna |
| GET | `/api/Test/health` | Health check del servicio | - | - | Ninguna |

### Version

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| GET | `/api/Version` | Información detallada de versión de la API | - | - | Ninguna |
| GET | `/api/Version/simple` | Información básica de versión | - | - | Ninguna |

## Contratos de Datos (DTOs)

### Auth DTOs

**AuthenticationDTO** (la clave `email` acepta email o CUIT)
```json
{
  "email": "20123456789",
  "password": "contraseña123"
}
```

**SetPasswordDTO**
```json
{
  "password": "nuevaContraseña123",
  "confirmPassword": "nuevaContraseña123"
}
```

**TokenModelDTO**
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "base64RefreshToken..."
}
```

**ReturnTokenDTO** (Respuesta de autenticación)
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expire": "2024-01-15T15:30:00Z",
  "roles": ["admin", "user"],
  "loginId": "user-id-123",
  "fullName": "Juan Pérez",
  "refreshToken": "base64RefreshToken..."
}
```

### User DTOs

**UserPostDTO** (Crear/Actualizar usuario)
```json
{
  "firstName": "Juan",
  "lastName": "Pérez",
  "email": "juan.perez@ejemplo.com",
  "roleIds": ["role-id-1", "role-id-2"]
}
```

**UserDTO** (Respuesta de usuario)
```json
{
  "id": "user-id-123",
  "firstName": "Juan",
  "lastName": "Pérez",
  "email": "juan.perez@ejemplo.com",
  "isLock": false,
  "isActive": true,
  "creationDate": "2024-01-15T10:00:00Z",
  "roles": [
    {
      "roleId": "role-id-1",
      "roleKey": "admin",
      "roleName": "Administrador",
      "creationDate": "2024-01-15T10:00:00Z"
    }
  ]
}
```

### Parser DTOs

**UploadPdfRequest** (multipart/form-data)
```
bank: "galicia" | "supervielle"
file: [archivo PDF]
```

**LearnRuleRequest**
```json
{
  "bank": "galicia",
  "pattern": "PAGO TARJETA",
  "category": "Gastos",
  "subcategory": "Tarjetas",
  "patternType": "Contains",
  "priority": 100
}
```

**ParseResult** (Respuesta de parseo)
```json
{
  "statement": {
    "bank": "Banco Galicia",
    "periodStart": "2024-01-01T00:00:00Z",
    "periodEnd": "2024-01-31T23:59:59Z",
    "accounts": [
      {
        "accountNumber": "22-04584827/3 (ARS)",
        "currency": "ARS",
        "openingBalance": 10000.00,
        "closingBalance": 8500.00,
        "transactions": [
          {
            "date": "2024-01-15T00:00:00Z",
            "description": "PAGO TARJETA VISA",
            "originalDescription": "PAGO TARJETAVISA RESUMEN",
            "amount": -1500.00,
            "type": "debit",
            "balance": 8500.00,
            "category": "Gastos",
            "subcategory": "Tarjetas",
            "categorySource": "BankRule",
            "categoryRuleId": "rule-guid-123"
          }
        ]
      }
    ]
  },
  "warnings": [
    "[diag] lines=150, parsed=25, range=2024-01-01->2024-01-31"
  ]
}
```

**Usage Response**
```json
{
  "count": 15,
  "remaining": 85
}
```

### Test DTOs

**TestCategorizationRequest**
```json
{
  "bank": "galicia",
  "description": "PAGO TARJETA VISA",
  "amount": -1500.00
}
```

### Version DTOs

**Version Response**
```json
{
  "version": "1.0.2",
  "buildDate": "2024-01-15T10:30:00Z",
  "buildNumber": "2",
  "assemblyVersion": "1.0.2.0",
  "environment": "Development",
  "framework": "8.0.17",
  "machineName": "DEV-MACHINE",
  "timestamp": "2024-01-15T15:45:30Z"
}
```

**Simple Version Response**
```json
{
  "version": "1.0.2",
  "buildDate": "2024-01-15T10:30:00Z"
}
```

## Códigos de Estado HTTP

### Respuestas Exitosas
- `200 OK` - Operación exitosa
- `204 No Content` - Operación exitosa sin contenido de respuesta

### Errores del Cliente
- `400 Bad Request` - Datos inválidos o faltantes
- `401 Unauthorized` - Token JWT inválido o expirado
- `403 Forbidden` - Permisos insuficientes
- `404 Not Found` - Recurso no encontrado
- `429 Too Many Requests` - Límite de uso excedido

### Errores del Servidor
- `500 Internal Server Error` - Error interno del servidor

## Bancos Soportados

El sistema actualmente soporta parseo de extractos PDF de los siguientes bancos:

- **galicia** - Banco Galicia
- **supervielle** - Banco Supervielle

Cada banco tiene su propio parser especializado que maneja el formato específico de sus extractos PDF.

## Configuration & Environment Variables

| Clave | Descripción | Valor por defecto |
|------|-------------|------------------|
| `MongoDB:ConnectionString` | Cadena de conexión a MongoDB | `mongodb://localhost:27017` |
| `MongoDB:Database` | Base de datos para reglas y uso | `parserdb` |
| `JWT:Secret` | Clave para firmar tokens JWT | `dev-secret-change-me` |
| `Usage:MonthlyLimit` | Límite mensual de parseos por usuario | `0` (sin límite) |
| `HttpsPort` | Puerto para redirección HTTPS | no definido |

Todas las claves pueden definirse como variables de entorno usando el formato `Seccion__Subclave` (p. ej. `MongoDB__ConnectionString`).

