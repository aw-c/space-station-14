// © SS220, An EULA/CLA with a hosting restriction, full text: https://raw.githubusercontent.com/SerbiaStrong-220/space-station-14/master/CLA.txt

using Content.Shared.SS220.Missable;
using Content.Shared.CombatMode;

namespace Content.Shared.SS220.CQCCombat;
public partial class CQCCombatSharedSystem : EntitySystem
{
    private const float MissChance = 0.8f;
    public override void Initialize()
    {
        SubscribeLocalEvent<CQCCombatComponent, MissableMissChanceBonusEvent>(RollChanceToMiss);
    }

    private void RollChanceToMiss(EntityUid entity, CQCCombatComponent comp, MissableMissChanceBonusEvent args)
    {
        if (!TryComp<CombatModeComponent>(entity, out var combat))
            return;
        if (!combat.IsInCombatMode)
            return;

        args.Handled = true;
        args.BonusMiss.Add(MissChance);
    }
}