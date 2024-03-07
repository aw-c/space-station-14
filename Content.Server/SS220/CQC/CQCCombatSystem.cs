// © SS220, An EULA/CLA with a hosting restriction, full text: https://raw.githubusercontent.com/SerbiaStrong-220/space-station-14/master/CLA.txt

using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Content.Shared.Actions;
using Content.Shared.Hands.Components;
using Content.Shared.SS220.CQCCombat;
using Content.Shared.Damage;
using Content.Shared.Pulling.Components;
using Content.Shared.StatusEffect;
using Content.Shared.Bed.Sleep;
using Content.Shared.Stunnable;
using Content.Shared.Humanoid;
using Content.Shared.Zombies;
using Content.Shared.Cuffs.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Server.Hands.Systems;
using Content.Server.Bed.Sleep;
using Content.Server.Cuffs;

namespace Content.Server.SS220.CQCCombat;

public sealed class CQCCombatSystem : CQCCombatSharedSystem
{
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SleepingSystem _sleeping = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly CuffableSystem _cuffs = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    private const string StatusEffectKey = "ForcedSleep"; // Same one used by N2O and other sleep chems.
    private const double SleepCooldown = 15;
    private const double BlowbackParalyze = 4;
    public override void Initialize()
    {
        SubscribeLocalEvent<CQCCanReadBook>(CanReadCQCBook);
        SubscribeLocalEvent<CQCCombatComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<CQCCombatComponent, ComponentRemove>(OnComponentRemove);
        SubscribeLocalEvent<CQCCombatComponent, CQCBlowbackEvent>(BaseAction);
        SubscribeLocalEvent<CQCCombatComponent, CQCPunchEvent>(BaseAction);
        SubscribeLocalEvent<CQCCombatComponent, CQCDisarmEvent>(BaseAction);
        SubscribeLocalEvent<CQCCombatComponent, CQCLongSleepEvent>(BaseAction);
    }
    private void CanReadCQCBook(CQCCanReadBook args)
    {
        args.Handled = true;
        if (HasComp<CQCCombatComponent>(args.Interactor))
        {
            args.Can = false;
            args.Cancelled = true;
            args.Reason = Loc.GetString("cqc-cannotlearn");
            return;
        }
        args.Can = true;
    }

    private void OnComponentInit(EntityUid uid, CQCCombatComponent component, ComponentInit args)
    {
        foreach (var proto in component.AvailableSpells)
        {
            var action = _actions.AddAction(uid, _prototypeManager.Index<CQCCombatSpellPrototype>(proto.Id).Entity);
            if (action is not null)
            {
                var comp = AddComp<CQCCombatInfosComponent>(action.Value);
                comp.Prototype = proto.Id;
            }
        }
    }
    private void OnComponentRemove(EntityUid uid, CQCCombatComponent component, ComponentRemove args)
    {
        var actions = _actions.GetActions(uid);
        var spellIds = component.AvailableSpells.Select(spell => spell.Id).ToHashSet();

        foreach (var (actionId, _) in actions)
            if (TryComp<CQCCombatInfosComponent>(actionId, out var comp) && spellIds.Contains(comp.Prototype))
                _actions.RemoveAction(actionId);
    }

    private EntityUid? GetTarget(EntityUid inflictor, BaseActionEvent args)
    {
        if (args is EntityTargetActionEvent actionEvent)
            return actionEvent.Target;
        if (args is InstantActionEvent)
        {
            if (TryComp<SharedPullerComponent>(inflictor, out var puller))
                return puller.Pulling;
        }
        return null;
    }

    private CQCCombatSpellPrototype? GetSpell(EntityUid? action)
    {
        if (action is null)
            return null;
        if (!TryComp<CQCCombatInfosComponent>(action, out var infosComponent))
            return null;

        foreach (var spell in _prototypeManager.EnumeratePrototypes<CQCCombatSpellPrototype>())
            if (infosComponent.Prototype == spell.ID)
                return spell;
        return null;
    }
    private bool CanTargettingAction(EntityUid inflictor, EntityUid target, Type eventType, [NotNullWhen(false)] out string? reason)
    {
        reason = "";
        if (TryComp<MobStateComponent>(inflictor, out var mobStateComp) && mobStateComp.CurrentState != MobState.Alive)
        {
            reason = "cqc-imnotalive"; //Я не могу использовать способности без сознания
            return false;
        }
        if (HasComp<ZombieComponent>(inflictor))
        {
            reason = "cqc-imthezombie"; //Я зомби
            return false;
        }
        if (eventType != typeof(CQCBlowbackEvent))
            if (TryComp<CuffableComponent>(inflictor, out var cuffComp) && _cuffs.GetAllCuffs(cuffComp).Count > 0)
            {
                reason = "cqc-icantusemyspellsincuffs"; //Я не могу использовать способности в наручниках
                return false;
            }
        if (!HasComp<HumanoidAppearanceComponent>(target))
        {
            reason = "cqc-icantattackthat"; //Я не могу атаковать это
            return false;
        }
        if (HasComp<CQCCombatComponent>(target))
        {
            reason = "cqc-equaltarget"; //Наши силы равны
            return false;
        }
        return true;
    }

    private void BaseAction(EntityUid inflictor, CQCCombatComponent comp, BaseActionEvent args)
    {
        if ((GetTarget(args.Performer, args) is { } target))
        {
            if (!CanTargettingAction(inflictor, target, args.GetType(), out var reason))
            {
                _popup.PopupEntity(Loc.GetString(reason), inflictor, inflictor);
                return;
            }

            switch (args)
            {
                case CQCBlowbackEvent:
                    OnBlowback(args.Performer, target);
                    break;
                case CQCDisarmEvent:
                    OnDisarm(args.Performer, target);
                    break;
                case CQCLongSleepEvent:
                    OnLongSleep(args.Performer, target);
                    break;
            }

            args.Handled = true;
            ApplyDamage(target, GetSpell(args.ActionId)?.Damage);

            _popup.PopupEntity(Loc.GetString("cqc-youwereattackebykaratist"), target, target);
            _popup.PopupEntity(Loc.GetString(GetPhrase()), inflictor, inflictor);

            return;
        }

        _popup.PopupEntity(Loc.GetString("cqc-therearenotarget"), inflictor, inflictor);
    }

    private string GetPhrase()
    {
        return "cqc-thereyougo" + _random.Next(0, 4);
    }

    private void ApplyDamage(EntityUid target, DamageSpecifier? damage)
    {
        if (damage is not null)
            _damage.TryChangeDamage(target, damage);
    }

    private void OnBlowback(EntityUid inflictor, EntityUid target)
    {
        _stun.TryParalyze(target, TimeSpan.FromSeconds(BlowbackParalyze), true);
    }

    private void OnDisarm(EntityUid inflictor, EntityUid target)
    {
        if (TryComp<HandsComponent>(target, out var handsComponent))
            foreach (var kvp in handsComponent.Hands)
                _hands.TryDrop(target, kvp.Value, null, false, false, handsComponent);
    }

    private void OnLongSleep(EntityUid inflictor, EntityUid target)
    {
        _sleeping.TrySleeping(target);
        _statusEffects.TryAddStatusEffect<ForcedSleepingComponent>(target, StatusEffectKey,
                TimeSpan.FromSeconds(SleepCooldown), true);
    }
}
