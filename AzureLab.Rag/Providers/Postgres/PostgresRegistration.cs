using AzureLab.Rag.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AzureLab.Rag.Providers.Postgres;

internal static class PostgresRegistration
{
    public static IServiceCollection AddPostgresVectorStore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("VectorDb");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "ConnectionStrings:VectorDb is required when Ai:VectorStore:Provider=Postgres.");

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();            // maps the pgvector type
            return builder.Build();
        });
        services.AddSingleton<IDocumentVectorStore, PostgresDocumentVectorStore>();
        return services;
    }
}