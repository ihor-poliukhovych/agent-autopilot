using Catalogue.Api.Books;
using Catalogue.Api.Loans;
using Catalogue.Api.Members;
using Catalogue.Api.Store;
using Catalogue.Api.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CatalogueStore>();
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
builder.Services.AddSingleton<TelemetryWriter>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseMiddleware<RequestLoggingMiddleware>();

app.MapBookEndpoints();
app.MapMemberEndpoints();
app.MapLoanEndpoints();

app.Run();
