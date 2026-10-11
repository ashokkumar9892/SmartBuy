using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Rules;
using SmartBuy.Domain.Units;
using SmartBuy.Infrastructure.Ai;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;
using ValidationException = SmartBuy.Infrastructure.Services.ValidationException;

namespace SmartBuy.Api.Controllers;

public sealed record ItemDto(Guid Id, int LineNumber, string Description, decimal Quantity, string Unit, string? Material, string? Specifications, string? Notes,
    string Source, Guid? CanonicalProductId, string? CanonicalProductName, string? CatalogUnit, bool IsSafetyCritical, double? MatchConfidence,
    Guid? ApprovedSubstituteProductId, string? ApprovedSubstituteName, DateTime? SubstitutionApprovedAt);

public sealed record ListDto(Guid Id, string Name, bool IsApproved, DateTime? ApprovedAt, string? SourceText, IReadOnlyList<string> Assumptions, Guid Version, IReadOnlyList<ItemDto> Items);

public sealed record ProjectSummary(Guid Id, string Name, string? Description, string Industry, string Status, DateTime? RequiredByDate, DateTime CreatedAt, DateTime UpdatedAt,
    int ItemCount, int ListCount, bool HasUnapprovedList, int CartLineCount, int PurchaseOrderCount, decimal? LastBaselineCost, decimal? LastOptimizedCost);

public sealed record ProjectDetail(Guid Id, string Name, string? Description, string Industry, string Status, DateTime? RequiredByDate, string? DeliveryPostalCode,
    DateTime CreatedAt, DateTime UpdatedAt, Guid Version, IReadOnlyList<ListDto> Lists);

public sealed record ItemInput(
    [Required(ErrorMessage = "Each item needs a description."), MaxLength(500)] string Description,
    [Range(0.0001, 1_000_000, ErrorMessage = "Quantity must be greater than zero.")] decimal Quantity,
    [Required, MaxLength(20)] string Unit,
    [MaxLength(100)] string? Material,
    [MaxLength(300)] string? Specifications,
    [MaxLength(500)] string? Notes,
    Guid? CanonicalProductId,
    ListItemSource Source = ListItemSource.Manual);

public sealed record ListInput(
    [Required, MaxLength(200)] string Name,
    [MaxLength(4000)] string? SourceText,
    [MaxLength(20)] List<string>? Assumptions,
    [MaxLength(500, ErrorMessage = "A list can have at most 500 items.")] List<ItemInput>? Items);

