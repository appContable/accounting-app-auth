using Xunit;
using Moq;
using FluentAssertions;
using AccountCore.Services.Parser;
using AccountCore.Services.Parser.Interfaces;
using AccountCore.DAL.Parser.Models;
using AccountCore.DTO.Parser.Parameters;

namespace AccountCore.Tests.Unit.Services.Parser
{
    public class CategorizationServiceTests
    {
        private readonly Mock<IUserCategoryRuleRepository> _userRepoMock;
        private readonly Mock<IBankCategoryRuleRepository> _bankRepoMock;
        private readonly CategorizationService _service;

        public CategorizationServiceTests()
        {
            _userRepoMock = new Mock<IUserCategoryRuleRepository>();
            _bankRepoMock = new Mock<IBankCategoryRuleRepository>();
            _service = new CategorizationService(_userRepoMock.Object, _bankRepoMock.Object);
        }

        [Fact]
        public async Task ApplyAsync_WithBankRules_CategorizesTransactions()
        {
            // Arrange
            var bankRules = new List<BankCategoryRule>
            {
                new() { Pattern = "PAGO TARJETA", PatternType = RulePatternType.Contains, 
                       Category = "Gastos", Subcategory = "Tarjetas", Priority = 10, Enabled = true }
            };

            var parseResult = new ParseResult
            {
                Statement = new BankStatement
                {
                    Accounts = new List<AccountStatement>
                    {
                        new() { Transactions = new List<Transaction>
                        {
                            new() { Description = "PAGO TARJETA VISA", Amount = -100 }
                        }}
                    }
                }
            };

            _bankRepoMock.Setup(x => x.GetByBankAsync("galicia", It.IsAny<CancellationToken>()))
                .ReturnsAsync(bankRules);
            _userRepoMock.Setup(x => x.GetByUserAndBankAsync("user1", "galicia", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<UserCategoryRule>());

            // Act
            await _service.ApplyAsync(parseResult, "galicia", "user1");

            // Assert
            var transaction = parseResult.Statement.Accounts[0].Transactions[0];
            transaction.Category.Should().Be("Gastos");
            transaction.Subcategory.Should().Be("Tarjetas");
            transaction.CategorySource.Should().Be("BankRule");
        }

        [Fact]
        public async Task LearnAsync_ValidRequest_CreatesUserRule()
        {
            // Arrange
            var request = new LearnRuleRequest
            {
                UserId = "user1",
                Bank = "galicia",
                Pattern = "TRANSFERENCIA",
                Category = "Transferencias",
                PatternType = "Contains"
            };

            _userRepoMock.Setup(x => x.UpsertAsync(It.IsAny<UserCategoryRule>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Act
            var result = await _service.LearnAsync(request);

            // Assert
            result.Should().NotBeNull();
            result.Pattern.Should().Be("TRANSFERENCIA");
            result.Category.Should().Be("Transferencias");
            result.UserId.Should().Be("user1");
            result.Bank.Should().Be("galicia");

            _userRepoMock.Verify(x => x.UpsertAsync(It.IsAny<UserCategoryRule>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}