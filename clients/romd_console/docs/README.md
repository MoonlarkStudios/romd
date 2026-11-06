# Ottercade documentation

Technical documentation for the Ottercade console client. New here? Start with
the [client README](../README.md), [setup and use](using-ottercade.md), then
[development](development.md) if you are building from source.

## Build and contribute

- [Development](development.md): setup, running, native prerequisites, tasks
  and conventions.
- [Client instructions](../AGENTS.md): invariants for storage, authentication,
  controller-first UI and generated code.

## How it works

- [Architecture](architecture.md): profiles, server instances, authentication,
  local ownership, access and launch, persistence.
- [Emulator runtime pattern](emulator-runtime-pattern.md): runtime profiles,
  adapters, dependency resolution and provisioning.
- [Original disc sets](original-disc-sets.md): multi-disc install and repair.

## Design system

- [Experience system](experience-system.md): page anatomy, focus, motion and
  the "One Archive, Two Moods" direction.
- [Theme token governance](theme-token-governance.md): how theme values are
  owned and checked.
- [Branding](branding.md): the checked-in Ottercade kit, native asset placement,
  typography ownership and update commands.

## Decisions

These records follow the
[decision-record guide](../../../docs/decisions/README.md).

- [Flutter client](decisions/flutter-client.md)
- [Profile access and offline grants](decisions/profile-access-and-offline-grants.md)
- [Controller identity](decisions/controller-identity.md)
- [Controller mapping ownership](decisions/controller-mapping-ownership.md)
- [Virtual controller routing](decisions/virtual-controller-routing.md)
- Shared with the server:
  [consumer runtime boundary](../../../docs/decisions/consumer-runtime-boundary.md)

## Roadmaps

- [Controller identity](roadmaps/controller-identity.md): active controller
  work.
- [Dolphin runtime](roadmaps/dolphin-runtime.md): GameCube support via Dolphin.

## Research

Platform investigations that inform the controller decisions. Each records its
own date, platform and limits.

- [Runtime controller adapter policies](research/runtime-controller-policies/README.md)
- [Virtual controller routing](research/virtual-controller-routing/README.md)
