using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Cosmos;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // Register CosmosClient as a singleton
        services.AddSingleton<CosmosClient>(sp =>
        {
            var connectionString = Environment.GetEnvironmentVariable("CosmosDBConnection");
            return new CosmosClient(connectionString);
        });
        
        // Register other services if needed
    })
    .Build();

host.Run();