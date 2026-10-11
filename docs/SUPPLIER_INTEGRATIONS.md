# Supplier integrations

## Current state

**Every supplier is served by a mock adapter.** No live supplier API is connected, because no approved credentials were
available. All offers are tagged `Demo` at the source and shown as **DEMO** in the UI, exports and PDFs. The mock
catalog (brands, model numbers, GTINs, prices, stock, delivery times, shipping fees) is invented; the shipping terms
attached to real retailer names are demonstration values, not those retailers' policies.

## Rules every adapter must follow

1. Use only officially permitted access: a documented API you have been approved for, or a merchant's own store
   credentials. **No scraping, no bypassing access controls.**
2. Never fabricate an affiliate link. `PurchaseLink.IsAffiliate` may be true only when a real tag from an approved
   program is configured.
3. Never report an order as placed unless the supplier's API returned a confirmation. No current adapter can place
   orders (`ProviderCapabilities.OrderPlacement` is false everywhere).
4. Report what you do not know as `null` (stock, delivery, shipping). The rest of the system shows it as "Unknown" and
   keeps it out of totals.
5. Respect the provider's caching and rate-limit terms via `Supplier.CacheTtlMinutes` and the adapter itself.

## Before enabling a live provider: checklist

Record the answers (with a link and the date checked) before switching a supplier from Mock to Live:

- [ ] **Eligibility:** which program grants access, and has this business been approved?
- [ ] **Authorization:** key, OAuth client credentials, or per-merchant token? Where are secrets stored?
- [ ] **Permitted uses:** may prices be shown next to competitors' prices? May they be stored? For how long?
- [ ] **Caching rules:** maximum age of a displayed price; whether a "price as of" timestamp is required.
- [ ] **Rate limits:** per second / per day; behaviour when exceeded.
- [ ] **Attribution and links:** required link format, disclosures, logo or wording rules.
- [ ] **Commercial terms:** fees, minimum sales, revenue share, termination conditions.
- [ ] **Coverage:** does the API expose inventory, delivery estimates, cart or checkout?

## Provider notes

These reflect general knowledge at the time of writing and **must be re-verified on each provider's own developer or
partner site** before any work starts. Terms change, and several programs are approval-based.

| Supplier | What a live adapter would need | Open questions to verify |
|---|---|---|
| **Home Depot** | No general public product/pricing API is assumed. A data feed or API would come through a partner or affiliate agreement. | Whether your account can get product, price and inventory data at all, and on what display terms. |
| **Lowe's** | Same position as Home Depot: assume no public API. | Partner or affiliate data access; display and caching terms. |
| **Amazon** | An approved Amazon Associates account plus access to its product-advertising API. | Current API name and version, qualifying-sales requirements, price display and caching limits, required disclosures. |
| **Walmart** | Approved access to Walmart's developer/affiliate product APIs. | Program eligibility, catalog coverage for trade materials, caching limits. |
| **eBay** | eBay developer keys and access to its Buy APIs (item search/browse) under the eBay API licence. | Which Buy APIs are open to new applications, partner-network requirements for outbound links, per-seller shipping data. |
| **Best Buy** | A Best Buy developer API key for its product catalog API. | Key approval, rate limits, attribution rules. Catalog overlaps only slightly with trade materials. |
| **Independent local suppliers** | No API. Onboard through a price-file import (CSV/Excel) they provide, or their own storefront if they have one. | Written permission to store and display their prices; how often the file is refreshed. |
| **Shopify merchants** | The merchant's own Storefront API access token (the merchant grants it). | Shipping is calculated at checkout, so report shipping as unknown unless the merchant gives flat rules. |
| **WooCommerce merchants** | The merchant's own REST API consumer key and secret. | Same as Shopify; inventory depends on the merchant's stock management settings. |

## Adding a live adapter

1. Implement `ISupplierProvider` (`backend/src/SmartBuy.Domain/Suppliers/ISupplierProvider.cs`): `SearchProductsAsync`,
   `GetProductDetailsAsync`, `GetOffersAsync`, `GetAvailabilityAsync`, `BuildPurchaseLinkAsync`, `GetProviderStatusAsync`.
   Return offers with `OfferDataSource.Live`.
2. Give it a unique `Key`, and register it in `DependencyInjection.AddSmartBuyInfrastructure` **only when its
   configuration is present**, so an unconfigured adapter never appears.
3. Set the supplier row's `ProviderKey` to that key and `Mode` to `Live` (Admin → Suppliers shows adapter, configuration
   and health status).
4. Set `WebsiteUrl` to the supplier's real domain: outbound links are refused unless they are HTTPS on that domain.
5. Treat product titles and descriptions as untrusted text. They are stored as data and never interpreted as
   instructions; keep it that way if you pass them to an AI provider.
6. Add tests with a stubbed `HttpMessageHandler` covering success, an error status, a timeout and malformed JSON.

The aggregation service already handles per-supplier timeouts and failures, records every call in `ProviderSyncLogs`,
keeps the last known offers (shown as **STALE** once past the cache window), and runs the matching engine on whatever
the adapter returns. Listings that match with low confidence go to Admin → Match review instead of being priced.

## Affiliate / referral revenue (optional module)

The pieces exist but are inactive: `Supplier.AffiliateProgramEnabled`, the `affiliate_links` feature flag, the
`AffiliateClicks` log (every outbound click is recorded with whether it carried a real tag), and the disclosure shown
in the "Continue at supplier" dialog. To activate it for a supplier, the live adapter's `BuildPurchaseLinkAsync` should
append the tag from configuration when both the flag and the supplier setting are on, and only after that supplier's
program has approved this use. Mock adapters never return affiliate links.
