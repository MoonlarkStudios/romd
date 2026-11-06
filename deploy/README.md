# ROMD deployment

ROMD supports a single-server Docker Compose installation with PostgreSQL 18,
one worker, the admin and consumer portals, and a separate browser player.
Production uses published images; it does not need an application checkout or SDK.

Download a versioned deployment bundle and verify its `.sha256` checksum. It
contains `images.env` with the exact image digests, `compose.yaml`, database
initialization assets, optional ingress examples, and backup/verification tools.
Copy `.env.example` to `.env`, fill it in, and follow
[the production runbook](../docs/self-hosting/production-deployment.md).
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

The Cloudflare example pins its connector to `ROMD_CLOUDFLARED_EDGE_IP` and
accepts visitor identity only from that address. When upgrading an existing
example, reserve this address outside the edge network's dynamic range and
recreate both ingress and cloudflared together; follow the runbook's
[Cloudflare setup](../docs/self-hosting/production-deployment.md#cloudflare-tunnel-example) in that runbook.

## Credits

Deployment bundles include `THIRD_PARTY_NOTICES.md`, the project license, and
asset/font notices. Each ROMD container carries that notice set under
`/usr/share/doc/romd/`. See the repository's central credits for individual
creators, sources, and component terms.
