using Microsoft.Extensions.Options;
using AccountCore.DAL.Parser.Models;
using AccountCore.DTO.Parser.Settings;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.API.HostedServices
{
    public class RuleSeederHostedService : IHostedService
    {
        private readonly IOptions<BankRulesSettings> _opt;
        private readonly IBankCategoryRuleRepository _repo;
        private readonly ILogger<RuleSeederHostedService> _logger;

        public RuleSeederHostedService(
            IOptions<BankRulesSettings> opt,
            IBankCategoryRuleRepository repo,
            ILogger<RuleSeederHostedService> logger)
        {
            _opt = opt;
            _repo = repo;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var banks = _opt.Value?.Banks;
            if (banks == null || banks.Count == 0) return;

            foreach (var kv in banks)
            {
                var bank = kv.Key;
                foreach (var item in kv.Value ?? Enumerable.Empty<BankRuleItem>())
                {
                    var existing = await _repo.FindByBankAndPatternAsync(bank, item.Pattern, cancellationToken);
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

                    await _repo.InsertAsync(rule, cancellationToken);
                    _logger.LogInformation("Seeded bank rule: {Bank} | {Pattern}", bank, item.Pattern);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