public sealed record ProjectInput(
    [Required(ErrorMessage = "Give the project a name."), MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description,
    Industry Industry,
    DateTime? RequiredByDate,
    [MaxLength(20)] string? DeliveryPostalCode,
    ProjectStatus? Status,
    Guid? Version,
    Guid? ReuseProjectId,
    ListInput? InitialList);

public sealed record RenameListRequest([Required, MaxLength(200)] string Name);
public sealed record GenerateRequest([Required(ErrorMessage = "Describe what you need."), MinLength(3), MaxLength(4000)] string Text, Industry Industry = Industry.General);
public sealed record ResolveRequest([Required, MaxLength(500)] string Description, [MaxLength(100)] string? Material, [MaxLength(300)] string? Specifications, [MaxLength(20)] string? Unit);

public sealed record DraftItemDto(string Description, decimal Quantity, string Unit, string? Material, string? Specifications, string? Notes,
    Guid? SuggestedProductId, string? SuggestedProductName, double? Confidence, IReadOnlyList<ResolveCandidate> Candidates);

public sealed record DraftResponse(string Source, string SourceLabel, bool RequiresReview, IReadOnlyList<DraftItemDto> Items, IReadOnlyList<string> Assumptions, IReadOnlyList<string> Warnings);

public sealed record CatalogProductDto(Guid Id, string Name, string Industry, string Category, string? Brand, string? Model, string? Mpn, string? Gtin, string? Material, string BaseUnit, bool IsSafetyCritical, string? SafetyNote, IReadOnlyDictionary<string, string> Specifications);

[ApiController]
[Route("api")]
[RequireRole]
public sealed class ProjectsController(
    AppDbContext db,
    AuditService audit,
    EntitlementService entitlements,
    CatalogResolver resolver,
    AiGateway ai,
    FeatureFlagService flags) : ApiController
{
    // ---------- Projects ----------

    [HttpGet("projects")]
    public async Task<List<ProjectSummary>> List([FromQuery] string? search, [FromQuery] ProjectStatus? status, CancellationToken ct)
    {
        var query = db.PurchasingProjects.AsNoTracking();
        if (status is not null) query = query.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(term) || (p.Description != null && p.Description.ToLower().Contains(term)));
        }
        var projects = await query.OrderByDescending(p => p.UpdatedAt).Take(500)
            .Select(p => new
            {
                Project = p,
                ListCount = p.ShoppingLists.Count,
                ItemCount = p.ShoppingLists.SelectMany(l => l.Items).Count(),
                Unapproved = p.ShoppingLists.Any(l => !l.IsApproved),
            }).ToListAsync(ct);
        var ids = projects.Select(p => p.Project.Id).ToList();
        var carts = await db.CartItems.AsNoTracking().Where(c => ids.Contains(c.ProjectId)).GroupBy(c => c.ProjectId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var orders = await db.PurchaseOrders.AsNoTracking().Where(o => o.ProjectId != null && ids.Contains(o.ProjectId.Value)).GroupBy(o => o.ProjectId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var runs = (await db.ComparisonRuns.AsNoTracking().Where(r => ids.Contains(r.ProjectId)).OrderByDescending(r => r.CreatedAt).ToListAsync(ct))
            .GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.First());

        return projects.Select(p => new ProjectSummary(p.Project.Id, p.Project.Name, p.Project.Description, p.Project.Industry.ToString(), p.Project.Status.ToString(),
            p.Project.RequiredByDate, p.Project.CreatedAt, p.Project.UpdatedAt, p.ItemCount, p.ListCount, p.Unapproved,
            carts.GetValueOrDefault(p.Project.Id), orders.GetValueOrDefault(p.Project.Id),
            runs.TryGetValue(p.Project.Id, out var run) ? run.BaselineCost : null, runs.TryGetValue(p.Project.Id, out run) ? run.OptimizedCost : null)).ToList();
    }

    [HttpGet("projects/{id:guid}")]
    public async Task<ProjectDetail> Get(Guid id, CancellationToken ct) => await LoadDetailAsync(id, ct);

    [HttpPost("projects"), RequireRole(OrgRole.Employee)]
    public async Task<ActionResult<ProjectDetail>> Create(ProjectInput request, CancellationToken ct)
    {
        await entitlements.RequireProjectQuotaAsync(ct);
        var project = new PurchasingProject
        {
            Name = request.Name.Trim(),
            Description = Clean(request.Description),
            Industry = request.Industry,
            Status = ProjectStatus.Draft,
            RequiredByDate = NormalizeDate(request.RequiredByDate),
            DeliveryPostalCode = Clean(request.DeliveryPostalCode),
            CreatedByUserId = UserId,
        };

        if (request.ReuseProjectId is { } sourceId)
        {
            var source = await db.PurchasingProjects.AsNoTracking().Include(p => p.ShoppingLists).ThenInclude(l => l.Items).AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == sourceId, ct) ?? throw new NotFoundException("The project to reuse was not found.");
            foreach (var list in source.ShoppingLists)
            {
                // A reused list is a starting point, so it goes back to "needs review" and substitutions are not carried over.
                var copy = new ShoppingList { Name = list.Name, SourceText = $"Reused from project \"{source.Name}\"", IsApproved = false };
                foreach (var item in list.Items.OrderBy(i => i.LineNumber))
                    copy.Items.Add(new ShoppingListItem
                    {
                        LineNumber = item.LineNumber, Description = item.Description, Quantity = item.Quantity, Unit = item.Unit, Material = item.Material,
                        Specifications = item.Specifications, Notes = item.Notes, Source = ListItemSource.Reuse, CanonicalProductId = item.CanonicalProductId, MatchConfidence = item.MatchConfidence,
                    });
                project.ShoppingLists.Add(copy);
            }
        }
        if (request.InitialList is { } initial) project.ShoppingLists.Add(await BuildListAsync(initial, ct));

        db.PurchasingProjects.Add(project);
        audit.Record("project.created", nameof(PurchasingProject), project.Id, project.Name);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, await LoadDetailAsync(project.Id, ct));
    }

    [HttpPut("projects/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> Update(Guid id, ProjectInput request, CancellationToken ct)
    {
        var project = await db.PurchasingProjects.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project not found.");
        if (request.Version is { } version) db.Entry(project).Property(p => p.Version).OriginalValue = version;
        project.Name = request.Name.Trim();
        project.Description = Clean(request.Description);
        project.Industry = request.Industry;
        project.RequiredByDate = NormalizeDate(request.RequiredByDate);
        project.DeliveryPostalCode = Clean(request.DeliveryPostalCode);
        if (request.Status is { } status) project.Status = status;
        audit.Record("project.updated", nameof(PurchasingProject), project.Id);
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(id, ct);
    }

    [HttpDelete("projects/{id:guid}"), RequireRole(OrgRole.PurchasingManager)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var project = await db.PurchasingProjects.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project not found.");
        db.PurchasingProjects.Remove(project);
        audit.Record("project.deleted", nameof(PurchasingProject), project.Id, project.Name);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---------- Shopping lists ----------

    [HttpPost("projects/{projectId:guid}/lists"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> AddList(Guid projectId, ListInput request, CancellationToken ct)
    {
        if (!await db.PurchasingProjects.AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project not found.");
        var list = await BuildListAsync(request, ct);
        list.ProjectId = projectId;
        db.ShoppingLists.Add(list);
        audit.Record("list.created", nameof(ShoppingList), list.Id, list.Name);
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(projectId, ct);
    }

    [HttpPut("lists/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> RenameList(Guid id, RenameListRequest request, CancellationToken ct)
    {
        var list = await db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("List not found.");
        list.Name = request.Name.Trim();
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(list.ProjectId, ct);
    }

    [HttpDelete("lists/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> DeleteList(Guid id, CancellationToken ct)
    {
        var list = await db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("List not found.");
        db.ShoppingLists.Remove(list);
        audit.Record("list.deleted", nameof(ShoppingList), list.Id, list.Name);
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(list.ProjectId, ct);
    }

    /// <summary>A person confirms the quantities. Supplier search is blocked until this has happened.</summary>
    [HttpPost("lists/{id:guid}/approve"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> ApproveList(Guid id, CancellationToken ct)
    {
        var list = await db.ShoppingLists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("List not found.");
        if (list.Items.Count == 0) throw new BusinessRuleException("Add at least one item before approving the list.");
        list.IsApproved = true;
        list.ApprovedAt = DateTime.UtcNow;
        list.ApprovedByUserId = UserId;
        var project = await db.PurchasingProjects.FirstAsync(p => p.Id == list.ProjectId, ct);
        if (project.Status == ProjectStatus.Draft) project.Status = ProjectStatus.Active;
        audit.Record("list.approved", nameof(ShoppingList), list.Id, $"{list.Items.Count} item(s)");
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(list.ProjectId, ct);
    }

    [HttpPost("lists/{id:guid}/reopen"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> ReopenList(Guid id, CancellationToken ct)
    {
        var list = await db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("List not found.");
        list.IsApproved = false;
        list.ApprovedAt = null;
        list.ApprovedByUserId = null;
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(list.ProjectId, ct);
    }

    [HttpPost("lists/{id:guid}/items"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> AddItem(Guid id, ItemInput request, CancellationToken ct)
    {
        var list = await db.ShoppingLists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new NotFoundException("List not found.");
        if (list.Items.Count >= 500) throw new BusinessRuleException("A list can have at most 500 items.");
        var item = await BuildItemAsync(request, list.Items.Count == 0 ? 1 : list.Items.Max(i => i.LineNumber) + 1, ct);
        item.ShoppingListId = list.Id;
        db.ShoppingListItems.Add(item);
        // Machine-proposed lines always send the list back for human review.
        if (request.Source is ListItemSource.Ai or ListItemSource.Upload) list.IsApproved = false;
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(list.ProjectId, ct);
    }

    [HttpPut("items/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> UpdateItem(Guid id, ItemInput request, CancellationToken ct)
    {
        var item = await db.ShoppingListItems.Include(i => i.ShoppingList).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Item not found.");
        var unit = UnitConverter.Normalize(request.Unit) ?? throw new ValidationException($"\"{request.Unit}\" is not a recognized unit.");
        if (request.CanonicalProductId is { } productId && !await db.CanonicalProducts.AnyAsync(p => p.Id == productId, ct)) throw new ValidationException("Unknown catalog product.");
        if (item.CanonicalProductId != request.CanonicalProductId)
        {
            // A substitution was approved for a specific product; changing the product invalidates that approval.
            item.ApprovedSubstituteProductId = null;
            item.SubstitutionApprovedAt = null;
            item.SubstitutionApprovedByUserId = null;
            item.MatchConfidence = request.CanonicalProductId is null ? null : 1.0;
        }
        item.Description = request.Description.Trim();
        item.Quantity = request.Quantity;
        item.Unit = unit;
        item.Material = Clean(request.Material);
        item.Specifications = Clean(request.Specifications);
        item.Notes = Clean(request.Notes);
        item.CanonicalProductId = request.CanonicalProductId;
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(item.ShoppingList!.ProjectId, ct);
    }

    [HttpDelete("items/{id:guid}"), RequireRole(OrgRole.Employee)]
    public async Task<ProjectDetail> DeleteItem(Guid id, CancellationToken ct)
    {
        var item = await db.ShoppingListItems.Include(i => i.ShoppingList).FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Item not found.");
        db.CartItems.RemoveRange(await db.CartItems.Where(c => c.ShoppingListItemId == id).ToListAsync(ct));
        db.ShoppingListItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return await LoadDetailAsync(item.ShoppingList!.ProjectId, ct);
    }

    // ---------- Draft generation (nothing here is saved) ----------

    /// <summary>Turns a natural-language request into a draft list. The draft is returned for review, never saved or priced automatically.</summary>
    [HttpPost("material-lists/generate"), RequireRole(OrgRole.Employee), EnableRateLimiting("ai")]
    public async Task<DraftResponse> Generate(GenerateRequest request, CancellationToken ct)
    {
        if (!await flags.IsEnabledAsync("ai_material_lists", ct)) throw new BusinessRuleException("AI material lists are switched off by the platform administrator.", "feature_disabled");
        await entitlements.RequireAsync(PlanFeature.AiMaterialLists, ct);

        var response = await ai.CompleteAsync(new AiRequest(AiFeatures.MaterialList, MaterialListPrompts.System, MaterialListPrompts.BuildUser(request.Text, request.Industry), MaterialListPrompts.Schema), ct);
        DraftList? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<DraftList>(response.Text, AiJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new AiProviderException("The AI response was not valid JSON.");
        }
        var draft = DraftSanitizer.Sanitize(parsed ?? new DraftList(new(), new()), out var warnings);
        var isMock = response.Provider == "mock";
        return await ToDraftResponseAsync(draft, warnings, isMock ? "mock" : response.Provider,
            isMock ? "Demo rule-based parser (no language model configured)" : $"AI draft ({response.Provider})", ct);
    }

    /// <summary>Parses an uploaded CSV, Excel or PDF purchase list into a draft. The file is read in memory and discarded.</summary>
    [HttpPost("material-lists/parse-upload"), RequireRole(OrgRole.Employee), RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<DraftResponse> ParseUpload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new ValidationException("Choose a file to upload.");
        if (file.Length > ListFileParser.MaxBytes) throw new ValidationException("The file is larger than the 5 MB limit.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var parsed = ListFileParser.Parse(Path.GetFileName(file.FileName), buffer.ToArray());
        var draft = DraftSanitizer.Sanitize(parsed.Draft, out var warnings);
        return await ToDraftResponseAsync(draft, parsed.Warnings.Concat(warnings).ToList(), "upload", $"Imported from {parsed.FileType.ToUpperInvariant()} file", ct);
    }

    [HttpPost("material-lists/resolve")]
    public async Task<IReadOnlyList<ResolveCandidate>> Resolve(ResolveRequest request, CancellationToken ct) =>
        await resolver.ResolveAsync(request.Description, request.Material, request.Specifications, request.Unit, ct);

    // ---------- Catalog lookups ----------

    [HttpGet("catalog/products")]
    public async Task<List<CatalogProductDto>> SearchCatalog([FromQuery] string? query, [FromQuery] Industry? industry, CancellationToken ct)
    {
        var products = db.CanonicalProducts.AsNoTracking().Include(p => p.Specifications).AsQueryable();
        if (industry is not null) products = products.Where(p => p.Industry == industry);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            products = products.Where(p => p.Name.ToLower().Contains(term) || p.Category.ToLower().Contains(term) || (p.Brand != null && p.Brand.ToLower().Contains(term)) || (p.Model != null && p.Model.ToLower().Contains(term)));
        }
        var list = await products.OrderBy(p => p.Industry).ThenBy(p => p.Category).ThenBy(p => p.Name).Take(100).ToListAsync(ct);
        return list.Select(ToCatalogDto).ToList();
    }

    public static CatalogProductDto ToCatalogDto(CanonicalProduct p) => new(p.Id, p.Name, p.Industry.ToString(), p.Category, p.Brand, p.Model, p.Mpn, p.Gtin, p.Material, p.BaseUnit, p.IsSafetyCritical, p.SafetyNote,
        p.Specifications.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First().Value));

    [HttpGet("catalog/units")]
    public IEnumerable<object> Units() => UnitConverter.All.Select(u => new { u.Code, u.Label, Dimension = u.Dimension.ToString() });

    [HttpGet("catalog/industries")]
    public IEnumerable<object> Industries() => Enum.GetValues<Industry>().Select(i => new { Value = i.ToString(), Label = IndustryLabel(i) });

    public static string IndustryLabel(Industry industry) => industry switch
    {
        Industry.Hvac => "HVAC",
        Industry.HomeRenovation => "Home renovation",
        Industry.PropertyManagement => "Property management",
        Industry.General => "General business supplies",
        _ => industry.ToString()
    };

    // ---------- Helpers ----------

    private async Task<DraftResponse> ToDraftResponseAsync(DraftList draft, List<string> warnings, string source, string label, CancellationToken ct)
    {
        var items = new List<DraftItemDto>();
        foreach (var item in draft.Items)
        {
            var candidates = await resolver.ResolveAsync(item.Description, item.Material, item.Specifications, item.Unit, ct);
            var pick = CatalogResolver.Preselect(candidates);
            items.Add(new DraftItemDto(item.Description, item.Quantity, item.Unit, item.Material, item.Specifications, item.Notes, pick?.ProductId, pick?.Name, pick?.Confidence, candidates));
        }
        return new DraftResponse(source, label, true, items, draft.Assumptions, warnings);
    }

    private async Task<ShoppingList> BuildListAsync(ListInput input, CancellationToken ct)
    {
        var list = new ShoppingList
        {
            Name = input.Name.Trim(),
            SourceText = Clean(input.SourceText),
            Assumptions = input.Assumptions is { Count: > 0 } ? string.Join("\n", input.Assumptions.Select(a => DraftSanitizer.Clean(a, 500)).Where(a => a.Length > 0)) : null,
            IsApproved = false,
        };
        var n = 1;
        foreach (var item in input.Items ?? new List<ItemInput>())
            list.Items.Add(await BuildItemAsync(item, n++, ct));
        return list;
    }

    private async Task<ShoppingListItem> BuildItemAsync(ItemInput input, int lineNumber, CancellationToken ct)
    {
        var unit = UnitConverter.Normalize(input.Unit) ?? throw new ValidationException($"\"{input.Unit}\" is not a recognized unit.");
        double? confidence = null;
        if (input.CanonicalProductId is { } productId)
        {
            if (!await db.CanonicalProducts.AnyAsync(p => p.Id == productId, ct)) throw new ValidationException("Unknown catalog product.");
            var candidates = await resolver.ResolveAsync(input.Description, input.Material, input.Specifications, unit, ct, take: 50);
            // Chosen explicitly by a person when it is not what the resolver would have suggested.
            confidence = candidates.FirstOrDefault(c => c.ProductId == productId)?.Confidence ?? 1.0;
        }
        return new ShoppingListItem
        {
            LineNumber = lineNumber,
            Description = input.Description.Trim(),
            Quantity = input.Quantity,
            Unit = unit,
            Material = Clean(input.Material),
            Specifications = Clean(input.Specifications),
            Notes = Clean(input.Notes),
            Source = input.Source,
            CanonicalProductId = input.CanonicalProductId,
            MatchConfidence = confidence,
        };
    }

    private async Task<ProjectDetail> LoadDetailAsync(Guid id, CancellationToken ct)
    {
        var project = await db.PurchasingProjects.AsNoTracking()
            .Include(p => p.ShoppingLists).ThenInclude(l => l.Items).ThenInclude(i => i.CanonicalProduct)
            .Include(p => p.ShoppingLists).ThenInclude(l => l.Items).ThenInclude(i => i.ApprovedSubstituteProduct)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project not found.");

        return new ProjectDetail(project.Id, project.Name, project.Description, project.Industry.ToString(), project.Status.ToString(), project.RequiredByDate,
            project.DeliveryPostalCode, project.CreatedAt, project.UpdatedAt, project.Version,
            project.ShoppingLists.OrderBy(l => l.CreatedAt).Select(l => new ListDto(l.Id, l.Name, l.IsApproved, l.ApprovedAt, l.SourceText,
                string.IsNullOrWhiteSpace(l.Assumptions) ? Array.Empty<string>() : l.Assumptions.Split('\n', StringSplitOptions.RemoveEmptyEntries), l.Version,
                l.Items.OrderBy(i => i.LineNumber).ThenBy(i => i.CreatedAt).Select(i => new ItemDto(i.Id, i.LineNumber, i.Description, i.Quantity, i.Unit, i.Material, i.Specifications, i.Notes,
                    i.Source.ToString(), i.CanonicalProductId, i.CanonicalProduct?.Name, i.CanonicalProduct?.BaseUnit, i.CanonicalProduct?.IsSafetyCritical ?? false, i.MatchConfidence,
                    i.ApprovedSubstituteProductId, i.ApprovedSubstituteProduct?.Name, i.SubstitutionApprovedAt)).ToList())).ToList());
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? NormalizeDate(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
}
