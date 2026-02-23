using System.Threading.Tasks;
using Content.Server._Stalker.Anomaly.Generation.Components;
using Content.Server._Stalker.Anomaly.Generation.Systems;
using Content.Shared._Stalker.Anomaly.Prototypes;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.Emission;

/// <summary>
/// Orchestrates staggered anomaly deletion and regeneration during emissions.
/// Deletion begins during Stage 2 (while players shelter), regeneration begins after
/// all deletions complete (typically during Stage 3). Maps are processed one at a time
/// with configurable stagger intervals to prevent tick spikes.
/// </summary>
public sealed class STEmissionAnomalyRegenSystem : EntitySystem
{
    [Dependency] private readonly STAnomalyGeneratorSystem _anomalyGenerator = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<EmissionAnomalyRegenComponent, EmissionEventRuleComponent>();
        while (query.MoveNext(out _, out var regen, out var emission))
        {
            if (!regen.Enabled)
                continue;

            ProcessPhase(regen, emission);
        }
    }

    private void ProcessPhase(EmissionAnomalyRegenComponent regen, EmissionEventRuleComponent emission)
    {
        switch (regen.Phase)
        {
            case EmissionRegenPhase.Idle:
                if (emission.Stage == EmissionStage.Stage2)
                {
                    regen.Phase = EmissionRegenPhase.WaitingForDeletion;
                    regen.NextAction = _timing.CurTime + regen.DeletionDelay;
                    BuildMapLists(regen);
                    Log.Info("Emission anomaly regen: entering deletion wait phase");
                }
                break;

            case EmissionRegenPhase.WaitingForDeletion:
                if (_timing.CurTime >= regen.NextAction)
                {
                    regen.Phase = EmissionRegenPhase.Deleting;
                    regen.CurrentMapIndex = 0;
                    regen.NextAction = _timing.CurTime;
                }
                break;

            case EmissionRegenPhase.Deleting:
                ProcessDeletion(regen);
                break;

            case EmissionRegenPhase.WaitingForRegeneration:
                if (_timing.CurTime >= regen.NextAction)
                {
                    regen.Phase = EmissionRegenPhase.Regenerating;
                    regen.CurrentMapIndex = 0;
                    regen.NextAction = _timing.CurTime;
                }
                break;

            case EmissionRegenPhase.Regenerating:
                ProcessRegeneration(regen);
                break;

            case EmissionRegenPhase.Complete:
                break;
        }
    }

    private void ProcessDeletion(EmissionAnomalyRegenComponent regen)
    {
        if (_timing.CurTime < regen.NextAction)
            return;

        if (regen.CurrentMapIndex < regen.PendingDeletionMaps.Count)
        {
            var mapId = regen.PendingDeletionMaps[regen.CurrentMapIndex];
            _anomalyGenerator.ClearGeneration(mapId);
            regen.CurrentMapIndex++;
            regen.NextAction = _timing.CurTime + regen.DeletionStaggerInterval;
            Log.Info($"Emission anomaly regen: cleared map {mapId} ({regen.CurrentMapIndex}/{regen.PendingDeletionMaps.Count})");
        }
        else
        {
            regen.Phase = EmissionRegenPhase.WaitingForRegeneration;
            regen.NextAction = _timing.CurTime + regen.RegenerationDelay;
            Log.Info("Emission anomaly regen: all maps cleared, waiting for regeneration phase");
        }
    }

    private void ProcessRegeneration(EmissionAnomalyRegenComponent regen)
    {
        if (_timing.CurTime < regen.NextAction)
            return;

        if (regen.CurrentMapIndex < regen.PendingRegenerationMaps.Count)
        {
            var (mapId, optionsProtoId) = regen.PendingRegenerationMaps[regen.CurrentMapIndex];

            if (_prototype.TryIndex(optionsProtoId, out var optionsProto))
            {
                _ = _anomalyGenerator.StartGeneration(mapId, optionsProto.Options).ContinueWith(
                    t => Log.Error($"Emission anomaly regen: generation failed for map {mapId}: {t.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted);
                Log.Info($"Emission anomaly regen: started generation on map {mapId} ({regen.CurrentMapIndex + 1}/{regen.PendingRegenerationMaps.Count})");
            }
            else
            {
                Log.Warning($"Emission anomaly regen: failed to index options prototype '{optionsProtoId}' for map {mapId}");
            }

            regen.CurrentMapIndex++;
            regen.NextAction = _timing.CurTime + regen.RegenerationStaggerInterval;
        }
        else
        {
            regen.Phase = EmissionRegenPhase.Complete;
            Log.Info("Emission anomaly regen: all maps queued for regeneration");
        }
    }

    /// <summary>
    /// Builds the ordered list of maps to delete/regenerate from STAnomalyGeneratorTargetComponent entities.
    /// Sorts smaller maps first to spread load (small maps finish faster).
    /// </summary>
    private void BuildMapLists(EmissionAnomalyRegenComponent regen)
    {
        regen.PendingDeletionMaps.Clear();
        regen.PendingRegenerationMaps.Clear();

        var query = EntityQueryEnumerator<MapComponent, STAnomalyGeneratorTargetComponent>();
        var entries = new List<(MapId MapId, ProtoId<STAnomalyGenerationOptionsPrototype> OptionsId, int TotalCount)>();

        while (query.MoveNext(out _, out var mapComp, out var target))
        {
            if (!_prototype.TryIndex(target.OptionsId, out var optionsProto))
                continue;

            entries.Add((mapComp.MapId, target.OptionsId, optionsProto.Options.TotalCount));
        }

        // Sort by TotalCount ascending -- delete and regenerate small maps first
        entries.Sort((a, b) => a.TotalCount.CompareTo(b.TotalCount));

        foreach (var (mapId, optionsId, _) in entries)
        {
            regen.PendingDeletionMaps.Add(mapId);
            regen.PendingRegenerationMaps.Add((mapId, optionsId));
        }

        Log.Info($"Emission anomaly regen: found {entries.Count} maps to regenerate");
    }
}
