# ROMD deployment

ROMD supports a single-server Docker Compose installation with PostgreSQL 18,
one worker, the admin and consumer portals, and a separate browser player.
Production uses published images; it does not need an application checkout or SDK.

To install from this release directory, create `.env` and start the stack:

```bash
cat > .env <<'EOF'
ROMD_HOST=nas.local
ROMD_DEFAULT_ADMIN_EMAIL=you@example.com
ROMD_DEFAULT_ADMIN_PASSWORD=choose-a-strong-password
EOF
chmod 600 .env
docker compose up -d --wait --wait-timeout 240
```

ROMD generates its own database passwords, signing secrets and HTTPS
certificate. Follow the [quick start](../docs/self-hosting/production-deployment.md#quick-start)
to trust that certificate and sign in, or
[the production runbook](../docs/self-hosting/production-deployment.md) for
every option. The release contains `images.env` with the exact image digests,
`compose.yaml`, database initialization assets, the built-in ingress
configuration, optional ingress and library overlays, and backup and
verification tools.
The bundle includes that guide as `docs/production-deployment.md`, with the
configuration and media-processing references beside it. References to omitted
source documentation use the bundle's exact source revision.

In a source checkout, the same runbook is at `../docs/self-hosting/production-deployment.md`
and the production environment template is `deploy/.env.example`. Development
uses `compose.dev.yaml`. `compose.build.yaml` is a maintainer-only overlay for
building and testing production images from source.

Keep installation-specific configuration in your own infrastructure repository:
public origins, tunnel routing, host paths, image selection, monitoring and backup
schedules. Keep plaintext credentials out of Git. The examples are optional;
ROMD does not require a particular DNS, proxy, tunnel or hosting provider.

Docker assigns every address. Each application shares a network only with the
ingress, and the Cloudflare example gives the connector and the ingress a network
of their own, where alone the tunnel listener accepts a visitor's identity. See
[network boundaries](../docs/self-hosting/production-deployment.md#network-boundaries),
and [upgrading from the `edge` network](../docs/self-hosting/production-deployment.md#upgrading-from-a-release-with-the-edge-network)
for an installation that set addresses.

## Credits

Deployment bundles include `THIRD_PARTY_NOTICES.md`, the project license, and
asset/font notices. Each ROMD container carries that notice set under
`/usr/share/doc/romd/`. See the repository's central credits for individual
creators, sources, and component terms.
