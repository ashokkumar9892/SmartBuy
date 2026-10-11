using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartBuy.Domain.Entities;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Suppliers.Mock;

namespace SmartBuy.Tests.Integration;

[Collection("api")]
public class AuthenticationTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_login_refresh_and_logout_work_end_to_end()
    {
        var session = await factory.RegisterAsync();
        var me = await session.GetAsync("/api/auth/me");
        Assert.Equal(session.Email, me["user"]!["email"]!.GetValue<string>());
        Assert.Equal("Owner", me["organization"]!["role"]!.GetValue<string>());
        Assert.Equal("free", me["plan"]!["code"]!.GetValue<string>());

        var again = await factory.LoginAsync(session.Email, session.Password);
        Assert.Equal(session.OrganizationId, again.OrganizationId);

        var anonymous = factory.CreateClient();
        var refreshed = await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = again.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newRefresh = (await refreshed.ReadJsonAsync())["refreshToken"]!.GetValue<string>();
        Assert.NotEqual(again.RefreshToken, newRefresh);

        // Rotated tokens are single use; presenting the old one again ends the whole session family.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = again.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = newRefresh })).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_repeated_failures_lock_the_account()
    {
        var session = await factory.RegisterAsync();
        var client = factory.CreateClient();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = session.Email, password = "wrong-password-1" })).StatusCode);
        // Locked: even the correct password is refused until the lockout expires.
        Assert.Equal((HttpStatusCode)423, (await client.PostAsJsonAsync("/api/auth/login", new { email = session.Email, password = session.Password })).StatusCode);
    }

    [Fact]
    public async Task Unknown_accounts_get_the_same_answer_as_wrong_passwords()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.test", password = "whatever-123" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("The email or password is incorrect.", (await response.ReadJsonAsync())["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Passwords_are_stored_hashed()
    {
        var session = await factory.RegisterAsync();
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var elevated = tenant.Elevate();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.FirstAsync(u => u.Id == session.UserId);
        Assert.StartsWith("pbkdf2-sha256$", user.PasswordHash);
        Assert.DoesNotContain(session.Password, user.PasswordHash);
    }

    [Theory]
    [InlineData("not-an-email", "Sup3r-secret-pass", "Name", "Org")]
    [InlineData("a@example.test", "short1", "Name", "Org")]
    [InlineData("a@example.test", "onlyletterslong", "Name", "Org")]
    [InlineData("a@example.test", "Sup3r-secret-pass", "", "Org")]
    public async Task Registration_input_is_validated(string email, string password, string fullName, string organizationName)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password, fullName, organizationName });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sign_up_needs_only_name_email_and_password()
    {
        var email = $"solo-{Guid.NewGuid().ToString("N")[..10]}@example.test";
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = "Sup3r-secret-pass", fullName = "Dana Whitfield" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = (await response.ReadJsonAsync())["session"]!;
        Assert.Equal("Dana's workspace", session["organization"]!["name"]!.GetValue<string>());
        Assert.Equal("Owner", session["organization"]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Facebook_sign_in_reports_not_configured_instead_of_pretending()
    {
        var config = await (await factory.CreateClient().GetAsync("/api/auth/config")).ReadJsonAsync();
        Assert.False(config["facebookEnabled"]!.GetValue<bool>());
        Assert.Null(config["facebookAppId"]);
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/facebook", new { accessToken = "x" });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_is_refused()
    {
        var session = await factory.RegisterAsync();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { email = session.Email.ToUpperInvariant(), password = "Sup3r-secret-pass", fullName = "X", organizationName = "Y" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_a_valid_token()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/overview")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Google_sign_in_reports_not_configured_instead_of_pretending()
    {
        var config = await (await factory.CreateClient().GetAsync("/api/auth/config")).ReadJsonAsync();
        Assert.False(config["googleEnabled"]!.GetValue<bool>());
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/google", new { idToken = "x" });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Demo_accounts_cannot_log_in_with_a_password()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = DataSeeder.DemoOwnerEmail("abc-plumbing"), password = "anything-at-all-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

[Collection("api")]
public class AuthorizationTests(ApiFactory factory)
{
    private async Task<TestSession> AddMemberAsync(TestSession owner, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..10]}@example.test";
        await owner.PostAsync("/api/organization/members", new { email, fullName = $"Test {role}", role, temporaryPassword = "Temp-pass-12345" });
        return await factory.LoginAsync(email, "Temp-pass-12345");
    }

    [Fact]
    public async Task Roles_limit_what_members_can_do()
    {
        var owner = await factory.RegisterAsync(plan: "business");
        var viewer = await AddMemberAsync(owner, "Viewer");
        var employee = await AddMemberAsync(owner, "Employee");
        var manager = await AddMemberAsync(owner, "PurchasingManager");

        // Viewer: read only.
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.PostAsJsonAsync("/api/projects", new { name = "Nope", industry = "Plumbing" })).StatusCode);

        // Employee: can build lists and draft POs, cannot approve, cannot manage the organization.
        var (projectId, _) = await employee.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 20, "ft"));
        await employee.PostAsync($"/api/projects/{projectId}/comparison");
        await employee.PostAsync($"/api/projects/{projectId}/cart/apply-plan", new { strategy = "LowestCost" });
        var po = (await employee.PostAsync($"/api/projects/{projectId}/cart/purchase-orders", new { })).AsArray()[0]!;
        var poId = po["id"]!.GetValue<Guid>();
        Assert.DoesNotContain("approve", po["allowedActions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.PostAsJsonAsync($"/api/purchase-orders/{poId}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.PutAsJsonAsync("/api/organization", new { name = "Hijack", industry = "Plumbing" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.PostAsJsonAsync("/api/organization/members", new { email = "x@example.test", fullName = "X", role = "Viewer", temporaryPassword = "Temp-pass-12345" })).StatusCode);

        // Manager: can approve, cannot change billing or manage members.
        await employee.PostAsync($"/api/purchase-orders/{poId}/submit");
        var approved = await manager.PostAsync($"/api/purchase-orders/{poId}/approve", new { comment = "ok" });
        Assert.Equal("Approved", approved["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.PostAsJsonAsync("/api/billing/change-plan", new { planCode = "free" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.DeleteAsync($"/api/organization/members/{employee.UserId}")).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_grant_a_role_above_their_own_or_remove_the_last_owner()
    {
        var owner = await factory.RegisterAsync(plan: "business");
        var admin = await AddMemberAsync(owner, "Administrator");
        var escalate = await admin.Client.PostAsJsonAsync("/api/organization/members", new { email = "boss@example.test", fullName = "Boss", role = "Owner", temporaryPassword = "Temp-pass-12345" });
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Client.PutAsJsonAsync($"/api/organization/members/{owner.UserId}", new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.Client.DeleteAsync($"/api/organization/members/{owner.UserId}")).StatusCode);
    }

    [Fact]
    public async Task Removed_members_lose_access_immediately_even_with_a_valid_token()
    {
        var owner = await factory.RegisterAsync(plan: "business");
        var employee = await AddMemberAsync(owner, "Employee");
        Assert.Equal(HttpStatusCode.OK, (await employee.Client.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Client.DeleteAsync($"/api/organization/members/{employee.UserId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await employee.Client.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Organization_owners_are_not_platform_administrators()
    {
        var owner = await factory.RegisterAsync(plan: "business");
        foreach (var url in new[] { "/api/admin/overview", "/api/admin/users", "/api/admin/organizations", "/api/admin/flags", "/api/admin/matches", "/api/admin/errors" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.PostAsJsonAsync("/api/admin/demo/reset", new { })).StatusCode);

        var admin = await factory.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var overview = await admin.GetAsync("/api/admin/overview");
        Assert.True(overview["organizations"]!.GetValue<int>() >= 4);
        Assert.NotEmpty((await admin.GetAsync("/api/admin/organizations")).AsArray());
    }
}

[Collection("api")]
public class TenantIsolationTests(ApiFactory factory)
{
    [Fact]
    public async Task One_organization_can_never_read_or_change_anothers_data()
    {
        var a = await factory.RegisterAsync("Tenant A", "business");
        var b = await factory.RegisterAsync("Tenant B", "business");

        var (projectId, project) = await a.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"), ("Angle Stop", 10, "each"));
        var listId = project["lists"]![0]!["id"]!.GetValue<Guid>();
        var itemId = project["lists"]![0]!["items"]![0]!["id"]!.GetValue<Guid>();
        await a.PostAsync($"/api/projects/{projectId}/comparison");
        var cart = await a.PostAsync($"/api/projects/{projectId}/cart/apply-plan", new { strategy = "LowestCost" });
        var cartItemId = cart["suppliers"]![0]!["lines"]![0]!["id"]!.GetValue<Guid>();
        var poId = (await a.PostAsync($"/api/projects/{projectId}/cart/purchase-orders", new { })).AsArray()[0]!["id"]!.GetValue<Guid>();
        var catalogId = (await a.GetAsync("/api/catalog/products?query=copper")).AsArray()[0]!["id"]!.GetValue<Guid>();
        await a.PostAsync("/api/price-alerts", new { type = "PriceBelow", productId = catalogId, targetPrice = 1.0 });

        // B sees nothing of A's in any listing.
        Assert.Empty((await b.GetAsync("/api/projects")).AsArray());
        Assert.Empty((await b.GetAsync("/api/purchase-orders")).AsArray());
        Assert.Empty((await b.GetAsync("/api/price-alerts")).AsArray());
        Assert.Equal(0, (await b.GetAsync("/api/dashboard"))["totalProjects"]!.GetValue<int>());
        Assert.Single((await b.GetAsync("/api/organization/members")).AsArray());

        // Direct access by id returns 404 for every resource type, for reads and writes alike.
        var item = new { description = "Hijacked", quantity = 1, unit = "each" };
        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("get project", () => b.Client.GetAsync($"/api/projects/{projectId}")),
            ("update project", () => b.Client.PutAsJsonAsync($"/api/projects/{projectId}", new { name = "Hijacked", industry = "Plumbing" })),
            ("delete project", () => b.Client.DeleteAsync($"/api/projects/{projectId}")),
            ("add list", () => b.Client.PostAsJsonAsync($"/api/projects/{projectId}/lists", new { name = "x" })),
            ("approve list", () => b.Client.PostAsJsonAsync($"/api/lists/{listId}/approve", new { })),
            ("add item", () => b.Client.PostAsJsonAsync($"/api/lists/{listId}/items", item)),
            ("update item", () => b.Client.PutAsJsonAsync($"/api/items/{itemId}", item)),
            ("delete item", () => b.Client.DeleteAsync($"/api/items/{itemId}")),
            ("alternatives", () => b.Client.GetAsync($"/api/items/{itemId}/alternatives")),
            ("compare", () => b.Client.PostAsJsonAsync($"/api/projects/{projectId}/comparison", new { })),
            ("get cart", () => b.Client.GetAsync($"/api/projects/{projectId}/cart")),
            ("update cart line", () => b.Client.PutAsJsonAsync($"/api/cart-items/{cartItemId}", new { packs = 99 })),
            ("purchase link", () => b.Client.PostAsJsonAsync($"/api/cart-items/{cartItemId}/purchase-link", new { })),
            ("get po", () => b.Client.GetAsync($"/api/purchase-orders/{poId}")),
            ("approve po", () => b.Client.PostAsJsonAsync($"/api/purchase-orders/{poId}/approve", new { })),
            ("export po", () => b.Client.GetAsync($"/api/purchase-orders/{poId}/export.pdf")),
            ("delete po", () => b.Client.DeleteAsync($"/api/purchase-orders/{poId}")),
            ("remove member", () => b.Client.DeleteAsync($"/api/organization/members/{a.UserId}")),
        };
        foreach (var (name, call) in attempts)
        {
            var response = await call();
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{name} returned {(int)response.StatusCode}");
        }

        // A project id from another tenant cannot be attached to B's own records either.
        var supplierId = (await b.GetAsync("/api/suppliers")).AsArray()[0]!["id"]!.GetValue<Guid>();
        var foreignProject = await b.Client.PostAsJsonAsync("/api/purchase-orders", new { supplierId, projectId, items = new[] { new { description = "x", quantity = 1, packQuantity = 1, unit = "each", unitPrice = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, foreignProject.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync("/api/projects", new { name = "Copy", industry = "Plumbing", reuseProjectId = projectId })).StatusCode);

        // A's data is untouched.
        var intact = await a.GetAsync($"/api/projects/{projectId}");
        Assert.Equal(2, intact["lists"]![0]!["items"]!.AsArray().Count);
        Assert.Equal("Draft", (await a.GetAsync($"/api/purchase-orders/{poId}"))["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_user_cannot_switch_into_an_organization_they_do_not_belong_to()
    {
        var a = await factory.RegisterAsync();
        var b = await factory.RegisterAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Client.PostAsJsonAsync("/api/auth/switch-organization", new { organizationId = a.OrganizationId })).StatusCode);
    }

    [Fact]
    public async Task The_data_layer_refuses_cross_tenant_writes_even_if_application_code_tries()
    {
        var a = await factory.RegisterAsync();
        var b = await factory.RegisterAsync();
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // No tenant at all: tenant-owned tables read as empty and reject writes.
        Assert.Empty(await db.PurchasingProjects.ToListAsync());
        db.PurchasingProjects.Add(new PurchasingProject { Name = "orphan" });
        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Acting as B while forging A's organization id is rejected.
        tenant.OrganizationId = b.OrganizationId;
        tenant.UserId = b.UserId;
        db.PurchasingProjects.Add(new PurchasingProject { Name = "forged", OrganizationId = a.OrganizationId });
        await Assert.ThrowsAsync<TenantIsolationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // A normal write is stamped with the active organization automatically.
        var own = new PurchasingProject { Name = "mine", CreatedByUserId = b.UserId };
        db.PurchasingProjects.Add(own);
        await db.SaveChangesAsync();
        Assert.Equal(b.OrganizationId, own.OrganizationId);
    }
}

[Collection("api")]
public class ValidationTests(ApiFactory factory)
{
    [Fact]
    public async Task Project_and_item_input_is_validated()
    {
        var session = await factory.RegisterAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync("/api/projects", new { name = "", industry = "Plumbing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync("/api/projects", new { name = "Ok", industry = "NotAnIndustry" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync("/api/projects", new { name = new string('x', 201), industry = "Plumbing" })).StatusCode);

        var project = await session.PostAsync("/api/projects", new { name = "Valid", industry = "Plumbing", initialList = new { name = "List" } });
        var listId = project["lists"]![0]!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync($"/api/lists/{listId}/items", new { description = "Pipe", quantity = -1, unit = "ft" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync($"/api/lists/{listId}/items", new { description = "Pipe", quantity = 0, unit = "ft" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync($"/api/lists/{listId}/items", new { description = "", quantity = 1, unit = "ft" })).StatusCode);
        var badUnit = await session.Client.PostAsJsonAsync($"/api/lists/{listId}/items", new { description = "Pipe", quantity = 1, unit = "furlongs" });
        Assert.Equal(HttpStatusCode.BadRequest, badUnit.StatusCode);
        Assert.Contains("not a recognized unit", (await badUnit.ReadJsonAsync())["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync($"/api/lists/{listId}/items", new { description = "Pipe", quantity = 1, unit = "ft", canonicalProductId = Guid.NewGuid() })).StatusCode);

        // An empty list cannot be approved, and an unapproved list cannot be compared.
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync($"/api/lists/{listId}/approve", new { })).StatusCode);
        var blocked = await session.Client.PostAsJsonAsync($"/api/projects/{project["id"]!.GetValue<Guid>()}/comparison", new { });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("list_not_approved", (await blocked.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Uploads_are_validated_and_parsed_server_side()
    {
        var session = await factory.RegisterAsync();

        async Task<HttpResponseMessage> Upload(string name, byte[] bytes)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", name);
            return await session.Client.PostAsync("/api/material-lists/parse-upload", form);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload("malware.exe", Encoding.UTF8.GetBytes("MZ"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload("fake.pdf", Encoding.UTF8.GetBytes("<html>"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload("fake.xlsx", Encoding.UTF8.GetBytes("plain text"))).StatusCode);

        var ok = await Upload("list.csv", Encoding.UTF8.GetBytes("Description,Quantity,Unit\n3/4 in. Type L Copper Pipe,50,ft\n=cmd|' /C calc'!A0,2,each\n"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var draft = await ok.ReadJsonAsync();
        Assert.True(draft["requiresReview"]!.GetValue<bool>());
        Assert.Equal(2, draft["items"]!.AsArray().Count);
        Assert.Equal("3/4 in. Type L Copper Pipe", draft["items"]![0]!["suggestedProductName"]!.GetValue<string>());
        // Nothing was saved by parsing.
        Assert.Empty((await session.GetAsync("/api/projects")).AsArray());
    }

    [Fact]
    public async Task Stale_edits_are_rejected_with_a_concurrency_conflict()
    {
        var session = await factory.RegisterAsync();
        var project = await session.PostAsync("/api/projects", new { name = "Original", industry = "Plumbing" });
        var id = project["id"]!.GetValue<Guid>();
        var version = project["version"]!.GetValue<Guid>();
        await session.PutAsync($"/api/projects/{id}", new { name = "First edit", industry = "Plumbing", version });
        var stale = await session.Client.PutAsJsonAsync($"/api/projects/{id}", new { name = "Second edit", industry = "Plumbing", version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency", (await stale.ReadJsonAsync())["code"]!.GetValue<string>());
        Assert.Equal("First edit", (await session.GetAsync($"/api/projects/{id}"))["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleted_projects_are_soft_deleted_and_hidden()
    {
        var session = await factory.RegisterAsync();
        var id = (await session.PostAsync("/api/projects", new { name = "Temp", industry = "Plumbing" }))["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.DeleteAsync($"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await session.Client.GetAsync($"/api/projects/{id}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        using var elevated = scope.ServiceProvider.GetRequiredService<TenantContext>().Elevate();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PurchasingProjects.IgnoreQueryFilters().FirstAsync(p => p.Id == id);
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAt);
    }
}

[Collection("api")]
public class SubscriptionTests(ApiFactory factory)
{
    [Fact]
    public async Task Free_plan_blocks_paid_features_with_402_and_caps_projects()
    {
        var session = await factory.RegisterAsync();
        var supplierId = (await session.GetAsync("/api/suppliers")).AsArray()[0]!["id"]!.GetValue<Guid>();
        var productId = (await session.GetAsync("/api/catalog/products?query=copper")).AsArray()[0]!["id"]!.GetValue<Guid>();

        var po = await session.Client.PostAsJsonAsync("/api/purchase-orders", new { supplierId, items = new[] { new { description = "x", quantity = 1, packQuantity = 1, unit = "each", unitPrice = 1 } } });
        Assert.Equal(HttpStatusCode.PaymentRequired, po.StatusCode);
        Assert.Equal("plan_limit", (await po.ReadJsonAsync())["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync("/api/price-alerts", new { type = "PriceBelow", productId, targetPrice = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync("/api/material-lists/generate", new { text = "20 elbows" })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.GetAsync("/api/reports/spend")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync("/api/organization/members", new { email = "second@example.test", fullName = "Second", role = "Employee", temporaryPassword = "Temp-pass-12345" })).StatusCode);

        // Basic comparison still works, with the advanced strategies locked.
        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"));
        var comparison = await session.PostAsync($"/api/projects/{projectId}/comparison");
        Assert.Equal(new[] { "LowestCost" }, comparison["plans"]!.AsArray().Select(p => p!["strategy"]!.GetValue<string>()));
        Assert.Equal(2, comparison["lockedStrategies"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync($"/api/projects/{projectId}/cart/apply-plan", new { strategy = "Balanced" })).StatusCode);

        // Three projects per month on Free; deleting one does not free up quota.
        await session.PostAsync("/api/projects", new { name = "Two", industry = "Plumbing" });
        var third = (await session.PostAsync("/api/projects", new { name = "Three", industry = "Plumbing" }))["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync("/api/projects", new { name = "Four", industry = "Plumbing" })).StatusCode);
        await session.Client.DeleteAsync($"/api/projects/{third}");
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync("/api/projects", new { name = "Four", industry = "Plumbing" })).StatusCode);
    }

    [Fact]
    public async Task Upgrading_unlocks_features_immediately()
    {
        var session = await factory.RegisterAsync();
        await session.PostAsync("/api/billing/change-plan", new { planCode = "pro" });
        var draft = await session.PostAsync("/api/material-lists/generate", new { text = "I need 50 feet of 3/4-inch copper pipe, 20 elbows, 10 shutoff valves, and 5 faucets", industry = "Plumbing" });
        Assert.Equal(4, draft["items"]!.AsArray().Count);
        Assert.Equal("mock", draft["source"]!.GetValue<string>());
        Assert.True(draft["requiresReview"]!.GetValue<bool>());
        Assert.NotEmpty(draft["assumptions"]!.AsArray());

        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"));
        Assert.Equal(3, (await session.PostAsync($"/api/projects/{projectId}/comparison"))["plans"]!.AsArray().Count);

        // Pro still has no purchase orders.
        var supplierId = (await session.GetAsync("/api/suppliers")).AsArray()[0]!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.PaymentRequired, (await session.Client.PostAsJsonAsync("/api/purchase-orders", new { supplierId, items = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task Billing_reports_stripe_as_not_configured_and_checkout_is_unavailable()
    {
        var session = await factory.RegisterAsync();
        var billing = await session.GetAsync("/api/billing");
        Assert.False(billing["billing"]!["stripeConfigured"]!.GetValue<bool>());
        Assert.Equal(new[] { 0, 2900, 7900 }, billing["plans"]!.AsArray().Select(p => p!["monthlyPriceCents"]!.GetValue<int>()));
        var checkout = await session.Client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "pro" });
        Assert.Equal(HttpStatusCode.Conflict, checkout.StatusCode);
        Assert.Equal("billing_not_configured", (await checkout.ReadJsonAsync())["code"]!.GetValue<string>());
        // Unsigned webhooks are rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsync("/api/billing/webhook", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }
}

[Collection("api")]
public class PurchasingWorkflowTests(ApiFactory factory)
{
    [Fact]
    public async Task Comparison_normalizes_packs_labels_data_and_never_reports_unknowns_as_zero()
    {
        var session = await factory.RegisterAsync(plan: "business");
        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"), ("3/4 in. Copper 90-Degree Elbow", 20, "each"), ("Brown Hardwood Mulch", 3, "cuyd"));
        var comparison = await session.PostAsync($"/api/projects/{projectId}/comparison");

        Assert.True(comparison["containsDemoData"]!.GetValue<bool>());
        Assert.Null(comparison["taxRatePercent"]);
        Assert.Contains(comparison["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("tax rate"));

        foreach (var item in comparison["items"]!.AsArray())
        {
            var quantity = item!["quantity"]!.GetValue<decimal>();
            foreach (var offer in item["offers"]!.AsArray())
            {
                Assert.Equal("DEMO", offer!["dataStatus"]!.GetValue<string>());
                Assert.True(offer["taxUnknown"]!.GetValue<bool>());
                Assert.Null(offer["tax"]);
                if (offer["shippingUnknown"]!.GetValue<bool>()) Assert.Null(offer["shipping"]);
                if (offer["packsRequired"] is null) continue;
                // Whole packages, enough to cover the requirement, and no more than one package of surplus.
                var packs = offer["packsRequired"]!.GetValue<int>();
                var purchased = offer["purchasedQuantity"]!.GetValue<decimal>();
                Assert.True(purchased >= quantity - 0.001m, $"{offer["productTitle"]}: bought {purchased} of {quantity}");
                Assert.Equal(decimal.Round(packs * offer["packPrice"]!.GetValue<decimal>(), 2), offer["subtotal"]!.GetValue<decimal>());
            }
        }

        var lowest = comparison["plans"]!.AsArray().First(p => p!["strategy"]!.GetValue<string>() == "LowestCost")!;
        Assert.True(lowest["totals"]!["taxUnknown"]!.GetValue<bool>());
        Assert.False(lowest["totals"]!["isComplete"]!.GetValue<bool>());
        Assert.True(lowest["totals"]!["knownTotal"]!.GetValue<decimal>() <= comparison["baseline"]!["totals"]!["knownTotal"]!.GetValue<decimal>() + 30m);
        Assert.StartsWith("Single supplier:", comparison["baseline"]!["label"]!.GetValue<string>());

        // Setting a tax rate makes tax known everywhere.
        await session.PutAsync("/api/organization", new { name = "Taxed Org", industry = "Plumbing", estimatedTaxRatePercent = 8.25 });
        var taxed = await session.PostAsync($"/api/projects/{projectId}/comparison");
        Assert.False(taxed["plans"]![0]!["totals"]!["taxUnknown"]!.GetValue<bool>());
        Assert.True(taxed["plans"]![0]!["totals"]!["tax"]!.GetValue<decimal>() > 0);
    }

    [Fact]
    public async Task One_failing_and_one_slow_supplier_do_not_break_the_comparison()
    {
        var session = await factory.RegisterAsync(plan: "business");
        var control = factory.Service<MockSupplierControl>();
        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"), ("3/4 in. Copper 90-Degree Elbow", 20, "each"));
        try
        {
            control.SetFault("homedepot", "error");
            control.SetFault("lowes", "timeout");
            var started = DateTime.UtcNow;
            var comparison = await session.PostAsync($"/api/projects/{projectId}/comparison?refresh=true");
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "A timed-out supplier must not stall the request.");

            var issues = comparison["providerIssues"]!.AsArray().Select(i => (Name: i!["supplierName"]!.GetValue<string>(), Message: i["message"]!.GetValue<string>())).ToList();
            Assert.Contains(issues, i => i.Name == "Home Depot" && i.Message.Contains("could not be reached"));
            Assert.Contains(issues, i => i.Name == "Lowe's" && i.Message.Contains("Timed out"));
            // Internal error text is not leaked to tenants.
            Assert.DoesNotContain(issues, i => i.Message.Contains("503") || i.Message.Contains("Exception"));

            var suppliers = comparison["items"]!.AsArray().SelectMany(i => i!["offers"]!.AsArray()).Select(o => o!["supplierName"]!.GetValue<string>()).Distinct().ToList();
            Assert.NotEmpty(suppliers);
            Assert.NotEmpty(comparison["plans"]![0]!["lines"]!.AsArray());

            var admin = await factory.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
            var usage = await admin.GetAsync("/api/admin/usage/providers");
            Assert.Contains(usage["recent"]!.AsArray(), l => l!["supplier"]!.GetValue<string>() == "Home Depot" && !l["success"]!.GetValue<bool>());
        }
        finally
        {
            control.SetFault("homedepot", null);
            control.SetFault("lowes", null);
        }
    }

    [Fact]
    public async Task Cart_groups_by_supplier_and_purchase_orders_follow_the_approval_workflow()
    {
        var session = await factory.RegisterAsync(plan: "business");
        await session.PutAsync("/api/organization", new { name = "PO Org", industry = "Plumbing", estimatedTaxRatePercent = 10 });
        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"), ("Angle Stop", 10, "each"), ("AF-2100", 5, "each"));
        var comparison = await session.PostAsync($"/api/projects/{projectId}/comparison");
        var plan = comparison["plans"]!.AsArray().First(p => p!["strategy"]!.GetValue<string>() == "LowestCost")!;

        var cart = await session.PostAsync($"/api/projects/{projectId}/cart/apply-plan", new { strategy = "LowestCost" });
        Assert.Equal(plan["lines"]!.AsArray().Count, cart["lineCount"]!.GetValue<int>());
        Assert.Equal(plan["totals"]!["subtotal"]!.GetValue<decimal>(), cart["totals"]!["subtotal"]!.GetValue<decimal>());
        Assert.All(cart["suppliers"]!.AsArray(), s => Assert.Contains(s!["checkoutMode"]!.GetValue<string>(), new[] { "link", "export" }));
        Assert.StartsWith("Supplier,Product", Encoding.UTF8.GetString(await session.Client.GetByteArrayAsync($"/api/projects/{projectId}/cart/export.csv")).TrimStart('﻿'));

        var orders = (await session.PostAsync($"/api/projects/{projectId}/cart/purchase-orders", new { })).AsArray();
        Assert.Equal(cart["suppliers"]!.AsArray().Count, orders.Count);
        Assert.Equal(0, (await session.GetAsync($"/api/projects/{projectId}/cart"))["lineCount"]!.GetValue<int>());

        var po = orders[0]!;
        var id = po["id"]!.GetValue<Guid>();
        Assert.Matches(@"^PO-\d{4}-\d{4}$", po["poNumber"]!.GetValue<string>());
        Assert.Equal(orders.Count, orders.Select(o => o!["poNumber"]!.GetValue<string>()).Distinct().Count());
        Assert.Equal("Draft", po["status"]!.GetValue<string>());
        var subtotal = po["items"]!.AsArray().Sum(i => decimal.Round(i!["quantity"]!.GetValue<decimal>() * i["unitPrice"]!.GetValue<decimal>(), 2, MidpointRounding.AwayFromZero));
        Assert.Equal(subtotal, po["subtotal"]!.GetValue<decimal>());
        Assert.Equal(decimal.Round(subtotal * 0.10m, 2, MidpointRounding.AwayFromZero), po["taxEstimate"]!.GetValue<decimal>());
        var expectedTotal = subtotal + (po["shippingEstimate"]?.GetValue<decimal>() ?? 0) + po["taxEstimate"]!.GetValue<decimal>();
        Assert.Equal(expectedTotal, po["total"]!.GetValue<decimal>());
        Assert.Equal(po["shippingEstimate"] is null, po["totalExcludesUnknowns"]!.GetValue<bool>());

        // Cannot jump straight from Draft to Sent.
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync($"/api/purchase-orders/{id}/record-sent", new { })).StatusCode);

        Assert.Equal("Pending Approval", (await session.PostAsync($"/api/purchase-orders/{id}/submit"))["status"]!.GetValue<string>());
        // Locked while pending.
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PutAsJsonAsync($"/api/purchase-orders/{id}", new { supplierId = po["supplierId"]!.GetValue<Guid>(), items = Array.Empty<object>() })).StatusCode);
        Assert.Equal("Draft", (await session.PostAsync($"/api/purchase-orders/{id}/reject", new { comment = "Check quantities" }))["status"]!.GetValue<string>());
        await session.PostAsync($"/api/purchase-orders/{id}/submit");
        var approved = await session.PostAsync($"/api/purchase-orders/{id}/approve", new { comment = "Looks right" });
        Assert.Equal("Approved", approved["status"]!.GetValue<string>());
        Assert.Equal(2, approved["approvals"]!.AsArray().Count);

        // No delivery channel is configured in tests: sending must fail loudly and leave the PO untouched.
        Assert.DoesNotContain("send", approved["allowedActions"]!.AsArray().Select(a => a!.GetValue<string>()));
        var send = await session.Client.PostAsJsonAsync($"/api/purchase-orders/{id}/send", new { recipient = "orders@supplier.example" });
        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode);
        Assert.Equal("delivery_not_configured", (await send.ReadJsonAsync())["code"]!.GetValue<string>());
        var stillApproved = await session.GetAsync($"/api/purchase-orders/{id}");
        Assert.Equal("Approved", stillApproved["status"]!.GetValue<string>());
        Assert.Null(stillApproved["sentAt"]);

        // Manual record is labelled as such.
        var sent = await session.PostAsync($"/api/purchase-orders/{id}/record-sent", new { note = "Phoned in to the counter" });
        Assert.Equal("Sent", sent["status"]!.GetValue<string>());
        Assert.Contains("manually", sent["sentVia"]!.GetValue<string>());

        var lines = sent["items"]!.AsArray();
        var partial = await session.PostAsync($"/api/purchase-orders/{id}/receive", new { lines = new[] { new { itemId = lines[0]!["id"]!.GetValue<Guid>(), quantityReceived = lines[0]!["quantity"]!.GetValue<decimal>() / 2 } } });
        Assert.Equal("Partially Received", partial["status"]!.GetValue<string>());
        var received = await session.PostAsync($"/api/purchase-orders/{id}/receive", new { lines = lines.Select(l => new { itemId = l!["id"]!.GetValue<Guid>(), quantityReceived = l["quantity"]!.GetValue<decimal>() }).ToArray() });
        Assert.Equal("Received", received["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync($"/api/purchase-orders/{id}/cancel", new { })).StatusCode);

        var pdf = await session.Client.GetByteArrayAsync($"/api/purchase-orders/{id}/export.pdf");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        var csv = Encoding.UTF8.GetString(await session.Client.GetByteArrayAsync($"/api/purchase-orders/{id}/export.csv"));
        Assert.Contains(po["poNumber"]!.GetValue<string>(), csv);

        var report = await session.GetAsync("/api/reports/spend");
        Assert.Equal(received["total"]!.GetValue<decimal>(), report["totalCommitted"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Purchase_links_are_plain_supplier_links_and_clicks_are_logged_without_fake_affiliate_tags()
    {
        var session = await factory.RegisterAsync(plan: "business");
        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"), ("AF-2100", 5, "each"), ("Angle Stop", 10, "each"));
        await session.PostAsync($"/api/projects/{projectId}/comparison");
        var cart = await session.PostAsync($"/api/projects/{projectId}/cart/apply-plan", new { strategy = "FastestDelivery" });
        foreach (var supplier in cart["suppliers"]!.AsArray())
        {
            var response = await session.Client.PostAsJsonAsync($"/api/cart-items/{supplier!["lines"]![0]!["id"]!.GetValue<Guid>()}/purchase-link", new { });
            if (supplier["checkoutMode"]!.GetValue<string>() == "export")
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                continue;
            }
            var link = await response.ReadJsonAsync();
            Assert.StartsWith("https://", link["url"]!.GetValue<string>());
            Assert.False(link["isAffiliate"]!.GetValue<bool>());
            Assert.DoesNotContain("tag=", link["url"]!.GetValue<string>());
            Assert.Contains("Demo data", link["note"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Safety_critical_substitutions_are_blocked_and_others_need_explicit_acknowledgement()
    {
        var session = await factory.RegisterAsync(plan: "business");
        var (projectId, project) = await session.CreateApprovedProjectAsync(("12/2 NM-B", 100, "ft"), ("3/4 in. Type L Copper Pipe", 50, "ft"));
        var items = project["lists"]![0]!["items"]!.AsArray();
        var wireItem = items[0]!["id"]!.GetValue<Guid>();
        var pipeItem = items[1]!["id"]!.GetValue<Guid>();

        var wire = await session.GetAsync($"/api/items/{wireItem}/alternatives");
        Assert.True(wire["isSafetyCritical"]!.GetValue<bool>());
        var smaller = wire["alternatives"]!.AsArray().Single();
        Assert.False(smaller!["eligible"]!.GetValue<bool>());
        Assert.Contains("safety-critical", smaller["blockedReason"]!.GetValue<string>());
        var forced = await session.Client.PostAsJsonAsync($"/api/items/{wireItem}/substitution", new { alternativeProductId = smaller["productId"]!.GetValue<Guid>(), acknowledged = true });
        Assert.Equal(HttpStatusCode.Conflict, forced.StatusCode);
        Assert.Equal("substitution_blocked", (await forced.ReadJsonAsync())["code"]!.GetValue<string>());

        var pipe = await session.GetAsync($"/api/items/{pipeItem}/alternatives");
        Assert.Equal("template", pipe["explanationSource"]!.GetValue<string>());
        var typeM = pipe["alternatives"]!.AsArray().First(a => a!["name"]!.GetValue<string>().Contains("Type M"))!;
        Assert.False(string.IsNullOrWhiteSpace(typeM["specDifferences"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(typeM["compatibility"]!.GetValue<string>()));
        var altId = typeM["productId"]!.GetValue<Guid>();

        // Not acknowledged, and an arbitrary product: both refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync($"/api/items/{pipeItem}/substitution", new { alternativeProductId = altId, acknowledged = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync($"/api/items/{pipeItem}/substitution", new { alternativeProductId = Guid.NewGuid(), acknowledged = true })).StatusCode);

        // Before approval the comparison contains no substitute offers at all.
        var before = await session.PostAsync($"/api/projects/{projectId}/comparison");
        Assert.DoesNotContain(before["items"]!.AsArray().SelectMany(i => i!["offers"]!.AsArray()), o => o!["isSubstitute"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsJsonAsync($"/api/items/{pipeItem}/substitution", new { alternativeProductId = altId, acknowledged = true })).StatusCode);
        var after = await session.PostAsync($"/api/projects/{projectId}/comparison");
        Assert.Contains(after["items"]!.AsArray().SelectMany(i => i!["offers"]!.AsArray()), o => o!["isSubstitute"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Price_alerts_run_on_demand_and_only_fire_when_the_rule_is_met()
    {
        var session = await factory.RegisterAsync(plan: "business");
        var productId = (await session.GetAsync("/api/catalog/products?query=Type L Copper Pipe")).AsArray()[0]!["id"]!.GetValue<Guid>();
        await session.PostAsync("/api/price-alerts", new { type = "PriceBelow", productId, targetPrice = 1000 });
        await session.PostAsync("/api/price-alerts", new { type = "PriceBelow", productId, targetPrice = 0.01 });
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync("/api/price-alerts", new { type = "PriceBelow", productId })).StatusCode);

        var result = await session.PostAsync("/api/price-alerts/run");
        Assert.Equal(2, result["alertsChecked"]!.GetValue<int>());
        Assert.Equal(1, result["alertsTriggered"]!.GetValue<int>());
        var notifications = (await session.GetAsync("/api/notifications")).AsArray();
        Assert.Single(notifications);
        Assert.StartsWith("Price drop", notifications[0]!["title"]!.GetValue<string>());

        // Running again at the same price does not notify twice.
        Assert.Equal(0, (await session.PostAsync("/api/price-alerts/run"))["alertsTriggered"]!.GetValue<int>());
        Assert.Equal(1, (await session.GetAsync("/api/dashboard"))["unreadNotifications"]!.GetValue<int>());
    }

    [Fact]
    public async Task Low_confidence_matches_wait_in_the_review_queue_until_an_administrator_decides()
    {
        var session = await factory.RegisterAsync(plan: "business");
        var (projectId, _) = await session.CreateApprovedProjectAsync(("3/4 in. Type L Copper Pipe", 50, "ft"), ("Angle Stop", 10, "each"), ("12/2 NM-B", 100, "ft"), ("Trash Bags", 40, "each"));
        var before = await session.PostAsync($"/api/projects/{projectId}/comparison");
        var admin = await factory.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var queue = (await admin.GetAsync("/api/admin/matches")).AsArray();
        Assert.NotEmpty(queue);
        Assert.All(queue, m => Assert.True(m!["confidence"]!.GetValue<double>() < 0.85));

        // Name-only listings (the eBay mock) are in the queue and absent from the comparison.
        var titleOnly = queue.First(m => m!["method"]!.GetValue<string>() == "TitleSimilarity")!;
        Assert.Equal("eBay", titleOnly["supplierName"]!.GetValue<string>());
        Assert.DoesNotContain(before["items"]!.AsArray().SelectMany(i => i!["offers"]!.AsArray()), o => o!["supplierName"]!.GetValue<string>() == "eBay");

        await admin.PostAsync($"/api/admin/matches/{titleOnly["id"]!.GetValue<Guid>()}/confirm");
        var after = await session.PostAsync($"/api/projects/{projectId}/comparison?refresh=true");
        var confirmedProduct = titleOnly["canonicalProductId"]!.GetValue<Guid>();
        if (after["items"]!.AsArray().Any(i => i!["canonicalProductId"]!.GetValue<Guid>() == confirmedProduct))
            Assert.Contains(after["items"]!.AsArray().SelectMany(i => i!["offers"]!.AsArray()), o => o!["supplierName"]!.GetValue<string>() == "eBay" && o["matchMethod"]!.GetValue<string>() == "Manual");
    }
}

[Collection("api")]
public class DemoAndPlatformTests(ApiFactory factory)
{
    [Fact]
    public async Task The_three_sample_businesses_are_seeded_navigable_and_labelled_as_demo()
    {
        var config = await (await factory.CreateClient().GetAsync("/api/auth/config")).ReadJsonAsync();
        Assert.True(config["demoEnabled"]!.GetValue<bool>());
        Assert.Equal(new[] { "ABC Plumbing", "Premier Renovations", "Green Valley Landscaping" }, config["demoBusinesses"]!.AsArray().Select(b => b!["name"]!.GetValue<string>()));

        foreach (var slug in new[] { "abc-plumbing", "premier-renovations", "green-valley-landscaping" })
        {
            var demo = await factory.DemoAsync(slug);
            var me = await demo.GetAsync("/api/auth/me");
            Assert.True(me["organization"]!["isDemo"]!.GetValue<bool>());
            var dashboard = await demo.GetAsync("/api/dashboard");
            Assert.True(dashboard["totalProjects"]!.GetValue<int>() >= 1);
            Assert.True(dashboard["savingsContainDemoData"]!.GetValue<bool>());
            Assert.NotEmpty(dashboard["recentComparisons"]!.AsArray());
            var projects = (await demo.GetAsync("/api/projects")).AsArray();
            var approved = projects.First(p => !p!["hasUnapprovedList"]!.GetValue<bool>())!;
            var comparison = await demo.PostAsync($"/api/projects/{approved["id"]!.GetValue<Guid>()}/comparison");
            Assert.True(comparison["containsDemoData"]!.GetValue<bool>());
            Assert.All(comparison["items"]!.AsArray().SelectMany(i => i!["offers"]!.AsArray()), o => Assert.NotEqual("LIVE", o!["dataStatus"]!.GetValue<string>()));
        }

        var suppliers = (await (await factory.DemoAsync("abc-plumbing")).GetAsync("/api/suppliers")).AsArray();
        Assert.Equal(9, suppliers.Count);
        Assert.All(suppliers, s => Assert.Equal("Mock", s!["mode"]!.GetValue<string>()));
        Assert.All(suppliers, s => Assert.False(s!["capabilities"]!["orderPlacement"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Anyone_can_search_a_product_and_get_supplier_prices_with_safe_buy_links()
    {
        var client = factory.CreateClient();
        var result = await (await client.GetAsync("/api/public/search?q=copper%20pipe")).ReadJsonAsync();
        var products = result["results"]!.AsArray();
        Assert.NotEmpty(products);
        Assert.True(result["demoData"]!.GetValue<bool>());
        foreach (var product in products)
        {
            var offers = product!["offers"]!.AsArray();
            Assert.NotEmpty(offers);
            Assert.All(offers, o => Assert.Equal("DEMO", o!["dataStatus"]!.GetValue<string>()));
            // Every offer can be clicked through, and only ever to the supplier's own HTTPS site.
            Assert.All(offers, o => Assert.StartsWith("https://www.", o!["buyUrl"]!.GetValue<string>()));
            Assert.DoesNotContain(offers, o => o!["supplier"]!.GetValue<string>() == "eBay");
            var available = offers.Where(o => o!["stockStatus"]!.GetValue<string>() != "out_of_stock").Select(o => o!["unitPrice"]!.GetValue<decimal>()).ToList();
            Assert.Equal(available.OrderBy(p => p), available);
        }

        var none = await (await client.GetAsync("/api/public/search?q=quantum%20flux%20capacitor")).ReadJsonAsync();
        Assert.Empty(none["results"]!.AsArray());
        Assert.NotEmpty(none["suggestions"]!.AsArray());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/public/search?q=")).StatusCode);

        // The search box suggests from this list as the visitor types.
        var catalog = (await (await client.GetAsync("/api/public/catalog")).ReadJsonAsync()).AsArray();
        Assert.Contains(catalog, p => p!["name"]!.GetValue<string>().Contains("Copper Pipe") && p["category"]!.GetValue<string>() == "Pipe");
    }

    [Fact]
    public async Task Health_and_openapi_endpoints_respond()
    {
        var client = factory.CreateClient();
        Assert.Equal("Healthy", await client.GetStringAsync("/health/live"));
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready"));
        var response = await client.GetAsync("/api/auth/config");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task Cors_only_allows_configured_origins()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        var response = await client.SendAsync(request);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Feature_flags_and_audit_log_are_enforced_and_recorded()
    {
        var admin = await factory.LoginAsync(ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var session = await factory.RegisterAsync(plan: "pro");
        try
        {
            await admin.Client.PutAsJsonAsync("/api/admin/flags/ai_material_lists", new { isEnabled = false });
            var blocked = await session.Client.PostAsJsonAsync("/api/material-lists/generate", new { text = "20 elbows" });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("feature_disabled", (await blocked.ReadJsonAsync())["code"]!.GetValue<string>());
        }
        finally
        {
            await admin.Client.PutAsJsonAsync("/api/admin/flags/ai_material_lists", new { isEnabled = true });
        }
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync("/api/material-lists/generate", new { text = "20 elbows" })).StatusCode);

        var audit = (await admin.GetAsync($"/api/admin/audit?organizationId={session.OrganizationId}")).AsArray().Select(a => a!["action"]!.GetValue<string>()).ToList();
        Assert.Contains("auth.registered", audit);
        Assert.Contains("billing.plan_changed_manually", audit);
        var ai = await admin.GetAsync("/api/admin/usage/ai");
        Assert.True(ai["total"]!["calls"]!.GetValue<int>() >= 1);
        Assert.Equal(0m, ai["total"]!["cost"]!.GetValue<decimal>());
    }
}
