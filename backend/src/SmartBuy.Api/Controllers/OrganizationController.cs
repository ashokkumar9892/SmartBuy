using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartBuy.Api.Infrastructure;
using SmartBuy.Domain.Entities;
using SmartBuy.Domain.Rules;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;

namespace SmartBuy.Api.Controllers;

public sealed record OrganizationDto(Guid Id, string Name, string Slug, string Industry, bool IsDemo, decimal? EstimatedTaxRatePercent, string? PostalCode, string? ShippingAddress, Guid Version);

public sealed record UpdateOrganizationRequest(
    [Required, MaxLength(200)] string Name,
    Industry Industry,
    [Range(0, 30, ErrorMessage = "Tax rate must be between 0 and 30 percent.")] decimal? EstimatedTaxRatePercent,
    [MaxLength(20)] string? PostalCode,
    [MaxLength(500)] string? ShippingAddress,
    Guid? Version);

public sealed record MemberDto(Guid UserId, string Email, string FullName, string Role, bool IsActive, DateTime JoinedAt, DateTime? LastLoginAt, bool IsYou);

public sealed record AddMemberRequest(
    [Required, EmailAddress, MaxLength(320)] string Email,
    [Required, MaxLength(200)] string FullName,
    OrgRole Role,
    [MaxLength(200)] string? TemporaryPassword);

public sealed record UpdateMemberRequest(OrgRole Role);

[ApiController]
[Route("api/organization")]
[RequireRole]
public sealed class OrganizationController(AppDbContext db, AuditService audit, EntitlementService entitlements) : ApiController
{
    private static OrganizationDto ToDto(Organization o) =>
        new(o.Id, o.Name, o.Slug, o.Industry.ToString(), o.IsDemo, o.EstimatedTaxRatePercent, o.PostalCode, o.ShippingAddress, o.Version);

    [HttpGet]
    public async Task<OrganizationDto> Get(CancellationToken ct) =>
        ToDto(await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == OrganizationId, ct));

    [HttpPut, RequireRole(OrgRole.Administrator)]
    public async Task<OrganizationDto> Update(UpdateOrganizationRequest request, CancellationToken ct)
    {
        var organization = await db.Organizations.FirstAsync(o => o.Id == OrganizationId, ct);
        if (request.Version is { } version) db.Entry(organization).Property(o => o.Version).OriginalValue = version;
        organization.Name = request.Name.Trim();
        organization.Industry = request.Industry;
        organization.EstimatedTaxRatePercent = request.EstimatedTaxRatePercent;
        organization.PostalCode = string.IsNullOrWhiteSpace(request.PostalCode) ? null : request.PostalCode.Trim();
        organization.ShippingAddress = string.IsNullOrWhiteSpace(request.ShippingAddress) ? null : request.ShippingAddress.Trim();
        audit.Record("organization.updated", nameof(Organization), organization.Id);
        await db.SaveChangesAsync(ct);
        return ToDto(organization);
    }

    [HttpGet("members")]
    public async Task<List<MemberDto>> Members(CancellationToken ct)
    {
        var members = await db.OrganizationMembers.AsNoTracking().Include(m => m.User).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return members.Select(m => new MemberDto(m.UserId, m.User!.Email, m.User.FullName, m.Role.ToString(), m.User.IsActive, m.CreatedAt, m.User.LastLoginAt, m.UserId == UserId)).ToList();
    }

    /// <summary>Adds an existing user by email, or creates the account with a temporary password the administrator passes on.</summary>
    [HttpPost("members"), RequireRole(OrgRole.Administrator)]
    public async Task<ActionResult<MemberDto>> AddMember(AddMemberRequest request, CancellationToken ct)
    {
        if (!RolePermissions.CanAssignRole(Role, request.Role)) throw new ForbiddenException("You cannot grant a role higher than your own.");
        if (await db.Organizations.AnyAsync(o => o.Id == OrganizationId && o.IsDemo, ct))
            throw new BusinessRuleException("Team members cannot be added to a demo business. Create your own account to invite your team.", "demo_read_only");
        await entitlements.RequireMemberQuotaAsync(ct);

        var normalized = request.Email.Trim().ToUpperInvariant();
        User? user;
        using (Tenant.Elevate())
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct);
            if (user is null)
            {
                if (string.IsNullOrEmpty(request.TemporaryPassword))
                    throw new SmartBuy.Infrastructure.Services.ValidationException("No account exists for that email. Provide a temporary password to create one.");
                AuthController.ValidatePassword(request.TemporaryPassword);
                user = new User { Email = request.Email.Trim(), NormalizedEmail = normalized, FullName = request.FullName.Trim(), PasswordHash = PasswordHasher.Hash(request.TemporaryPassword) };
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
            }
            else if (user.IsDemo) throw new BusinessRuleException("Demo accounts cannot be added to an organization.");
        }

        if (await db.OrganizationMembers.AnyAsync(m => m.UserId == user.Id, ct))
            throw new BusinessRuleException("That person is already a member of this organization.", "already_member");
        var member = new OrganizationMember { UserId = user.Id, Role = request.Role };
        db.OrganizationMembers.Add(member);
        audit.Record("organization.member_added", nameof(User), user.Id, request.Role.ToString());
        await db.SaveChangesAsync(ct);
        return new MemberDto(user.Id, user.Email, user.FullName, member.Role.ToString(), user.IsActive, member.CreatedAt, user.LastLoginAt, false);
    }

    [HttpPut("members/{userId:guid}"), RequireRole(OrgRole.Administrator)]
    public async Task<IActionResult> UpdateMember(Guid userId, UpdateMemberRequest request, CancellationToken ct)
    {
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.UserId == userId, ct) ?? throw new NotFoundException("Member not found.");
        if (!RolePermissions.CanAssignRole(Role, request.Role) || member.Role > Role)
            throw new ForbiddenException("You cannot change the role of someone with a higher role than your own, or grant a role above your own.");
        if (member.Role == OrgRole.Owner && request.Role != OrgRole.Owner && await db.OrganizationMembers.CountAsync(m => m.Role == OrgRole.Owner, ct) <= 1)
            throw new BusinessRuleException("An organization must keep at least one owner.", "last_owner");
        member.Role = request.Role;
        audit.Record("organization.member_role_changed", nameof(User), userId, request.Role.ToString());
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("members/{userId:guid}"), RequireRole(OrgRole.Administrator)]
    public async Task<IActionResult> RemoveMember(Guid userId, CancellationToken ct)
    {
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.UserId == userId, ct) ?? throw new NotFoundException("Member not found.");
        if (member.Role > Role) throw new ForbiddenException("You cannot remove someone with a higher role than your own.");
        if (member.Role == OrgRole.Owner && await db.OrganizationMembers.CountAsync(m => m.Role == OrgRole.Owner, ct) <= 1)
            throw new BusinessRuleException("An organization must keep at least one owner.", "last_owner");
        db.OrganizationMembers.Remove(member);
        audit.Record("organization.member_removed", nameof(User), userId);
        await db.SaveChangesAsync(ct);
        using (Tenant.Elevate())
            await db.RefreshTokens.Where(t => t.UserId == userId && t.OrganizationId == OrganizationId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow), ct);
        return NoContent();
    }
}
