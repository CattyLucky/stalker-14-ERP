using Content.Shared._Stalker_EN.RitualChasm;

namespace Content.Client._Stalker_EN.RitualChasm;

public sealed class RitualChasmSystem : SharedRitualChasmSystem
{
    // nothing on client
    protected override void PunishEntity(EntityUid uid) { }

    // nothing on client
    protected override void HandleReturnedEntity(EntityUid uid, Entity<RitualChasmComponent> ritualChasmEntity) { }
}
