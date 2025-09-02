using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using System.Text;
using System.Text.Json;
using AccountCore.DTO.Auth.Entities;
using AccountCore.DTO.Auth.ReturnsModels;
using AccountCore.API;

namespace AccountCore.Tests.Integration.Controllers
{
    public class AuthControllerTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public AuthControllerTests(WebApplicationFactory<Program> factory)
        {
            _factory = factory;
            _client = _factory.CreateClient();
        }

        [Fact]
        public async Task Authentication_InvalidCredentials_ReturnsBadRequest()
        {
            // Arrange
            var authDto = new AuthenticationDTO
            {
                Email = "nonexistent@example.com",
                Password = "wrongpassword"
            };

            var json = JsonSerializer.Serialize(authDto);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act
            var response = await _client.PostAsync("/api/Auth/authentication", content);

            // Assert
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Authentication_MissingEmail_ReturnsBadRequest()
        {
            // Arrange
            var authDto = new AuthenticationDTO
            {
                Password = "somepassword"
            };

            var json = JsonSerializer.Serialize(authDto);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act
            var response = await _client.PostAsync("/api/Auth/authentication", content);

            // Assert
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        }
    }
}