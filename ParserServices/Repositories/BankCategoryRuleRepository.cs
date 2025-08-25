using MongoDB.Bson;
using MongoDB.Driver;
using ParserDAL.Models;
using ParserServices.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ParserServices.Repositories
{
    public class BankCategoryRuleRepository : IBankCategoryRuleRepository
    {
        private readonly IMongoCollection<BankCategoryRule> _col;

        public BankCategoryRuleRepository(IMongoDatabase db)
        {
            _col = db.GetCollection<BankCategoryRule>("bank_category_rules");

            // Índice ÚNICO por (Bank, Pattern) sólo para docs donde ambos existen.
            // Evitamos $ne "" porque no está soportado en partial indexes de tu cluster.
            var uniqKeys = Builders<BankCategoryRule>.IndexKeys
                .Ascending(x => x.Bank)
                .Ascending(x => x.Pattern);

            var uniqPartial = Builders<BankCategoryRule>.Filter.And(
                Builders<BankCategoryRule>.Filter.Exists(x => x.Bank, true),
                Builders<BankCategoryRule>.Filter.Exists(x => x.Pattern, true)
            );

            try
            {
                _col.Indexes.CreateOne(new CreateIndexModel<BankCategoryRule>(
                    uniqKeys,
                    new CreateIndexOptions<BankCategoryRule>
                    {
                        Name = "uniq_bank_pattern",
                        Unique = true,
                        PartialFilterExpression = uniqPartial
                    }
                ));
            }
            catch (MongoCommandException)
            {
                // Si ya existe o hay conflicto de opciones, ignoramos para no romper el arranque.
                // (Si falla por duplicados reales con Bank+Pattern presentes, revisar datos seed).
            }

            // Índice de lectura para queries: por banco, habilitado y orden por prioridad (asc).
            var readKeys = Builders<BankCategoryRule>.IndexKeys
                .Ascending(x => x.Bank)
                .Ascending(x => x.Enabled)
                .Ascending(x => x.Priority);

            try
            {
                _col.Indexes.CreateOne(new CreateIndexModel<BankCategoryRule>(
                    readKeys,
                    new CreateIndexOptions { Name = "by_bank_enabled_priority" }
                ));
            }
            catch (MongoCommandException)
            {
                // Ignorar si ya existe.
            }
        }

        public Task<BankCategoryRule?> FindByBankAndPatternAsync(string bank, string pattern, CancellationToken ct = default)
        {
            var b = (bank ?? string.Empty).Trim();
            var p = (pattern ?? string.Empty).Trim();
            return _col.Find(x => x.Bank == b && x.Pattern == p).FirstOrDefaultAsync(ct);
        }

        public Task InsertAsync(BankCategoryRule rule, CancellationToken ct = default)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (string.IsNullOrWhiteSpace(rule.Bank))   throw new ArgumentException("Bank requerido.", nameof(rule.Bank));
            if (string.IsNullOrWhiteSpace(rule.Pattern)) throw new ArgumentException("Pattern requerido.", nameof(rule.Pattern));
            if (string.IsNullOrWhiteSpace(rule.Category)) throw new ArgumentException("Category requerida.", nameof(rule.Category));

            rule.Bank    = rule.Bank.Trim();
            rule.Pattern = rule.Pattern.Trim();
            rule.Category = rule.Category.Trim();

            return _col.InsertOneAsync(rule, cancellationToken: ct);
        }

        public async Task<IReadOnlyList<BankCategoryRule>> GetByBankAsync(string bank, CancellationToken ct = default)
        {
            var b = (bank ?? string.Empty).Trim();
            return await _col.Find(x => x.Bank == b && x.Enabled)
                             .SortBy(x => x.Priority) // menor número = mayor prioridad
                             .ToListAsync(ct);
        }
    }
}
