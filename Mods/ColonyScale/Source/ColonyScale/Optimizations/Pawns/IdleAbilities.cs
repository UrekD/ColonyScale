using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;

namespace ColonyScale.Optimizations.Pawns
{
    /// <summary>
    /// Pawn_AbilityTracker.AbilitiesTick calls Ability.AbilityTick every tick for every ability a pawn has (psycasters
    /// carry dozens). Almost always the ability is idle and every step is a no-op, yet each tick evaluates Casting up
    /// to six times and walks verbs and comps. The single call in AbilitiesTick is swapped for a helper that skips
    /// the call when every step is provably a no-op; the loop itself (and its AllAbilitiesForReading calls) is untouched.
    ///
    /// Exact, for abilities whose runtime type is exactly Ability or Psycast (anything else runs vanilla). Given the
    /// checked state, each step of Ability.AbilityTick does nothing:
    ///   VerbTracker.VerbsTick: tracker and verb list exist (else vanilla, which would create them), every verb is
    ///     not in VerbState.Bursting and has no maintained effecters, so Verb.VerbTick (non-virtual) does nothing;
    ///   emitted fleck, MoteTick, WarmupTick (Casting is false implies !verb.WarmingUp), CastingTick (warmupEffecter is
    ///     null), SoundTick and Psycast's mote all require Casting; EffectersTick has an empty list;
    ///   CooldownTick returns on (!inCooldown || !CanCooldown || CooldownTicksRemaining > 0), evaluated here as vanilla;
    ///   every AbilityComp's runtime type inherits AbilityComp.CompTick, which is empty;
    ///   wasCastingOnPrevTick is false and Casting is false, so lastCastTick is untouched and the field keeps false.
    /// Casting, CanCooldown and CooldownTicksRemaining only read state and nothing changes state in between, so one
    /// evaluation equals vanilla's several. The one side effect of the idle path, MoteTick's lazy load of
    /// AbilityDef.WarmupMoteSocialSymbol, is reproduced. No randomness is used on the idle path (the only Rand call is
    /// the cooldown restart, which is excluded). If evaluating the conditions throws, vanilla runs instead.
    /// </summary>
    public sealed class IdleAbilities : Optimization
    {
        private const string OptId = "idleAbilities";

        public override string Id => OptId;
        public override string Label => "Skip idle ability ticks";
        public override string Description =>
            "Abilities and psycasts that are not being cast, have no running effects and are not finishing a cooldown are not ticked. " +
            "Helps most with many psycasters.";
        public override string Category => "Pawns";

        private static readonly MethodInfo AbilityTickMethod = AccessTools.Method(typeof(Ability), nameof(Ability.AbilityTick));
        private static readonly MethodInfo TickAbilityMethod = AccessTools.Method(typeof(IdleAbilities), nameof(TickAbility));
        private static readonly MethodInfo AbilityCompTick = AccessTools.Method(typeof(AbilityComp), nameof(AbilityComp.CompTick));

        private static AccessTools.FieldRef<Ability, VerbTracker> verbTrackerRef;
        private static AccessTools.FieldRef<VerbTracker, List<Verb>> verbsRef;
        private static AccessTools.FieldRef<Verb, List<Tuple<Effecter, TargetInfo, TargetInfo>>> verbEffectersRef;
        private static AccessTools.FieldRef<Ability, List<Tuple<Effecter, TargetInfo, TargetInfo>>> effectersRef;
        private static AccessTools.FieldRef<Ability, Effecter> warmupEffecterRef;
        private static AccessTools.FieldRef<Ability, bool> wasCastingRef;
        private static AccessTools.FieldRef<Ability, bool> inCooldownRef;

