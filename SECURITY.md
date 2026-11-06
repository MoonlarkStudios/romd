# Security policy

## Report a vulnerability privately

Report vulnerabilities and exposed credentials through
[GitHub private vulnerability reporting](https://github.com/MoonlarkStudios/romd/security/advisories/new).
No separate reporting email address is offered.

Do not post vulnerabilities, credentials, authentication tokens, signed content
URLs or private service details in public issues, pull requests or discussions.
The bug-report form is for non-sensitive product defects only.

## What to include

- The affected release tag or commit and the affected surface (admin, consumer,
  worker, isolated player, console or deployment bundle).
- Your environment and deployment shape.
- The impact, including whether the issue crosses admin, consumer, worker or
  isolated-player authentication boundaries.
- A minimal reproduction using synthetic content, and any artifact or checksum
  needed to reproduce it safely.

Redact credentials and personal data before sending.

## What not to send

Do not send ROM collections, BIOS files, other people's data or a live secret to
demonstrate an issue. If a credential was exposed, say where and when; do not
include its value. Maintainers rotate or revoke confirmed exposed credentials.

## Supported versions

The first supported Linux amd64 server release is pending qualification, and
native console binaries are experimental on every target. No supported-version
security maintenance window or response-time commitment is published yet.
