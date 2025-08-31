using AccountCore.Services.Parser.Interfaces; // <-- tu namespace del repo

namespace AccountCore.API.Infraestructure
{
    public class MongoIndexHostedService : IHostedService
    {
        private readonly IServiceProvider _sp;
        public MongoIndexHostedService(IServiceProvider sp) => _sp = sp;

        public async Task StartAsync(CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUserCategoryRuleRepository>(); // <— interfaz
            await repo.EnsureIndexesAsync(ct);
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