        // Verify mode only.
        private static AccessTools.FieldRef<Ability, Mote> warmupMoteRef;
        private static AccessTools.FieldRef<Ability, Sustainer> soundCastRef;
        private static AccessTools.FieldRef<Ability, int> cooldownEndTickRef;
        private static AccessTools.FieldRef<Psycast, Mote> moteCastRef;
        private static AccessTools.FieldRef<Verb, int> burstShotsLeftRef;
        private static AccessTools.FieldRef<Verb, int> ticksToNextBurstShotRef;
        private static Func<ulong> randState;

        public override IEnumerable<PatchSpec> GetPatches()
        {
            verbTrackerRef = AccessTools.FieldRefAccess<Ability, VerbTracker>("verbTracker");
            verbsRef = AccessTools.FieldRefAccess<VerbTracker, List<Verb>>("verbs");
            verbEffectersRef = AccessTools.FieldRefAccess<Verb, List<Tuple<Effecter, TargetInfo, TargetInfo>>>("maintainedEffecters");
            effectersRef = AccessTools.FieldRefAccess<Ability, List<Tuple<Effecter, TargetInfo, TargetInfo>>>("maintainedEffecters");
            warmupEffecterRef = AccessTools.FieldRefAccess<Ability, Effecter>("warmupEffecter");
            wasCastingRef = AccessTools.FieldRefAccess<Ability, bool>("wasCastingOnPrevTick");
            inCooldownRef = AccessTools.FieldRefAccess<Ability, bool>("inCooldown");
            warmupMoteRef = AccessTools.FieldRefAccess<Ability, Mote>("warmupMote");
            soundCastRef = AccessTools.FieldRefAccess<Ability, Sustainer>("soundCast");
            cooldownEndTickRef = AccessTools.FieldRefAccess<Ability, int>("cooldownEndTick");
            moteCastRef = AccessTools.FieldRefAccess<Psycast, Mote>("moteCast");
            burstShotsLeftRef = AccessTools.FieldRefAccess<Verb, int>("burstShotsLeft");
            ticksToNextBurstShotRef = AccessTools.FieldRefAccess<Verb, int>("ticksToNextBurstShot");
            var randGetter = AccessTools.PropertyGetter(typeof(Rand), "StateCompressed") ?? throw new MissingMethodException(nameof(Rand), "StateCompressed");
            randState = AccessTools.MethodDelegate<Func<ulong>>(randGetter);

            yield return PatchSpec.For(AccessTools.Method(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.AbilitiesTick)))
                .WithTranspiler(typeof(IdleAbilities), nameof(AbilitiesTickTranspiler));
        }

        public override IEnumerable<MethodBase> DependsOn => new MethodBase[]
        {
            AbilityTickMethod,
            AccessTools.Method(typeof(Psycast), nameof(Psycast.AbilityTick)),
            AccessTools.Method(typeof(Ability), "MoteTick"),
            AccessTools.Method(typeof(Ability), "EffectersTick"),
            AccessTools.Method(typeof(Ability), "WarmupTick"),
            AccessTools.Method(typeof(Ability), "CastingTick"),
            AccessTools.Method(typeof(Ability), "SoundTick"),
            AccessTools.Method(typeof(Ability), "CooldownTick"),
            AccessTools.PropertyGetter(typeof(Ability), nameof(Ability.Casting)),
            AccessTools.PropertyGetter(typeof(Ability), nameof(Ability.CanCooldown)),
            AccessTools.PropertyGetter(typeof(Ability), nameof(Ability.HasCooldown)),
            AccessTools.PropertyGetter(typeof(Ability), nameof(Ability.CooldownTicksRemaining)),
            AccessTools.PropertyGetter(typeof(Ability), nameof(Ability.verb)),
            AccessTools.PropertyGetter(typeof(Ability), nameof(Ability.VerbTracker)),
            AbilityCompTick,
            AccessTools.Method(typeof(VerbTracker), nameof(VerbTracker.VerbsTick)),
            AccessTools.PropertyGetter(typeof(VerbTracker), nameof(VerbTracker.PrimaryVerb)),
            AccessTools.Method(typeof(Verb), nameof(Verb.VerbTick)),
            AccessTools.PropertyGetter(typeof(Verb), nameof(Verb.WarmingUp)),
            AccessTools.PropertyGetter(typeof(Verb), nameof(Verb.WarmupStance)),
            AccessTools.PropertyGetter(typeof(AbilityDef), nameof(AbilityDef.WarmupMoteSocialSymbol)),
        };

