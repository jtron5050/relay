using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Relay.Core.Persistence;

namespace Relay.Infrastructure.Persistence;

public sealed class DynamoOptions
{
    public string TableName { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    // Loopback HTTP only; production uses the normal SDK credential chain and regional HTTPS.
    public string? LocalEndpoint { get; set; }
}

public static class PersistenceRegistration
{
    public static IServiceCollection AddRelayPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DynamoOptions>().Bind(configuration.GetSection("DynamoDb"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.TableName) && !string.IsNullOrWhiteSpace(o.Region),
                "DynamoDb TableName and Region are required.")
            .Validate(o => o.LocalEndpoint is null || (Uri.TryCreate(o.LocalEndpoint, UriKind.Absolute, out var uri)
                && uri.IsLoopback && uri.Scheme == "http"), "LocalEndpoint must be loopback HTTP.")
            .ValidateOnStart();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddDataProtection();
        services.TryAddSingleton<IAmazonDynamoDB>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<DynamoOptions>>().Value;
            return options.LocalEndpoint is null
                ? new AmazonDynamoDBClient(RegionEndpoint.GetBySystemName(options.Region))
                : new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"),
                    new AmazonDynamoDBConfig { ServiceURL = options.LocalEndpoint });
        });
        services.AddSingleton<IRecordStore, DynamoRecordStore>();
        return services;
    }
}
