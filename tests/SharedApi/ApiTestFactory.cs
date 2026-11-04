using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class ApiTestFactory : WebApplicationFactory<Program>
{
    public ApiTestFactory()
    {
        ClientOptions.BaseAddress = new Uri("https://localhost");
        ClientOptions.AllowAutoRedirect = false;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        if (typeof(Program).Assembly.GetName().Name == "Relay.AdminApi")
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["StaffAuthentication:Issuer"] = "https://staff.example.test/pool",
                    ["StaffAuthentication:ClientId"] = "test-client"
                }));
    }
}
