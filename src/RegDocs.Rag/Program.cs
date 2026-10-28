var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // serves /openapi/v1.json
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "regdocs-rag v1"));
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "regdocs-rag",
    utc = DateTime.UtcNow
}))
.WithName("Health");

app.Run();
