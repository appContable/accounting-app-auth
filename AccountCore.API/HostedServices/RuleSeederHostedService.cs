using Microsoft.Extensions.Options;
using AccountCore.DAL.Parser.Models;
using AccountCore.DTO.Parser.Settings;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.API.HostedServices
{
    public class RuleSeederHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<RuleSeederHostedService> _logger;

        public RuleSeederHostedService(
            IServiceProvider serviceProvider,
            ILogger<RuleSeederHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IOptions<BankRulesSettings>>();
            var repo = scope.ServiceProvider.GetRequiredService<IBankCategoryRuleRepository>();
            
            var banks = settings.Value?.Banks;
            if (banks == null || banks.Count == 0) return;

            foreach (var kv in banks)
            {
                var bank = kv.Key;
                foreach (var item in kv.Value ?? Enumerable.Empty<BankRuleItem>())
                {
                    var existing = await repo.FindByBankAndPatternAsync(bank, item.Pattern, cancellationToken);
                    if (existing != null) continue;

                    var rule = new BankCategoryRule
                    {
                        Bank = bank,
                        Pattern = item.Pattern,
                        PatternType = item.PatternType,
                        Category = item.Category,
                        Subcategory = item.Subcategory,
                        Priority = item.Priority,
                        Enabled = true,
                        BuiltIn = true,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await repo.InsertAsync(rule, cancellationToken);
                    _logger.LogInformation("Seeded bank rule: {Bank} | {Pattern}", bank, item.Pattern);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
