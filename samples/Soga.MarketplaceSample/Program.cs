var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "Soga marketplace sample",
    status = "placeholder",
    message = "The realistic sample will be implemented with the focused V1 workflows."
}));

app.Run();
