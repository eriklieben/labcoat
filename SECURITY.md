# Security

## Supported versions

Security fixes go into the latest released version only.

| Version | Supported |
| --- | --- |
| latest release | yes |
| older releases | no |

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Report it privately through GitHub:
[Report a vulnerability](https://github.com/eriklieben/labcoat/security/advisories/new) (Security tab →
"Report a vulnerability").

Include what you can of:

- the Labcoat version (`labcoat --version`) and how it was started (tool, `dnx`, Aspire hosting, `--labcoat`);
- the operating system and browser;
- steps to reproduce or a proof of concept, and the impact you see (for example: a web page that can start test runs,
  read files or reach the API from another origin).

You'll get an acknowledgement within a week. Once a fix is ready, it is released and a GitHub security advisory is
published, crediting you unless you prefer otherwise.

Labcoat runs your test code and builds your projects: running it on a repository you don't trust is equivalent to
running that repository's code. That on its own is not a vulnerability in Labcoat.

## Threat model

Labcoat is a local development server: it listens on loopback (`http://localhost:<port>`), builds and runs your tests,
serves your source files to its UI and can start an editor. It has no user accounts. The people it must keep out are
**web pages in your browser** (any site you visit can make your browser talk to `localhost`) and, to a lesser degree,
**other processes on the machine**. The checks live in `src/Labcoat/Security.cs` (`RequestGuard`, `SecurityHeaders`).

### What Labcoat protects against

| Attack | Defence |
| --- | --- |
| **DNS rebinding**: `attacker.example` resolves to `127.0.0.1`, so the attacker's page becomes "same origin" with Labcoat and reads `/api/state` or `/api/source` | Every request's `Host` header must be an allowed name: `localhost`, `*.localhost`, loopback IPs (`127.0.0.1`, `[::1]`), the listen host, or `LABCOAT_ALLOWED_HOSTS` (comma-separated; Labcoat.Aspire.Hosting adds the proxied endpoint's host). Anything else gets **400** before routing, including static files and `/ws`. |
| **Cross-site request forgery**: a page posts to `/api/run`, `/api/open-in-editor`, `/api/watch`, … (a `text/plain` form/fetch needs no CORS preflight) | Every non-GET `/api/*` request must come from the **same origin** (the `Origin` host:port equals the `Host` header and the host is allowed) or from an origin listed in `LABCOAT_ALLOWED_ORIGINS`; otherwise **403**. JSON bodies must be sent as `Content-Type: application/json` (**415** otherwise), which a cross-site page can't do without a preflight that Labcoat never approves (no CORS headers are ever sent). |
| **Cross-site WebSocket hijacking**: a page opens `ws://localhost:4300/ws`, receives the state (test names, errors, stdout, file paths) and sends commands | The `/ws` handshake gets the same Origin check (**403**). Browsers always send `Origin` on WebSocket handshakes. |
| **Clickjacking** | `Content-Security-Policy: frame-ancestors 'none'` and `X-Frame-Options: DENY`. |
| Injected script / content sniffing | CSP `default-src 'self'` (scripts: own files plus the SHA-256 hashes of `index.html`'s inline scripts, computed at runtime; styles: own files + inline style attributes; images/fonts: self + `data:`; `connect-src` self + `ws(s)://<host>`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`. |
| Reading arbitrary files through the API | `GET /api/source` and `POST /api/open-in-editor` only accept files under the source roots (CLI paths, `--root`, project directories and their ProjectReference closure) or files named by test locations / the coverage report. Paths are canonicalized with symlinks resolved before the check (traversal, `/a/b` vs `/a/bc`, symlinks pointing out of a root → **403**); 2 MB max, binary files refused. |

**Clients without an `Origin` header** (curl, scripts, the Aspire dashboard commands) are accepted for state-changing
requests only over **loopback**, and only if they don't carry a cross-site `Sec-Fetch-Site` header. Browsers send
`Origin` on every WebSocket handshake and every non-GET `fetch`/form submission, so a web page cannot pose as such a
client. When **`LABCOAT_TOKEN`** is set, those clients must also send it as `X-Labcoat-Token`: Labcoat.Aspire.Hosting
generates a random token per AppHost run, passes it to the explorer and sends it with every dashboard command, so other
local processes cannot drive an Aspire-hosted Labcoat without reading the AppHost's resource environment. The browser
UI (same origin) never needs the token. Read-only `GET` endpoints are not origin-checked: a cross-origin page can send
them but cannot read the answers (no CORS), and the Host check stops rebinding.

### What it does not protect against

- **Code you run.** Labcoat builds and executes the tests of the workspace you point it at; a malicious repository
  owns your machine as soon as its tests (or MSBuild targets) run.
- **Other users on the same machine** when no token is set. Loopback is shared by every local user: on a multi-user
  host anyone can `curl localhost:<port>/api/state` and, without `LABCOAT_TOKEN`, start runs. Set `LABCOAT_TOKEN` (and
  keep in mind that read-only endpoints stay open) or don't run Labcoat on shared machines.
- **Exposing it on a network.** Binding to a non-loopback address (`ASPNETCORE_URLS=http://0.0.0.0:4300`) or setting
  `LABCOAT_ALLOWED_HOSTS=*` (turns the Host check off; Labcoat warns) puts an unauthenticated API that runs code on
  that network. Requests without `Origin` from non-loopback addresses are still refused, but a browser on another
  machine is not. Use an SSH tunnel (`ssh -L 4300:localhost:4300 host`) instead.
- **Development setups** that serve the UI from another origin (the Vite dev server on `:5174`) must list that origin in
  `LABCOAT_ALLOWED_ORIGINS`, or make the dev proxy rewrite `Origin` to the backend's origin.

Found a way around one of these checks? Please report it as described above.