        public override void ClearCaches()
        {
            lock (sync) overridesCompTick = new Dictionary<Type, bool>();
            compTypesByDef = new CompTypes[0];
        }

        public static IEnumerable<CodeInstruction> AbilitiesTickTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.SwapCalls(instructions, OptId, AbilityTickMethod, TickAbilityMethod, 1);

        /// <summary>Replaces <c>callvirt Ability::AbilityTick()</c> in Pawn_AbilityTracker.AbilitiesTick.</summary>
        public static void TickAbility(Ability ability)
        {
            if (IsIdle(ability))
            {
                if (Verify.Enabled) VerifyIdle(ability);
                return;
            }
            ability.AbilityTick();
        }

        private static bool IsIdle(Ability ability)
        {
            Type type = ability.GetType(); // null: same NullReferenceException as vanilla's callvirt
            if (type != typeof(Ability) && type != typeof(Psycast)) return false;
            AbilityDef def = ability.def;
            if (ability.pawn == null || def == null) return false;
            if (wasCastingRef(ability) || warmupEffecterRef(ability) != null) return false;
            var effecters = effectersRef(ability);
            if (effecters == null || effecters.Count != 0) return false;

            VerbTracker tracker = verbTrackerRef(ability);
            if (tracker == null) return false;
            List<Verb> verbs = verbsRef(tracker);
            if (verbs == null) return false;
            for (int i = 0; i < verbs.Count; i++)
            {
                Verb verb = verbs[i];
                if (verb == null || verb.state == VerbState.Bursting || verbEffectersRef(verb).Count != 0) return false;
            }

            if (!CompsTickIdle(def, ability.comps)) return false;

            try
            {
                if (inCooldownRef(ability) && ability.CanCooldown && ability.CooldownTicksRemaining <= 0) return false;
                if (ability.verb == null || ability.Casting) return false;
            }
            catch (Exception)
            {
                return false; // vanilla runs and fails the same way
            }

            // MoteTick's first test, the idle path's only side effect: WarmupMoteSocialSymbol lazily loads and caches
            // a texture. Evaluated once, as vanilla does per tick.
            if (def.warmupMote == null) _ = def.WarmupMoteSocialSymbol == null;
            return true;
        }

        /// <summary>Last seen comp types per AbilityDef.index; an entry is used only if def and every type match.</summary>
        private sealed class CompTypes
        {
            public AbilityDef def;
            public Type[] types;
            public bool anyOverride;
        }

        private static CompTypes[] compTypesByDef = new CompTypes[0];

