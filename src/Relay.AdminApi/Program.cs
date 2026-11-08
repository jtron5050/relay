using Relay.AdminApi.Audit;
using Relay.Core.Audit;
using Relay.Infrastructure.Audit;
using Relay.Infrastructure.Persistence;
using Relay.Http;
using Relay.AdminApi.Authentication;
using Amazon.Lambda.AspNetCoreServer.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.RestApi);
builder.Services.AddStaffAuthentication(builder.Configuration);
builder.AddRelayHttp();
builder.Services.AddRelayPersistence(builder.Configuration);
builder.Services.AddSingleton<IAdministrativeMutations, AdministrativeMutations>();
builder.Services.AddSingleton<IAuditHistory, AuditHistory>();

var app = builder.Build();
app.UseRelayHttp();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/openapi/v1.json", () => Results.File(Path.Combine(AppContext.BaseDirectory, "openapi.json"), "application/json")).AllowAnonymous();
app.MapAudit();
app.Run();

public partial class Program { }
