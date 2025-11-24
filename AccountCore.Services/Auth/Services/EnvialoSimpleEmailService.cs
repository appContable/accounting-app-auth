using System.Net.Http.Headers;
using System.Net.Http.Json;
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

        public EnvialoSimpleEmailService(HttpClient httpClient, ILogger<EnvialoSimpleEmailService> logger, IOptions<EmailSettings> emailOptions)
        {
            _httpClient = httpClient;
            _logger = logger;
            _settings = emailOptions.Value;

            if (!string.IsNullOrWhiteSpace(_settings.BaseUrl))
            {
                _httpClient.BaseAddress = new Uri(_settings.BaseUrl);
            }

            if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
            }
        }

        public async Task<ServiceResult<bool>> SendWelcomeEmailAsync(User user, string activationLink)
        {
            var subject = string.IsNullOrWhiteSpace(_settings.WelcomeSubject) ? "Bienvenido" : _settings.WelcomeSubject;
            var body = BuildWelcomeBody(user, activationLink);

            return await SendAsync(user, subject, body, _settings.WelcomeTemplateId, activationLink);
        }

        public async Task<ServiceResult<bool>> SendResetPasswordEmailAsync(User user, string resetLink)
        {
            var subject = string.IsNullOrWhiteSpace(_settings.ResetPasswordSubject)
                ? "Restablecé tu contraseña"
                : _settings.ResetPasswordSubject;

            var body = BuildResetPasswordBody(user, resetLink);

            return await SendAsync(user, subject, body, _settings.ResetPasswordTemplateId, resetLink);
        }

        private async Task<ServiceResult<bool>> SendAsync(User user, string subject, string htmlBody, string? templateId, string actionLink)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || string.IsNullOrWhiteSpace(_settings.ApiKey))
                {
                    return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, "Email settings are not configured");
                }

                var requestBody = new
                {
                    from = new { email = _settings.FromEmail, name = _settings.FromName },
                    to = new[] { new { email = user.Email, name = $"{user.FirstName} {user.LastName}".Trim() } },
                    subject,
                    html = htmlBody,
                    template_id = string.IsNullOrWhiteSpace(templateId) ? null : templateId,
                    custom_fields = new
                    {
                        firstName = user.FirstName,
                        lastName = user.LastName,
                        cuit = user.Cuit,
                        action_url = actionLink
                    }
                };

                var response = await _httpClient.PostAsJsonAsync("api/transactional/email", requestBody);
                if (response.IsSuccessStatusCode)
                {
                    return ServiceResult<bool>.Ok(true);
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "EnvialoSimple send failed with status {Status}. Response: {Body}",
                    response.StatusCode,
                    responseBody);

                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, "Failed to send transactional email");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnvialoSimpleEmailService.SendAsync");
                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, ex.Message);
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