        /// <summary>True if no comp's runtime type overrides AbilityComp.CompTick (so the comp loop does nothing).</summary>
        private static bool CompsTickIdle(AbilityDef def, List<AbilityComp> comps)
        {
            if (comps == null || comps.Count == 0) return true;
            int idx = def.index;
            var table = compTypesByDef;
            var entry = idx < table.Length ? table[idx] : null;
            if (entry != null && entry.def == def && entry.types.Length == comps.Count)
            {
                bool match = true;
                for (int i = 0; i < comps.Count; i++)
                {
                    var comp = comps[i];
                    if (comp == null || comp.GetType() != entry.types[i]) { match = false; break; }
                }
                if (match) return !entry.anyOverride;
            }

            var types = new Type[comps.Count];
            bool anyOverride = false;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] == null) return false; // vanilla throws; let it
                types[i] = comps[i].GetType();
                anyOverride |= OverridesCompTick(types[i]);
            }
            if (idx != ushort.MaxValue)
            {
                if (idx >= table.Length)
                {
                    var grown = new CompTypes[Math.Max(idx + 1, DefDatabase<AbilityDef>.DefCount + 16)];
                    Array.Copy(table, grown, table.Length);
                    table = grown;
                }
                table[idx] = new CompTypes { def = def, types = types, anyOverride = anyOverride };
                compTypesByDef = table;
            }
            return !anyOverride;
        }

        private static readonly object sync = new object();
        // Copy-on-write so concurrent readers never see a dictionary that is being written.
        private static Dictionary<Type, bool> overridesCompTick = new Dictionary<Type, bool>();

        private static bool OverridesCompTick(Type type)
        {
            if (type == typeof(AbilityComp)) return false;
            var cache = overridesCompTick;
            if (cache.TryGetValue(type, out bool result)) return result;
            result = false;
            for (Type t = type; t != null && t != typeof(AbilityComp); t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != AbilityCompTick.Name || m.GetParameters().Length != 0) continue;
                    var baseDef = m.GetBaseDefinition();
                    if (baseDef.DeclaringType == AbilityCompTick.DeclaringType && baseDef.Name == AbilityCompTick.Name) result = true;
                }
            }
            lock (sync)
            {
                var copy = new Dictionary<Type, bool>(overridesCompTick) { [type] = result };
                overridesCompTick = copy;
            }
            return result;
        }

        private static readonly List<int> verbSnapshot = new List<int>();

        /// <summary>Runs vanilla on an ability judged idle and records whether it really changed nothing we can see.</summary>
        private static void VerifyIdle(Ability ability)
        {
            ulong rand = randState();
            bool wasCasting = wasCastingRef(ability);
            int lastCastTick = ability.lastCastTick;
            bool inCooldown = inCooldownRef(ability);
            int cooldownEnd = cooldownEndTickRef(ability);
            int charges = ability.RemainingCharges;
            Effecter warmupEffecter = warmupEffecterRef(ability);
            Mote warmupMote = warmupMoteRef(ability);
            Sustainer soundCast = soundCastRef(ability);
            int effecters = effectersRef(ability).Count;
            Mote moteCast = ability is Psycast p ? moteCastRef(p) : null;
            List<Verb> verbs = verbsRef(verbTrackerRef(ability));
            SnapshotVerbs(verbs, verbSnapshot);

            ability.AbilityTick();

            bool same = rand == randState()
                && wasCasting == wasCastingRef(ability)
                && lastCastTick == ability.lastCastTick
                && inCooldown == inCooldownRef(ability)
                && cooldownEnd == cooldownEndTickRef(ability)
                && charges == ability.RemainingCharges
                && warmupEffecter == warmupEffecterRef(ability)
                && warmupMote == warmupMoteRef(ability)
                && soundCast == soundCastRef(ability)
                && effecters == effectersRef(ability).Count
                && moteCast == (ability is Psycast q ? moteCastRef(q) : null)
                && ReferenceEquals(verbs, verbsRef(verbTrackerRef(ability)))
                && VerbsUnchanged(verbs, verbSnapshot);
            Verify.Record(OptId, same);
        }

        private static void SnapshotVerbs(List<Verb> verbs, List<int> into)
        {
            into.Clear();
            into.Add(verbs.Count);
            foreach (var v in verbs)
            {
                into.Add((int)v.state);
                into.Add(burstShotsLeftRef(v));
                into.Add(ticksToNextBurstShotRef(v));
                into.Add(verbEffectersRef(v).Count);
            }
        }

        private static readonly List<int> verbSnapshotAfter = new List<int>();

        private static bool VerbsUnchanged(List<Verb> verbs, List<int> before)
        {
            SnapshotVerbs(verbs, verbSnapshotAfter);
            if (verbSnapshotAfter.Count != before.Count) return false;
            for (int i = 0; i < before.Count; i++)
                if (before[i] != verbSnapshotAfter[i]) return false;
            return true;
        }
    }
}
