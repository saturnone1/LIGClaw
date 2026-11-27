# Reviewed Cline SDK packages

These local archives preserve the Cline 0.0.65 agent/runtime contracts while
removing the unused Dify model provider and backporting Zod's collection size
guard into the SDK's embedded Zod 4.4.3 copies. All three packages use the
explicit downstream version `0.0.65-ligclaw.2`.

- Cline upstream: https://github.com/cline/cline (Apache-2.0).
- Zod upstream fix: https://github.com/colinhacks/zod/commit/a0ebf970f2d752e5f58527cd2942ff48be4f46bd (MIT).
- Each provenance JSON pins the original npm SHA-512, patched archive SHA-256,
  changed runtime files, and backport revision. Archives include both licenses,
  the original README, and a notice of the modifications.

The model SDK patch removes `dify-ai-provider` from dependencies and removes
Dify from four catalogs and two Node imports/implementations. Legacy enum and
factory symbol names remain for source compatibility; Dify is not an installed
provider and the legacy factory explicitly rejects it.

The upstream packages embed Zod independently of the npm dependency graph.
A zero-result npm audit alone would not validate that code. The AST recipe
adds the upstream leading-size guard to Array/Map/Set constructors in all seven
embedded runtime bundles, including the Agents and Shared SDKs. Original Zod
version markers are retained; this is a documented backport. Element parsing
is skipped for oversized bounded collections, while preceding transformations,
async parsing, invalid-type handling, and backward/skip-check contexts retain
their ordering. Regression tests exercise the actual embedded constructors.

Root npm overrides route both direct and indirect SDK dependencies to these
archives. No install-time patch, missing-module shim, advisory suppression, or
forced Zod 3 major upgrade is used. Normal `npm ci` needs only Node/npm; MSIX
staging copies the vendor directory before production restore.

## Rebuild

Use Node 24, Python 3, and the locked development TypeScript parser. First run
`npm ci --prefix sidecar` using the checked-in archives. Download the three
published originals into a temporary directory with `npm pack`:

```powershell
npm pack @cline/llms@0.0.65 --pack-destination <temporary-directory>
npm pack @cline/agents@0.0.65 --pack-destination <temporary-directory>
npm pack @cline/shared@0.0.65 --pack-destination <temporary-directory>
python scripts/vendor-cline-without-dify.py <temporary-directory>/cline-llms-0.0.65.tgz --agents <temporary-directory>/cline-agents-0.0.65.tgz --shared <temporary-directory>/cline-shared-0.0.65.tgz
```

The recipe verifies upstream integrity and exact patch locations. It parses
bundles with the locked TypeScript AST, requires exactly three collection
constructors per bundle, and writes deterministic archives and provenance.
Rebuilding from the same originals must reproduce all hashes. Review a new
upstream version explicitly instead of broadening these preconditions.
