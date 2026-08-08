# Radar — PoE2

Shared terrain, target, map-image, and route service for ExileCore2.

## Logic

1. Read PoE2 terrain/AreaGraph data and build a walkability/image model for the
   current area.
2. Match configured target metadata and maintain area-scoped target state.
3. Run route requests on cancellable workers with nearest-walkable fallback and
   finite/path bounds checks.
4. Copy worker results before publication so stale area callbacks cannot replace
   a newer route.
5. Render terrain, targets, paths, and map images; publish bridge methods used
   primarily by `ExileCampaigns2`.

Bridge contracts: `Radar.LookForRoute`, `Radar.ClusterTarget`,
`Radar.GetMapImage`, and `Radar.GetMapSvg`.

## Status

Build: **PASS**. Classification: **CURRENT_WITH_WARNINGS**; terrain offsets and
route quality need a live 0.5.4e traversal.

Detailed report: [PoE2 plugin catalog](../../README.md) ·
[audit](../../../docs/plugins/Radar/AUDIT.md).
