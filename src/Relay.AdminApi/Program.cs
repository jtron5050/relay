using Relay.Http;
using Relay.AdminApi.Authentication;
using Amazon.Lambda.AspNetCoreServer.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);
builder.Services.AddStaffAuthentication(builder.Configuration);
builder.AddRelayHttp();

var app = builder.Build();
app.UseRelayHttp();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/openapi/v1.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json")).AllowAnonymous();
app.Run();

public partial class Program { }
