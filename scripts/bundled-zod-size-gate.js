// Backport of the collection size gate in colinhacks/zod a0ebf970f2d752e5f58527cd2942ff48be4f46bd.
// Zod is MIT licensed; see sidecar/vendor/LICENSE.zod and the vendor provenance.
// Keep the original bundled version marker: this is a documented backport, not a version upgrade.
function __ligclawCapCollection(schema, kind) {
  const leadingCaps = () => {
    const caps = [];
    for (const check of schema._zod.def.checks ?? []) {
      const definition = check._zod.def;
      if (!/_(length|size)$|_equals$/.test(definition.check)) break;
      const maximum = definition.maximum ?? definition.length ?? definition.size;
      if (maximum >= 0) caps.push([maximum, check]);
    }
    return caps;
  };
  const caps = leadingCaps();
  if (!caps.length) return;
  const limit = Math.min(...caps.map(([maximum]) => maximum));
  const parse = schema._zod.parse;
  schema._zod.parse = (payload, context) => {
    const value = payload.value;
    const size = kind === "Array" ? (Array.isArray(value) ? value.length : -1)
      : kind === "Map" ? (value instanceof Map ? value.size : -1)
        : (value instanceof Set ? value.size : -1);
    if (size > limit && !context?.skipChecks && context?.direction !== "backward") {
      for (const [maximum, check] of leadingCaps()) {
        if (size <= maximum || !check._zod.def.when(payload)) continue;
        const start = payload.issues.length;
        check._zod.check(payload);
        payload.issues[start].continue = false;
        for (let index = start; index < payload.issues.length; index++) {
          payload.issues[index].schema ??= schema;
        }
        return payload;
      }
    }
    return parse(payload, context);
  };
}
