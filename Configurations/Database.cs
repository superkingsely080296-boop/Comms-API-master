using FusionComms.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FusionComms.Configurations
{
    public static class DbContextConfiguration
    {
        public static IServiceCollection ConfigureDbContext(this IServiceCollection services, string connectionString) =>
            services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(connectionString));
    }
}
