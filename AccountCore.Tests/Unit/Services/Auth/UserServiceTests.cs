using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using AutoMapper;
using AccountCore.Services.Auth.Services;
using AccountCore.Services.Auth.Interfaces;
using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DAL.Auth.Models;
using System.Security.Claims;

namespace AccountCore.Tests.Unit.Services.Auth
{
    public class UserServiceTests
    {
        private readonly Mock<ILogger<UserService>> _loggerMock;
        private readonly Mock<IMapper> _mapperMock;
        private readonly Mock<IUserRepository> _userRepositoryMock;
        private readonly Mock<IRoleRepository> _roleRepositoryMock;
        private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
        private readonly UserService _userService;

        public UserServiceTests()
        {
            _loggerMock = new Mock<ILogger<UserService>>();
            _mapperMock = new Mock<IMapper>();
            _userRepositoryMock = new Mock<IUserRepository>();
            _roleRepositoryMock = new Mock<IRoleRepository>();
            _httpContextAccessorMock = new Mock<IHttpContextAccessor>();

            _userService = new UserService(
                _loggerMock.Object,
                _mapperMock.Object,
                _userRepositoryMock.Object,
                _roleRepositoryMock.Object,
                _httpContextAccessorMock.Object);
        }

        [Fact]
        public async Task Create_ValidUser_ReturnsSuccess()
        {
            // Arrange
            var userDto = new UserPostDTO
            {
                FirstName = "John",
                LastName = "Doe",
                Email = "john.doe@example.com",
                RoleIds = new[] { "role1" }
            };

            var role = new Role { Id = "role1", RoleKey = "user", Name = "User" };
            var createdUser = new User { Id = "user1", Email = userDto.Email };
            var expectedDto = new UserDTO { Id = "user1", Email = userDto.Email };

            _userRepositoryMock.Setup(x => x.EmailExistsAsync(userDto.Email, null))
                .ReturnsAsync(false);
            _roleRepositoryMock.Setup(x => x.GetRolesByIdsAsync(It.IsAny<IEnumerable<string>>()))
                .ReturnsAsync(new[] { role });
            _userRepositoryMock.Setup(x => x.CreateAsync(It.IsAny<User>()))
                .ReturnsAsync(createdUser);
            _mapperMock.Setup(x => x.Map<UserDTO>(createdUser))
                .Returns(expectedDto);

            // Act
            var result = await _userService.Create(userDto);

            // Assert
            result.Success.Should().BeTrue();
            result.Value.Should().NotBeNull();
            result.Value!.Email.Should().Be(userDto.Email);
        }

        [Fact]
        public async Task Create_DuplicateEmail_ReturnsError()
        {
            // Arrange
            var userDto = new UserPostDTO
            {
                FirstName = "John",
                LastName = "Doe",
                Email = "existing@example.com",
                RoleIds = new[] { "role1" }
            };

            _userRepositoryMock.Setup(x => x.EmailExistsAsync(userDto.Email, null))
                .ReturnsAsync(true);

            // Act
            var result = await _userService.Create(userDto);

            // Assert
            result.Success.Should().BeFalse();
            result.Errors.Should().NotBeNull();
            result.Errors!.Should().Contain(e => e.Key == "EmailExists");
        }

        [Fact]
        public async Task GetById_ExistingUser_ReturnsUser()
        {
            // Arrange
            var userId = "user1";
            var user = new User { Id = userId, Email = "test@example.com" };
            var expectedDto = new UserDTO { Id = userId, Email = "test@example.com" };

            _userRepositoryMock.Setup(x => x.GetByIdAsync(userId))
                .ReturnsAsync(user);
            _mapperMock.Setup(x => x.Map<UserDTO>(user))
                .Returns(expectedDto);

            // Act
            var result = await _userService.GetById(userId);

            // Assert
            result.Success.Should().BeTrue();
            result.Value.Should().NotBeNull();
            result.Value!.Id.Should().Be(userId);
        }
    }
}