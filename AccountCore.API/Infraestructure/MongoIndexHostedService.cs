using AccountCore.Services.Parser.Interfaces; // <-- tu namespace del repo

namespace AccountCore.API.Infraestructure
{
    public class MongoIndexHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        public MongoIndexHostedService(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public async Task StartAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUserCategoryRuleRepository>();
            await repo.EnsureIndexesAsync(ct);
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
