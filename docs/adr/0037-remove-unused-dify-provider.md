# ADR 0037: Remove the unused Dify provider dependency

Date: 2026-11-27
Status: Accepted; supersedes the dependency release blocker in ADR 0036.

## Context

LIGClaw always configures its Cline runtime with `openai-compatible`; Dify
is not exposed by model settings or used by application code. Cline's pinned
model SDK nevertheless imports `dify-ai-provider` eagerly in its distributed
Node bundles and declares it as a dependency. That introduces Zod 3 and
GHSA-mrhx-c7xc-m3w5. Removing a root package entry or deleting node_modules
would either leave the indirect dependency or break SDK loading.

## Decision

Retain the 0.0.65 agent/runtime contract and use reviewed local Agents, Models,
and Shared SDK archives, version `0.0.65-ligclaw.2`. Remove Dify from the SDK dependency
manifest, all four provider registration catalogs, and the two Node bundle
imports/implementations. Retain legacy exported symbol names for source
compatibility but reject Dify explicitly. OpenAI-compatible providers remain
available; no Desktop/Sidecar protocol or data format changes.

Root npm overrides also route indirect SDK dependencies to these exact archives,
so npm cannot reintroduce the unmodified model SDK's Dify dependency. The source recipe checks the published upstream SHA-512 and
exact patch locations. It emits a deterministic tarball, SHA-256 provenance,
upstream attribution/license, and a notice of the modifications. See
`sidecar/vendor/README.md` for regeneration and review instructions.

Copy the vendor directory into the MSIX Sidecar staging directory before
production `npm ci`. This avoids a package that works only in a source checkout.
No audit bypass, install-time patch, stub package, or incompatible Zod override
is introduced. Reassess and remove the downstream patch when upstream makes
this provider optional or removes the dependency.

## Verification

Regression tests reject Dify/Zod 3 in the installed lock graph, check that
both Node and browser catalogs exclude Dify while retaining compatible
endpoints, and verify archive provenance and the installed SDK version.
Validation includes clean development and production installs, npm audit,
generated contracts, TypeScript, existing Cline replay/Tool tests, .NET
checks, and the actual Sidecar process lifecycle. The aggregate verification
script and its moderate-or-higher audit gate remain unchanged.

## Embedded code not covered by npm audit

The original Agents, Models and Shared distributions also embed Zod 4.4.3
inside seven JavaScript bundles. These copies are not separate dependency
entries, so removing Dify and achieving zero npm audit findings does not fix
them. Backport the upstream collection size guard from Zod commit
[a0ebf970f2d752e5f58527cd2942ff48be4f46bd](https://github.com/colinhacks/zod/commit/a0ebf970f2d752e5f58527cd2942ff48be4f46bd).

The checked-in TypeScript AST transformer inserts the guard at the same
Array/Map/Set constructor boundary. It rejects changed layouts and leaves
version markers intact. This 4.4.3 baseline predates the separate collection
compiler modified by the upstream commit; its object fast path delegates
to the guarded collection parser. The guard checks only leading bounds and
respects skipChecks/backward mode and an earlier transformation's ordering.
Actual embedded constructors in all seven bundles are tested for zero element
walks and one size issue on oversized inputs, in sync/async modes, alongside
a transformation-before-bound regression. Preserve the upstream MIT license
and a modification notice in every archive.

## Results on the review PC

- Aggregate `scripts/verify.ps1` passed with the unchanged audit severity gate.
- Node 94/94 and .NET 417/417 tests passed; build warnings/errors: zero.
- The final collection regressions also cover exact lengths/sizes, nested object
  JIT parsing, wrong input types, skipChecks, and backward mode in all seven bundles.
- Development and separately staged production `npm ci` and audits passed with
  zero reported vulnerabilities; the production runtime imported successfully.
- All three archives reproduced byte-for-byte from the pinned npm originals.
- Real-process Sidecar handshake, heartbeat, forced restart and cleanup passed.

User registry settings were not changed: review used process-local public npm
and NuGet source overrides because the PC's configured local proxy was offline.
Clean-account signed installation and Windows 10 hardware/live-provider checks
remain separate release gates; they were not part of this dependency correction.
