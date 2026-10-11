# Showing real products and prices

FindMyPrice only shows a price as real when it comes straight from a supplier's official data service. It does not
copy prices off retailer web pages: that breaks those sites' terms and gets blocked. Until a supplier's service is
connected, the site falls back to a small sample catalog and labels every price **DEMO**.

So "real prices" means connecting at least one supplier. Each supplier decides who gets access:

| Supplier | How you get access | Self-serve? | Built into the site? |
|---|---|---|---|
| **eBay** | Free developer account at developer.ebay.com, then production keys | Yes | **Yes** (steps below) |
| Best Buy | Developer API key from developer.bestbuy.com (they review requests) | Request | Not yet |
| Amazon | Amazon Associates account, then product-advertising API access (needs qualifying sales) | Approval | Not yet |
| Walmart | Affiliate programme approval, then its product API | Approval | Not yet |
| Home Depot, Lowe's | No public price service; needs a partner or affiliate data agreement | No | Not yet |

Check each provider's own site for its current terms before relying on this table.

## Turn on live eBay prices (about 20 minutes)

1. Create an account at <https://developer.ebay.com/> and sign in.
2. Under **Application Keys**, create a keyset for **Production** (not Sandbox). eBay asks you to either subscribe to
   or opt out of "marketplace account deletion" notifications before it activates production keys; a site that stores
   no eBay user data can choose the exemption.
3. Copy the **App ID (Client ID)** and the **Cert ID (Client Secret)**.
4. In Netlify: **Site configuration → Environment variables**, add
   - `EBAY_CLIENT_ID`: the App ID
   - `EBAY_CLIENT_SECRET`: the Cert ID (keep it private)
   - optional `EBAY_MARKETPLACE`: default is `EBAY_US`
5. **Deploys → Trigger deploy → Deploy site.**

After that, every search returns current eBay listings for whatever the customer types, with the real price, shipping
where the seller states it, a photo, and a **Buy at eBay** button that opens that exact listing. Results are labelled
**LIVE**. The sample catalog is no longer shown; if eBay cannot be reached, the site says so and shows sample prices
for that search.

What customers see is a list of separate listings that matched their words, cheapest first. They are not verified to
be the identical item, so the page says to check each one.

### Earning commission (optional)

Join the eBay Partner Network, create a campaign, and add `EBAY_CAMPAIGN_ID` in Netlify. Links then become affiliate
links and the results page says so.

## Status

The eBay connection is written to eBay's published Browse API but **has not been run against eBay**, because that
needs your keys. Expect to test it once after adding them. It is only in the Netlify site's own API
(`frontend/netlify/functions/api.mts`); the full ASP.NET Core API still uses demo adapters.
