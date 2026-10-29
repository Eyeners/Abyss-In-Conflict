using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
#if SERVER
using Barotrauma.Networking;
#endif

namespace AIC.Components
{
    // The crew-list entry and orders belong to the original. Only its proxy executes them.
    public sealed class AICCloneAI : IAssemblyPlugin
    {
        private Harmony harmony;
        private sealed class OrderState { internal Order[] Orders = Array.Empty<Order>(); }
        private static ConditionalWeakTable<AICCloneChamber, OrderState> orderStates = new();

        public void PreInitPatching() => Initialize();
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.clone.ai");
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.SetOrder)),
                postfix: new HarmonyMethod(typeof(AICCloneAI), nameof(OnOrderChanged)));
            harmony.Patch(AccessTools.Method(typeof(HumanAIController), nameof(HumanAIController.SetOrder)),
                prefix: new HarmonyMethod(typeof(AICCloneAI), nameof(KeepOriginalSeated)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(CrewManager), nameof(CrewManager.AddCharacter)),
                prefix: new HarmonyMethod(typeof(AICCloneAI), nameof(KeepCloneOutOfCrew)));
            harmony.Patch(AccessTools.PropertySetter(typeof(Character), nameof(Character.Controlled)),
                prefix: new HarmonyMethod(typeof(AICCloneAI), nameof(SelectProxy)));
            harmony.Patch(AccessTools.Method(typeof(CrewManager), "TryAdjustIndex"),
                transpiler: new HarmonyMethod(typeof(AICCloneAI), nameof(CycleFromOriginal)));
#endif
#if SERVER
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.SetClientCharacter)),
                prefix: new HarmonyMethod(typeof(AICCloneAI), nameof(SelectServerProxy)));
#endif
        }

        private static bool KeepOriginalSeated(HumanAIController __instance)
            => !AICCloneChamber.Bodies.TryGetValue(__instance.Character, out var chamber) || !chamber.Connected;

        private static void OnOrderChanged(Character __instance)
        {
            if (GameMain.NetworkMember?.IsClient == true) return;
            if (!AICCloneChamber.Bodies.TryGetValue(__instance, out var chamber) || !chamber.Connected) return;
            var state = orderStates.GetOrCreateValue(chamber);
            // Failed hearing checks leave the original's order objects unchanged.
            if (!state.Orders.SequenceEqual(__instance.CurrentOrders)) CopyOrders(chamber);
        }

        internal static void CopyOrders(AICCloneChamber chamber)
        {
            if (GameMain.NetworkMember?.IsClient == true || !AICCloneChamber.Alive(chamber.Original)
                || !AICCloneChamber.Alive(chamber.Clone) || chamber.Clone.AIController is not HumanAIController) return;
            var orders = chamber.Original.CurrentOrders.ToArray();
            orderStates.GetOrCreateValue(chamber).Orders = orders;
            chamber.Clone.CurrentOrders.Clear();
            chamber.Clone.CurrentOrders.AddRange(orders.Select(o => o.Clone().WithObjective(null)));
            // Do not call Character.SetOrder again: that would repeat OnGiveOrder talents
            // and auto-dismiss the original's matching operate order.
            RefreshObjectives(chamber.Clone);
        }

        internal static void RefreshObjectives(Character character)
        {
            if (GameMain.NetworkMember?.IsClient == true || !AICCloneChamber.Alive(character)
                || character.AIController is not HumanAIController ai) return;
            var orders = character.CurrentOrders.ToArray();
            ai.ObjectiveManager.CurrentOrders.Clear();
            ai.ObjectiveManager.ClearForcedOrder();
            if (orders.Length == 0) ai.ObjectiveManager.CreateAutonomousObjectives();
            else foreach (var order in orders) ai.SetOrder(order, speak: false);
            ai.ObjectiveManager.SortObjectives();
        }

        internal static Character ResolveProxy(Character character)
        {
            return character != null && AICCloneChamber.Bodies.TryGetValue(character, out var chamber)
                && chamber.Connected && AICCloneChamber.Alive(chamber.Clone) ? chamber.Clone : character;
        }

#if CLIENT
        private static bool KeepCloneOutOfCrew(Character __0)
            => __0 == null || !AICCloneChamber.Clones.ContainsKey(__0);

        // Resolve only the list lookup, without changing control or selection state.
        private static Character ControlledCrewEntry()
        {
            var controlled = Character.Controlled;
            return controlled != null && AICCloneChamber.Clones.TryGetValue(controlled, out var chamber)
                && chamber.Connected && AICCloneChamber.Alive(chamber.Original)
                ? chamber.Original : controlled;
        }

        private static IEnumerable<CodeInstruction> CycleFromOriginal(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(Character), nameof(Character.Controlled));
            var replacement = AccessTools.Method(typeof(AICCloneAI), nameof(ControlledCrewEntry));
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(getter)) instruction.operand = replacement;
                yield return instruction;
            }
        }

        private static void SelectProxy(ref Character value)
        {
            // Multiplayer control messages are authoritative. A stale chamber event must
            // never redirect a server-issued return to the original back into the clone.
            if (GameMain.NetworkMember?.IsClient == true) return;
            value = ResolveProxy(value);
        }
#endif
#if SERVER
        private static void SelectServerProxy(Client client, ref Character newCharacter)
        {
            newCharacter = ResolveProxy(newCharacter);
            if (newCharacter != null && AICCloneChamber.Clones.TryGetValue(newCharacter, out var chamber)
                && chamber.Connected) chamber.SetControllerClient(client);
        }
#endif

        public void OnLoadCompleted()
        {
#if SERVER
            AICCloneChamber.RegisterReturnMessage();
#endif
        }
        public void Dispose()
        {
#if SERVER
            AICCloneChamber.ReturnMessageEnabled = false;
#endif
            harmony?.UnpatchSelf(); harmony = null;
            orderStates = new();
        }
    }

    partial class AICCloneChamber
    {
        internal const string ReturnMessage = "aic.clone.return";
#if SERVER
        internal static bool ReturnMessageEnabled;
        internal static void RegisterReturnMessage()
        {
            ReturnMessageEnabled = true;
            LuaCsSetup.Instance.Networking.Receive(ReturnMessage, (IReadMessage message, Client client) =>
            {
                if (!ReturnMessageEnabled) return;
                ushort chamberId = message.ReadUInt16();
                ushort cloneId = message.ReadUInt16();
                if (client?.Character == null) return;
                var chamber = (Entity.FindEntityByID(chamberId) as Item)?.GetComponent<AICCloneChamber>();
                if (chamber?.Clone == null || chamber.Clone.ID != cloneId || chamber.Item.Removed) return;
                if (client.Character == chamber.Clone) chamber.Disconnect();
                else if (client == chamber.returnRecipient && client.Character == chamber.Original) chamber.Sync();
            });
        }
#endif
#if SERVER
        internal void SetControllerClient(Client client)
        {
            // Ownership is assigned by Connect, not by arbitrary character switches.
            if (owner == null && client?.Character == Original) owner = client;
        }
#endif
    }
}
