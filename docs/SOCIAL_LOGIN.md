# Turning on Google and Facebook sign-in

The code for both is already in the site. Each button appears automatically once its keys are set. Until then the
buttons stay hidden, because Google and Facebook only let a site use their sign-in after the site owner registers it
with them. That registration has to be done from **your** Google and Facebook accounts; nobody else can do it for you.

You need your live site address for both, for example `https://your-site.netlify.app`. Use the exact address, with
`https://` and no trailing slash. If you add a custom domain later, add that address in both places too.

Menu names on Google's and Facebook's sites change from time to time, so a label may differ slightly from what is
written here.

## Google (about 10 minutes)

1. Go to <https://console.cloud.google.com/> and sign in. Create a project (any name, e.g. "FindMyPrice").
2. Open **APIs & Services → OAuth consent screen**. Choose **External**, enter the app name "FindMyPrice", your
   support email and developer email, and save. Publish the app (move it out of "Testing") so anyone can sign in,
   not only test users you list.
3. Open **APIs & Services → Credentials → Create credentials → OAuth client ID**.
   - Application type: **Web application**
   - **Authorized JavaScript origins**: add your site address. For local testing also add `http://localhost:5173`.
   - No redirect URI is needed.
4. Copy the **Client ID** (it ends with `.apps.googleusercontent.com`). The client secret is not used.
5. In Netlify: **Site configuration → Environment variables → Add a variable**
   - Key `GOOGLE_CLIENT_ID`, value: the Client ID.
6. **Deploys → Trigger deploy → Deploy site.**

## Facebook (about 15 minutes)

1. Go to <https://developers.facebook.com/apps/> and sign in. Choose **Create app**, pick the option for letting
   people log in with their Facebook account, and name it "FindMyPrice".
2. Add the **Facebook Login** product (Web) if it was not added for you.
3. In **App settings → Basic**:
   - **App domains**: your site's domain, without `https://` (for example `your-site.netlify.app`)
   - Add platform **Website** and set **Site URL** to your site address
   - Fill in the Privacy Policy URL (Facebook requires one before the app can go live)
4. In **Facebook Login → Settings**: turn on **Login with the JavaScript SDK** and add your site address under
   **Allowed Domains for the JavaScript SDK**.
5. Make sure the app has the **email** permission (it is needed to create the account), then switch the app from
   **Development** to **Live**. In Development mode only you and listed testers can sign in.
6. From **App settings → Basic** copy the **App ID** and the **App secret**.
7. In Netlify add three environment variables:
   - `FACEBOOK_APP_ID`: the App ID
   - `FACEBOOK_APP_SECRET`: the App secret (keep this private; never put it in the code or share it)
   - `FACEBOOK_GRAPH_VERSION`: the API version shown at the top of your app's dashboard, for example `v21.0`
8. **Deploys → Trigger deploy → Deploy site.**

## Check it worked

Open the site's sign-in page. "Continue with Google" and/or "Continue with Facebook" now appear under the password
form. Sign in with each once. If a button shows but sign-in fails:

| What you see | Usual cause |
|---|---|
| Google pop-up says the origin is not allowed | The site address under *Authorized JavaScript origins* does not exactly match the address in the browser. |
| "Google sign-in could not be verified." | `GOOGLE_CLIENT_ID` in Netlify is not the same Client ID the address was registered under. |
| Facebook pop-up says the domain is not included | The site address is missing from *App domains* or *Allowed Domains for the JavaScript SDK*. |
| "Facebook did not share an email address." | The email permission is not granted, or the person's Facebook account has no confirmed email. |
| Works for you but not for others | The Google consent screen is still in Testing, or the Facebook app is still in Development mode. |

## If you host the full API instead

When the site uses the ASP.NET Core API (`VITE_API_BASE_URL` is set), put the same values on the API server instead:
`Google__ClientId`, `Facebook__AppId`, `Facebook__AppSecret`, `Facebook__GraphVersion`.

## Other sign-in options

Email and password works today with no setup. Other providers (Microsoft, Apple) are not built.
