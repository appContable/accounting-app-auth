using Xunit;
using FluentAssertions;
using AccountCore.Services.Parser.Parsers;

namespace AccountCore.Tests.Unit.Parsers
{
    public class GaliciaStatementParserTests
    {
        private readonly GaliciaStatementParser _parser;

        public GaliciaStatementParserTests()
        {
            _parser = new GaliciaStatementParser();
        }

        [Fact]
        public void Parse_EmptyText_ReturnsEmptyResult()
        {
            // Arrange
            var text = string.Empty;

            // Act
            var result = _parser.Parse(text);

            // Assert
            result.Should().NotBeNull();
            result.Statement.Should().NotBeNull();
            result.Statement.Bank.Should().Be("Banco Galicia");
            result.Statement.Accounts.Should().BeEmpty();
        }

        [Fact]
        public void Parse_ValidMovement_ParsesCorrectly()
        {
            // Arrange
            var text = @"
Movimientos
15/01/2024 PAGO TARJETA VISA 1.500,00 10.000,00
";

            // Act
            var result = _parser.Parse(text);

            // Assert
            result.Should().NotBeNull();
            result.Statement.Accounts.Should().HaveCount(1);
            
            var transactions = result.Statement.Accounts[0].Transactions;
            transactions.Should().HaveCount(1);
            
            var transaction = transactions[0];
            transaction.Date.Should().Be(new DateTime(2024, 1, 15));
            transaction.Description.Should().Contain("PAGO TARJETA VISA");
            transaction.Amount.Should().Be(-1500.00m);
            transaction.Balance.Should().Be(10000.00m);
            transaction.Type.Should().Be("debit");
        }
    }
}