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
| POST | `/api/Auth/authentication` | Autentica usuario y retorna JWT + refresh token | - | `AuthenticationDTO` (email, password) | Ninguna |
| POST | `/api/Auth/SetNewPassword/{userId}/{codeBase64}` | Confirma restablecimiento de contraseña | `userId`, `codeBase64` | `SetPasswordDTO` | Ninguna |
| POST | `/api/Auth/ResetPassword` | Envía instrucciones de reseteo por email | - | campo `email` (form-data) | Ninguna |
| POST | `/api/Auth/refresh-token` | Genera un nuevo JWT usando un refresh token válido | - | `TokenModelDTO` (token, refreshToken) | Ninguna |

### Parser

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| POST | `/api/Parser/parse` | Parsea PDF y aplica reglas de categorización | - | `multipart/form-data` con `file`, `bank`, `userId` | Ninguna |
| GET | `/api/Parser/usage` | Devuelve parseos usados y restantes del mes | `userId` (query) | - | Ninguna |

### Rules

| Método | Ruta | Descripción | Parámetros | Cuerpo | Autorización |
|-------|------|-------------|------------|-------|-------------|
| GET | `/api/Rules` | Lista reglas del usuario para un banco | `userId`, `bank`, `onlyActive` (query) | - | Ninguna |
| POST | `/api/Rules/learn` | Aprende o actualiza una regla | - | `LearnRuleRequest` (userId, bank, pattern, category) | Ninguna |
| PATCH | `/api/Rules/{id}/deactivate` | Desactiva una regla existente | `id` (path), `userId`, `bank` (query) | - | Ninguna |

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

## Configuration & Environment Variables

| Clave | Descripción | Valor por defecto |
|------|-------------|------------------|
| `MongoDB:ConnectionString` | Cadena de conexión a MongoDB | `mongodb://localhost:27017` |
| `MongoDB:Database` | Base de datos para reglas y uso | `parserdb` |
| `JWT:Secret` | Clave para firmar tokens JWT | `dev-secret-change-me` |
| `Usage:MonthlyLimit` | Límite mensual de parseos por usuario | `0` (sin límite) |
| `HttpsPort` | Puerto para redirección HTTPS | no definido |

Todas las claves pueden definirse como variables de entorno usando el formato `Seccion__Subclave` (p. ej. `MongoDB__ConnectionString`).

