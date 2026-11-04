using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

public class ApiTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (typeof(Program).Assembly.GetName().Name == "Relay.AdminApi")
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["StaffAuthentication:Issuer"] = "https://staff.example.test/pool",
                    ["StaffAuthentication:ClientId"] = "test-client"
                }));
    }
}
