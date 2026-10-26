using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Soga.Authorization;
using Soga.EntityFrameworkCore.Extensions;
using Soga.Extensions;

// A realistic host: the marketplace owns its DbContext (orders, users) and Soga's tables live in the same database,
// so the host's migrations create them and one connection serves both. Each order gets its own buyer/seller thread,
// identified by the order reference, so "the conversation about order 42" is stable and never duplicated.
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication("Sample")
    .AddScheme<AuthenticationSchemeOptions, SampleAuthenticationHandler>("Sample", _ => { });
builder.Services.AddDbContext<MarketplaceDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Marketplace") ?? "Data Source=marketplace.db"));
builder.Services
    .AddSoga(options => options.Messages.MaximumTextLength = 4_000)
    .UseEntityFrameworkCore<MarketplaceDbContext>();

// Soga checks membership and roles. Who may START a conversation is the marketplace's decision, so it plugs in a policy.
// A policy can only deny: it can never grant more than Soga allows.
builder.Services.AddScoped<ISogaAuthorizationPolicy, OrderConversationPolicy>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Sample only. A real host applies EF migrations, and Soga never migrates a production database for you.
    var db = scope.ServiceProvider.GetRequiredService<MarketplaceDbContext>();
    db.Database.EnsureCreated();
    if (!db.Orders.Any())
    {
        db.Orders.Add(new Order { Id = 1, BuyerId = "buyer-1", SellerId = "seller-1", Title = "Handmade table" });
        db.SaveChanges();
    }
}

app.UseAuthentication();
app.UseAuthorization();
app.MapSoga();
app.MapHealthChecks("/health");

// The client asks the host which conversation to open for an order, then calls Soga's POST /soga/v1/conversations/direct
// with exactly that body. The host decides who may talk about an order; Soga stores and delivers the conversation.
app.MapGet("/orders/{id:int}/conversation", async (int id, ClaimsPrincipal user, MarketplaceDbContext db) =>
{
    var me = user.FindFirstValue(ClaimTypes.NameIdentifier);
    var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
    if (order is null || (me != order.BuyerId && me != order.SellerId))
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        otherUserId = me == order.BuyerId ? order.SellerId : order.BuyerId,
        externalReference = $"order:{order.Id}",
    });
}).RequireAuthorization();

app.Run();

internal sealed class Order
{
    public int Id { get; set; }
    public required string BuyerId { get; set; }
    public required string SellerId { get; set; }
    public required string Title { get; set; }
}

internal sealed class MarketplaceDbContext(DbContextOptions<MarketplaceDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
        modelBuilder.AddSoga();
    }
}

/// <summary>Trusts an X-Soga-User header. Never do this in production: use your real authentication scheme.</summary>
internal sealed class SampleAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Soga-User", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, value.ToString())], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
