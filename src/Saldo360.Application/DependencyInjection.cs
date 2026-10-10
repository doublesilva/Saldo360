using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Families;

namespace Saldo360.Application;

public static class DependencyInjection
{
    /// <summary>Registra os serviços de aplicação (casos de uso).</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<FamilyService>();
        services.AddScoped<FamilyInvitationService>();
        return services;
    }
}
