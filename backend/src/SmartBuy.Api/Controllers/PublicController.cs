using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartBuy.Infrastructure.Services;

namespace SmartBuy.Api.Controllers;

/// <summary>Endpoints anyone can use without an account.</summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public sealed class PublicController(PublicSearchService search) : ControllerBase
{
    /// <summary>Search for a product and get each supplier's price with a link to buy it at that supplier.</summary>
    [HttpGet("search")]
    public Task<SearchResponse> Search([FromQuery] string? q, CancellationToken ct) => search.SearchAsync(q ?? "", ct);

    /// <summary>The names of the products that can be searched, used for suggestions as the visitor types.</summary>
    [HttpGet("catalog")]
    public Task<IReadOnlyList<CatalogEntry>> Catalog(CancellationToken ct) => search.CatalogAsync(ct);
}
