using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AccountCore.DAL.Auth.Models;
using AccountCore.DTO.Auth.Configuration;
using AccountCore.DTO.Auth.IServices.Result;
using AccountCore.Services.Auth.Errors;
using AccountCore.Services.Auth.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AccountCore.Services.Auth.Services
{
    public class EnvialoSimpleEmailService : IEmailService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<EnvialoSimpleEmailService> _logger;
        private readonly EmailSettings _settings;

        public EnvialoSimpleEmailService(
            HttpClient httpClient,
            ILogger<EnvialoSimpleEmailService> logger,
            IOptions<EmailSettings> emailOptions)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = emailOptions.Value;

            // Base url EXACTA del servicio
            // POST https://api.envialosimple.email/api/v1/mail/send
            var baseUrl = string.IsNullOrWhiteSpace(_settings.BaseUrl)
                ? "https://api.envialosimple.email/"
                : _settings.BaseUrl.TrimEnd('/') + "/";

            _httpClient.BaseAddress = new Uri(baseUrl);

            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            }
        }

        public async Task<ServiceResult<bool>> SendWelcomeEmailAsync(User user, string activationLink)
        {
            var subject = string.IsNullOrWhiteSpace(_settings.WelcomeSubject)
                ? "Bienvenido"
                : _settings.WelcomeSubject;

            var body = BuildWelcomeBody(user, activationLink);

            return await SendAsync(
                user,
                subject,
                body,
                _settings.WelcomeTemplateId,
                activationLink);
        }

        public async Task<ServiceResult<bool>> SendResetPasswordEmailAsync(User user, string resetLink)
        {
            var subject = string.IsNullOrWhiteSpace(_settings.ResetPasswordSubject)
                ? "Restablecé tu contraseña"
                : _settings.ResetPasswordSubject;

            var body = BuildResetPasswordBody(user, resetLink);

            return await SendAsync(
                user,
                subject,
                body,
                _settings.ResetPasswordTemplateId,
                resetLink);
        }

        private async Task<ServiceResult<bool>> SendAsync(
            User user,
            string subject,
            string htmlBody,
            string? templateId,
            string actionLink)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.ApiKey))
                {
                    return ServiceResult<bool>.Error(
                        ErrorsKey.InternalErrorCode,
                        "Email API key is not configured");
                }

                if (string.IsNullOrWhiteSpace(_settings.FromEmail))
                {
                    return ServiceResult<bool>.Error(
                        ErrorsKey.InternalErrorCode,
                        "FromEmail is not configured");
                }

                // "from" y "to" usando los formatos que la doc muestra
                // https://api.envialosimple.email/api/v1/mail/send
                var fromValue = string.IsNullOrWhiteSpace(_settings.FromName)
                    ? _settings.FromEmail
                    : $"{_settings.FromName} <{_settings.FromEmail}>";

                var toValue = string.IsNullOrWhiteSpace(user.FirstName) && string.IsNullOrWhiteSpace(user.LastName)
                    ? user.Email
                    : $"{user.FirstName} {user.LastName} <{user.Email}>";

                // Armamos el payload siguiendo la doc:
                // from, to, subject, html (o templateID), context/substitutions
                var requestBody = new Dictionary<string, object?>
                {
                    ["from"] = fromValue,
                    ["to"] = toValue,
                    ["subject"] = subject
                };

                if (!string.IsNullOrWhiteSpace(templateId))
                {
                    // Usar plantilla
                    requestBody["templateID"] = templateId;
                }
                else
                {
                    // Usar HTML directo
                    requestBody["html"] = htmlBody;
                }

                // Variables para reemplazar en asunto/html/plantilla
                // Usamos "context" (recomendado por la doc)
                requestBody["context"] = new
                {
                    firstName = user.FirstName,
                    lastName = user.LastName,
                    cuit = user.Cuit,
                    action_url = actionLink
                };

                var json = JsonSerializer.Serialize(requestBody);
                using var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var response = await _httpClient.PostAsync("api/v1/mail/send", content);

                if (response.IsSuccessStatusCode)
                {
                    return ServiceResult<bool>.Ok(true);
                }

                var responseBody = await response.Content.ReadAsStringAsync();

                _logger.LogError(
                    "EnvialoSimple send failed. Status: {StatusCode}. Body: {Body}",
                    response.StatusCode,
                    responseBody);

                // Opcional: intentar parsear el código de error de EnvialoSimple
                // para devolver algo más amigable
                return ServiceResult<bool>.Error(
                    ErrorsKey.InternalErrorCode,
                    "Failed to send transactional email");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnvialoSimpleEmailService.SendAsync");
                return ServiceResult<bool>.Error(
                    ErrorsKey.InternalErrorCode,
                    ex.Message);
            }
        }

        private static string BuildWelcomeBody(User user, string activationLink)
        {
            return $"<p>Hola {user.FirstName} {user.LastName},</p>" +
                   "<p>Te damos la bienvenida. Usá el siguiente enlace para crear tu contraseña:</p>" +
                   $"<p><a href='{activationLink}'>Crear contraseña</a></p>" +
                   "<p>Si no solicitaste este acceso, ignorá este mensaje.</p>";
        }

        private static string BuildResetPasswordBody(User user, string resetLink)
        {
            return $"<p>Hola {user.FirstName} {user.LastName},</p>" +
                   "<p>Recibimos un pedido para restablecer tu contraseña.</p>" +
                   $"<p>Podés hacerlo con el siguiente enlace: <a href='{resetLink}'>Restablecer contraseña</a></p>" +
                   "<p>Si no solicitaste el cambio, ignorá este correo.</p>";
        }
    }
}
