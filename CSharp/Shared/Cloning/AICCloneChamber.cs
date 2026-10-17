using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    // Body properties belong to the substrate, not to the copied CharacterInfo.
    partial class AICCloneSubstrate : ItemComponent, IServerSerializable, IClientSerializable
    {
        [Serialize(100f, IsPropertySaveable.No)] public float BaseVitality { get; set; }
        [Serialize("", IsPropertySaveable.No)] public string SpeciesName { get; set; }
        public AICCloneSubstrate(Item item, ContentXElement element) : base(item, element) { }
    }

    partial class AICCloneChamber : ItemComponent, IClientSerializable, IServerSerializable
    {
        internal static readonly Dictionary<Character, AICCloneChamber> Bodies = new();
        internal static readonly Dictionary<Character, AICCloneChamber> Clones = new();
        internal Character Original, Clone;
        internal bool Connected;
        private bool originalAIEnabled, originalForceRagdoll;
        private Character lastUser;
        private float baseVitality = 100;
        private double nextAction;
        private bool creating;
        private Character parkedCharacter;
        [Serialize("140,0", IsPropertySaveable.Yes)] public Vector2 SpawnOffset { get; set; }
#if SERVER
        private Client owner;
        private Client returnRecipient;
        private double nextReturnCheck;
        private double returnRetryUntil;
#endif
        private Controller cachedSeat;
        private Controller Seat => cachedSeat ??= Item.GetComponent<Controller>();
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static bool Alive(Character character) => character != null && !character.Removed && !character.IsDead;

        public AICCloneChamber(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
#if CLIENT
            // ItemComponent creates the frame, but only calls CreateGUI on resolution changes.
            CreateGUI();
#endif
        }

        // Keep this component selectable for native HUD layout, without adding another
        // interaction or overriding the Controller's enter/exit result.
        public override bool Select(Character character) => false;

        public override void Update(float deltaTime, Camera cam)
        {
            UpdateDeployment(deltaTime);
            RefreshStasisOccupant();
#if CLIENT
            ResolveCharacters();
            if (Clone != null && Character.Controlled == Clone)
            {
                bool pressed = PlayerInput.KeyDown(Keys.F);
                if (pressed && !previousF && GUI.KeyboardDispatcher.Subscriber == null) Request(true);
                previousF = pressed;
            }
            else previousF = false;
#endif
            if (!Authority) { ApplyBodyState(); return; }
#if SERVER
            if (!Connected && Timing.TotalTime < returnRetryUntil && Timing.TotalTime >= nextReturnCheck)
            {
                nextReturnCheck = Timing.TotalTime + 1;
                ReturnCloneController();
            }
#endif
            UpdateDamageWarning();
            if (IsDeployed) RestoreStoredClone();
            if (Connected)
            {
                bool ownerDisconnected = false;
#if SERVER
                ownerDisconnected = owner == null || !GameMain.Server.ConnectedClients.Contains(owner);
#endif
                if (!IsDeployed || !Alive(Original) || !Alive(Clone) || Item.Condition <= 0 || Seat?.User != Original || ownerDisconnected)
                    Disconnect();
            }
            // A controller can be occupied before a clone connection succeeds.
            // Broken chambers must release that native pose even when Connected is false.
            if (Item.Condition <= 0 && Seat?.User is Character occupant)
                Seat.Select(occupant);
            ApplyBodyState();
            var user = Seat?.User;
            // Re-entering a chamber with a living clone reconnects without another substrate.
            if (user != lastUser)
            {
                lastUser = user;
                if (!Connected && Alive(Clone) && user == Original) Connect(user);
            }
        }

        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

        private void ApplyBodyState()
        {
            if (Alive(Clone))
            {
                bool enableCloneAI = Authority && Connected && !Clone.IsPlayer;
                if (Clone.AIController != null && Clone.AIController.Enabled != enableCloneAI)
                    Clone.AIController.Enabled = enableCloneAI;
                bool parkClone = !Connected;
#if CLIENT
                // Keep the local player usable while awaiting the authoritative return.
                if (!Authority && Character.Controlled == Clone) parkClone = false;
#endif
                if (Clone.IsForceRagdolled != parkClone) Clone.IsForceRagdolled = parkClone;
                if (parkClone && parkedCharacter != Clone) Clone.ClearInputs();
                parkedCharacter = parkClone ? Clone : null;
            }
            if (Connected && Alive(Original))
            {
                if (Original.AIController != null) Original.AIController.Enabled = false;
                Original.ClearInputs();
                Original.IsForceRagdolled = true;
                Original.SelectedItem = Item;
            }
        }

        private bool CanConnect(Character user)
        {
            if (!Alive(user) || user.Info?.Job == null || user.IsUnconscious || !user.IsHuman
                || !IsDeployed || IsMaintenanceInteraction(user) || Item.Removed || Item.Condition <= 0 || Seat?.User != user || !user.IsAnySelectedItem(Item)
                || Connected || creating || Timing.TotalTime < nextAction || Clones.ContainsKey(user)
                || (!Alive(Clone) && !string.IsNullOrEmpty(StoredClone))) return false;
            if (Bodies.TryGetValue(user, out var existing) && existing != this) return false;
            if (Alive(Clone) && Original != user) return false;
#if SERVER
            if (!GameMain.Server.ConnectedClients.Any(c => c.Character == user)) return false;
#elif CLIENT
            if (Character.Controlled != user) return false;
#endif
            return true;
        }

        private void Connect(Character user)
        {
            if (!Authority || !CanConnect(user)) return;
            nextAction = Timing.TotalTime + 0.5;
            if (!Alive(Clone))
            {
                var material = Item.GetComponent<ItemContainer>()?.Inventory.AllItems
                    .FirstOrDefault(i => !i.Removed && i.Condition > 0 && i.GetComponent<AICCloneSubstrate>() != null);
                if (material == null) return;
                creating = true;
                Character spawned = null;
                try
                {
                    // A serialized copy owns a distinct Job/Skill/SavedStatValues graph.
                    XElement data = user.Info.Save(null);
                    RemoveOwnerMarkers(data);
                    data.SetAttributeValue("name", user.Name + " · Clone");
                    data.SetAttributeValue("startitemsgiven", true);
                    data.SetAttributeValue("hull", null);
                    data.SetAttributeValue("npcsetid", null);
                    data.SetAttributeValue("npcid", null);
                    var substrate = material.GetComponent<AICCloneSubstrate>();
                    if (!string.IsNullOrWhiteSpace(substrate.SpeciesName))
                    {
                        var species = substrate.SpeciesName.ToIdentifier();
                        if (CharacterPrefab.FindBySpeciesName(species) == null)
                            throw new InvalidOperationException("Unknown substrate species: " + species);
                        // Copy progression, but use the target species' own appearance.
                        var appearance = new CharacterInfo(species, name: user.Name).Save(null);
                        foreach (string attribute in new[] { "speciesname", "tags", "hairindex", "beardindex",
                            "moustacheindex", "faceattachmentindex", "skincolor", "haircolor", "facialhaircolor" })
                            data.SetAttributeValue(attribute, (string)appearance.Attribute(attribute));
                    }
                    var info = new CharacterInfo(new ContentXElement(null, data));
                    info.TeamID = user.TeamID;
                    baseVitality = MathHelper.Clamp(substrate.BaseVitality, 1, 10000);
                    RememberFrame(Clone);
                    CloneSubstrateId = material.Prefab.Identifier.Value;
                    spawned = Character.Create(info, Item.WorldPosition + SpawnOffset,
                        Guid.NewGuid().ToString(), hasAi: true, spawnInitialItems: false);
                    spawned.TeamID = user.TeamID;
                    spawned.CharacterHealth.SetVitality(baseVitality);
                    spawned.LoadTalents();
                    ClonePollution = substrate.Pollution;
                    AICCloneSubstrate.ApplyPollution(spawned, ClonePollution);
                    if (Clone != null) Clones.Remove(Clone);
                    if (Original != null) Bodies.Remove(Original);
                    Original = user;
                    Clone = spawned;
                    MarkOriginal(user);
                    Bodies[Original] = this;
                    Clones[Clone] = this;
                    material.Condition = 0;
                    Entity.Spawner.AddItemToRemoveQueue(material);
                }
                catch (Exception error)
                {
                    if (spawned != null) Entity.Spawner?.AddEntityToRemoveQueue(spawned);
                    LuaCsLogger.LogError("[AIC CloneChamber] Create failed: " + error);
                    return;
                }
                finally { creating = false; }
            }
            originalAIEnabled = Original.AIController?.Enabled ?? false;
            originalForceRagdoll = Original.IsForceRagdolled;
            // Reconnecting also picks up progress made by the original while disconnected.
            CopyProgress(Original.Info, Clone.Info);
            foreach (var talent in Original.Info.UnlockedTalents.ToArray()) Clone.GiveTalent(talent, false);
            AICCloneTalentProgress.Synchronize(Original, Clone);
            Connected = true;
#if SERVER
            returnRecipient = null;
#endif
            AICCloneAI.CopyOrders(this);
            Clone.IsForceRagdolled = false;
            Clone.IsRagdolled = false;
            Clone.ClearInputs();
#if SERVER
            owner = GameMain.Server.ConnectedClients.FirstOrDefault(c => c.Character == Original);
            GameMain.Server.SetClientCharacter(owner, Clone);
#elif CLIENT
            Character.Controlled = Clone;
#endif
            ApplyBodyState();
            Sync();
        }

        private static void CopyProgress(CharacterInfo source, CharacterInfo target)
        {
            AICCloneSystem.Syncing = true;
            try
            {
                foreach (var skill in source.Job.GetSkills())
                    target.SetSkillLevel(skill.Identifier, skill.Level, false);
                target.SetExperience(source.ExperiencePoints);
                target.AdditionalTalentPoints = source.AdditionalTalentPoints;
            }
            finally { AICCloneSystem.Syncing = false; }
        }

        internal void Disconnect()
        {
            if (!Connected)
            {
#if SERVER
                ReturnCloneController();
                Sync();
#endif
                return;
            }
            if (Authority) ClearDamageWarning();
            Connected = false;
#if SERVER
            returnRetryUntil = Timing.TotalTime + 5;
#endif
            nextAction = Timing.TotalTime + 0.5;
            if (Original != null && !Original.Removed)
            {
                Original.IsForceRagdolled = originalForceRagdoll;
                Original.IsRagdolled = originalForceRagdoll;
                Original.ClearInputs();
                if (Original.AIController != null) Original.AIController.Enabled = originalAIEnabled;
                AICCloneAI.RefreshObjectives(Original);
                // Native Select releases the pose, limb joints and rear draw layer.
                if (Seat?.User == Original) Seat.Select(Original);
            }
            if (Authority)
            {
#if SERVER
                ReturnCloneController();
                if (owner != null && owner.Character == null)
                    GameMain.Server.SetClientCharacter(owner, Alive(Original) ? Original : null);
                owner = null;
#elif CLIENT
                if (Character.Controlled == Clone || Character.Controlled == null)
                    Character.Controlled = Alive(Original) ? Original : null;
#endif
                Sync();
            }
            ApplyBodyState();
        }

        internal void Release()
        {
            if (stasisOccupant != null && StasisOccupants.TryGetValue(stasisOccupant, out var seated) && seated == this)
                StasisOccupants.Remove(stasisOccupant);
            stasisOccupant = null;
            ReleaseDeploymentBody();
            Disconnect();
            if (Original != null) Bodies.Remove(Original);
            if (Clone != null) { RememberFrame(Clone); Clones.Remove(Clone); }
            Original = null;
            Clone = null;
        }

        protected override void RemoveComponentSpecific()
        {
            ReleaseDeploymentBody();
            Release();
        }
        private void Sync()
        {
#if SERVER
            if (!Item.Removed) Item.CreateServerEvent(this);
#endif
        }

#if CLIENT
        private ushort originalId, cloneId;
        private bool previousF;
        private GUITextBlock status;
        private string cachedPollutionValue;
        private string[] cachedPollutants = Array.Empty<string>();
        private string cachedPollutionText = "";
        private double nextPollutionTextUpdate;
        private GUIButton connectButton;
        private sealed record RequestData(bool Disconnect, bool Recycle) : ItemComponent.IEventData;

        protected override void CreateGUI()
        {
            if (GuiFrame == null) return;
            var layout = new GUILayoutGroup(new RectTransform(new Vector2(0.9f, 0.9f), GuiFrame.RectTransform, Anchor.Center))
                { RelativeSpacing = 0.06f };
            new GUITextBlock(new RectTransform(new Vector2(1, 0.16f), layout.RectTransform), TextManager.Get("aic.clone.title"));
            status = new GUITextBlock(new RectTransform(new Vector2(1, 0.48f), layout.RectTransform), "", wrap: true);
            connectButton = new GUIButton(new RectTransform(new Vector2(1, 0.24f), layout.RectTransform), TextManager.Get("aic.clone.connect"));
            connectButton.OnClicked = (_, _) => { Request(false); return true; };
        }

        public override void UpdateHUDComponentSpecific(Character character, float deltaTime, Camera cam)
        {
            if (status == null) return;
            bool recycling = RecycleTarget != null;
            connectButton.Text = TextManager.Get(recycling ? "aic.clone.recycle" : "aic.clone.connect");
            if (recycling)
            {
                status.Text = TextManager.Get("aic.clone.recyclehint");
                connectButton.Enabled = CanRecycle(character);
                return;
            }
            bool occupied = Alive(Clone) && Original != character;
            bool pending = !Alive(Clone) && !string.IsNullOrEmpty(StoredClone);
            status.Text = TextManager.Get(restoreFailed ? "aic.clone.restorefailed" : pending ? "aic.clone.restoring"
                : occupied ? "aic.clone.occupied" : Seat?.User != character ? "aic.clone.enter"
                : Alive(Clone) ? "aic.clone.reconnect" : "aic.clone.material");
            var substrate = Item.GetComponent<ItemContainer>()?.Inventory.GetItemAt(0)?.GetComponent<AICCloneSubstrate>();
            string pollutionValue = substrate?.Pollution ?? "";
            if (cachedPollutionValue != pollutionValue)
            {
                cachedPollutionValue = pollutionValue;
                cachedPollutants = AICCloneSubstrate.ParsePollution(pollutionValue);
                nextPollutionTextUpdate = 0;
            }
            var pollutants = cachedPollutants;
            if (pollutants.Length > 0)
            {
                if (Timing.TotalTime >= nextPollutionTextUpdate)
                {
                    cachedPollutionText = "\n" + TextManager.Get("aic.clone.pollution").Value + ": "
                        + string.Join(", ", pollutants.Select(id => TextManager.Get("afflictionname." + id).Value));
                    nextPollutionTextUpdate = Timing.TotalTime + 0.5;
                }
                status.Text += cachedPollutionText;
            }
            connectButton.Enabled = !pending && !occupied && Seat?.User == character && !Connected;
        }

        private void Request(bool disconnect)
        {
            bool recycle = !disconnect && RecycleTarget != null;
            if (GameMain.NetworkMember?.IsClient == true)
            {
                if (disconnect)
                {
                    // Returning is not a physical item interaction and must not wait
                    // for a stunned proxy's character input state to catch up.
                    var message = LuaCsSetup.Instance.Networking.Start(ReturnMessage);
                    message.WriteUInt16(Item.ID);
                    message.WriteUInt16(Clone?.ID ?? (ushort)0);
                    LuaCsSetup.Instance.Networking.Send(message);
                }
                else Item.CreateClientEvent(this, new RequestData(false, recycle));
            }
            else if (disconnect) Disconnect();
            else if (recycle) Recycle(Character.Controlled);
            else Connect(Character.Controlled);
        }

        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
        {
            var request = extraData is Item.ComponentStateEventData data ? data.ComponentData as RequestData : null;
            msg.WriteBoolean(request?.Disconnect == true);
            msg.WriteBoolean(request?.Recycle == true);
        }

        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            originalId = msg.ReadUInt16();
            cloneId = msg.ReadUInt16();
            bool connected = msg.ReadBoolean();
            baseVitality = msg.ReadSingle();
            recycleOccupantId = msg.ReadUInt16();
            bool confirmReturn = msg.ReadBoolean();
            if (Connected && !connected) Disconnect();
            if (!Connected && connected && Original != null)
            {
                originalAIEnabled = Original.AIController?.Enabled ?? false;
                originalForceRagdoll = Original.IsForceRagdolled;
            }
            Connected = connected;
            ResolveCharacters();
            if (confirmReturn && !connected && Alive(Original)
                && (Character.Controlled == Clone || GameMain.Client.Character == Clone || Character.Controlled == null))
            {
                // This acknowledgement is addressed only to the returning client by the server.
                Character.Controlled = Original;
                GameMain.Client.Character = Original;
                Original.IsForceRagdolled = false;
                Original.IsRagdolled = false;
                Original.AnimController.Frozen = false;
            }
        }

        private void ResolveCharacters()
        {
            if (Authority) return;
            if (originalId == 0 && Original != null)
            {
                Bodies.Remove(Original);
                Original = null;
            }
            if (cloneId == 0 && Clone != null)
            {
                Clones.Remove(Clone);
                Clone = null;
            }
            // Spawn events may arrive after the chamber event. Retain the IDs until resolved.
            var body = Original != null && !Original.Removed && Original.ID == originalId
                ? Original : Entity.FindEntityByID(originalId) as Character;
            var clone = Clone != null && !Clone.Removed && Clone.ID == cloneId
                ? Clone : Entity.FindEntityByID(cloneId) as Character;
            if (body != null && Original != body)
            {
                if (Original != null) Bodies.Remove(Original);
                Original = body;
                Bodies[body] = this;
                originalAIEnabled = body.AIController?.Enabled ?? false;
                originalForceRagdoll = body.IsForceRagdolled;
            }
            if (clone != null && Clone != clone)
            {
                if (Clone != null) Clones.Remove(Clone);
                Clone = clone;
                Clones[clone] = this;
                clone.CharacterHealth.SetVitality(baseVitality);
                // Native ReadSpawnData adds every friendly humanoid to the crew UI.
                GameMain.GameSession?.CrewManager?.RemoveCharacter(clone, removeInfo: true);
            }
        }
#endif
#if SERVER
        private void ReturnCloneController()
        {
            if (Clone == null || Original == null || Original.Removed) return;
            foreach (var client in GameMain.Server.ConnectedClients.Where(c => c.Character == Clone).ToArray())
            {
                // Do not steal an original currently controlled by another player.
                if (GameMain.Server.ConnectedClients.Any(c => c != client && c.Character == Original)) continue;
                returnRecipient = client;
                GameMain.Server.SetClientCharacter(client, Alive(Original) ? Original : null);
                if (Alive(Original))
                {
                    Original.IsForceRagdolled = false;
                    Original.IsRagdolled = false;
                    Original.AnimController.Frozen = false;
                }
                Sync();
            }
            if (!GameMain.Server.ConnectedClients.Any(c => c.Character == Clone)) returnRetryUntil = 0;
        }

        public void ServerEventRead(IReadMessage msg, Client client)
        {
            bool disconnect = msg.ReadBoolean();
            bool recycle = msg.ReadBoolean();
            if (disconnect)
            {
                if (client?.Character == Clone && Clone != null) Disconnect();
                else if (client != null && client == returnRecipient && client.Character == Original) Sync();
            }
            else if (recycle) Recycle(client?.Character);
            else Connect(client?.Character);
        }

        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteUInt16(Original?.ID ?? (ushort)0);
            msg.WriteUInt16(Clone?.ID ?? (ushort)0);
            msg.WriteBoolean(Connected);
            msg.WriteSingle(baseVitality);
            msg.WriteUInt16(RecycleTarget?.ID ?? (ushort)0);
            msg.WriteBoolean(!Connected && client == returnRecipient && client?.Character == Original);
        }
#endif
    }

    public sealed class AICCloneSystem : IAssemblyPlugin
    {
        private Harmony harmony;
        internal static bool Syncing;
        public void PreInitPatching() => Initialize();
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.clone.chamber");
            AICCloneChamber.PatchStasis(harmony);
            harmony.Patch(AccessTools.Method(typeof(MapEntity), nameof(MapEntity.UpdateAll)),
                postfix: new HarmonyMethod(typeof(AICCloneChamber), nameof(AICCloneChamber.RestorePendingClones)));
            harmony.Patch(AccessTools.Method(typeof(Controller), nameof(Controller.Select), new[] { typeof(Character) }),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(SelectDeployedChamber)));
            harmony.Patch(AccessTools.Method(typeof(CharacterInfo), nameof(CharacterInfo.IncreaseSkillLevel)),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(BeforeSkill)),
                postfix: new HarmonyMethod(typeof(AICCloneSystem), nameof(AfterSkill)));
            harmony.Patch(AccessTools.Method(typeof(CharacterInfo), nameof(CharacterInfo.SetSkillLevel)),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(BeforeSkill)),
                postfix: new HarmonyMethod(typeof(AICCloneSystem), nameof(AfterSkill)));
#if CLIENT
            // Skill values are already updated before this client-only notification.
            // Suppress both fractional and whole-point popups on the occupied original,
            // including skill updates received from the server.
            harmony.Patch(AccessTools.Method(typeof(CharacterInfo), "OnSkillChanged"),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(ShowSkillNotification)));
#endif
            harmony.Patch(AccessTools.Method(typeof(CharacterInfo), nameof(CharacterInfo.GiveExperience)),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(BeforeExperience)),
                postfix: new HarmonyMethod(typeof(AICCloneSystem), nameof(AfterExperience)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.GiveTalent), new[] { typeof(TalentPrefab), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(AICCloneSystem), nameof(AfterTalent)));
            harmony.Patch(AccessTools.Method(typeof(CharacterTalent), nameof(CharacterTalent.UpdateTalent)),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(UpdateTalent)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Character)).Where(m => m.Name == nameof(Character.CheckTalents)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(CheckTalents)));
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(GameSession)).Where(m => m.Name == "EndRound"))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(EndRound)));
#if SERVER
            // DisconnectClient can save a player's data before the next item update.
            harmony.Patch(AccessTools.Method(typeof(MultiPlayerCampaign), "SetClientCharacterData"),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(BeforeSavePlayer)));
#endif
            // Kill has already marked the character dead here, but player death handling
            // has not yet run. Return control before vanilla processes the clone as a player.
            harmony.Patch(AccessTools.Method(typeof(Character), "KillProjSpecific"),
                prefix: new HarmonyMethod(typeof(AICCloneSystem), nameof(OnDeath)));
        }

        private static bool SelectDeployedChamber(Controller __instance, Character __0, ref bool __result)
        {
            // Bind by argument position: native Controller.Select calls it "activator".
            var character = __0;
            var chamber = __instance.Item.GetComponent<AICCloneChamber>();
            if (chamber == null || __instance.User == character) return true;
            if (character?.SelectedCharacter != null)
            {
                chamber.TryInsertFrame(character);
                __result = false;
                return false;
            }
            if (__instance.Item.Condition > 0 && !__instance.Item.Removed
                && __instance.Item.GetComponent<Holdable>()?.Attached == true
                && !chamber.IsMaintenanceInteraction(character)) return true;
            __result = false;
            return false;
        }

        private static AICCloneChamber GetLink(CharacterInfo info)
        {
            if (Syncing || GameMain.NetworkMember?.IsClient == true || info?.Character == null) return null;
            return AICCloneChamber.Clones.TryGetValue(info.Character, out var chamber)
                && AICCloneChamber.Alive(chamber.Original) ? chamber : null;
        }

        private static void BeforeSkill(CharacterInfo __instance, Identifier skillIdentifier, out float __state)
            => __state = GetLink(__instance) == null ? float.NaN : __instance.Job.GetSkillLevel(skillIdentifier);

        private static void AfterSkill(CharacterInfo __instance, Identifier skillIdentifier, float __state)
        {
            var chamber = GetLink(__instance);
            if (chamber == null || float.IsNaN(__state)) return;
            float gain = __instance.Job.GetSkillLevel(skillIdentifier) - __state;
            if (gain <= 0) return;
            Syncing = true;
            try { chamber.Original.Info.SetSkillLevel(skillIdentifier, chamber.Original.Info.Job.GetSkillLevel(skillIdentifier) + gain, false); }
            finally { Syncing = false; }
        }

        private static void BeforeExperience(CharacterInfo __instance, out int __state) => __state = __instance.ExperiencePoints;
        private static void AfterExperience(CharacterInfo __instance, int __state)
        {
            var chamber = GetLink(__instance);
            int gain = __instance.ExperiencePoints - __state;
            if (chamber == null || gain <= 0) return;
            // Set the actual gain; GiveExperience would apply the original's multiplier again.
            chamber.Original.Info.SetExperience(chamber.Original.Info.ExperiencePoints + gain);
        }

        private static void AfterTalent(Character __instance, TalentPrefab talentPrefab, bool addingFirstTime, bool __result)
        {
            if (!__result || !addingFirstTime) return;
            var chamber = GetLink(__instance.Info);
            if (chamber == null) return;
            Syncing = true;
            try { chamber.Original.GiveTalent(talentPrefab, false); }
            finally { Syncing = false; }
        }

        private static bool UpdateTalent(CharacterTalent __instance)
            => CanRunTalents(__instance.Character);

#if CLIENT
        private static bool ShowSkillNotification(CharacterInfo __instance)
            => __instance.Character == null
                || !AICCloneChamber.Bodies.TryGetValue(__instance.Character, out var chamber)
                || !chamber.Connected;
#endif

        private static bool CheckTalents(Character __instance) => CanRunTalents(__instance);

        private static bool CanRunTalents(Character character)
        {
            if (AICCloneChamber.Bodies.TryGetValue(character, out var body)) return !body.Connected;
            if (AICCloneChamber.Clones.TryGetValue(character, out var clone)) return clone.Connected;
            return true;
        }

        private static void EndRound()
        {
            // Restore the real crew before vanilla campaign data and mission rewards are saved.
            foreach (var chamber in AICCloneChamber.Bodies.Values.Distinct().ToArray())
            {
                chamber.Disconnect();
                chamber.CaptureCloneForTransition();
            }
        }

        private static void OnDeath(Character __instance)
        {
            if (GameMain.NetworkMember?.IsClient == true) return;
            if (AICCloneChamber.Clones.TryGetValue(__instance, out var chamber)
                || AICCloneChamber.Bodies.TryGetValue(__instance, out chamber))
            {
                chamber.Disconnect();
                if (chamber.Clone == __instance) chamber.StoredClone = "";
            }
        }

#if SERVER
        private static void BeforeSavePlayer(Client __0)
        {
            if (__0?.Character != null && AICCloneChamber.Clones.TryGetValue(__0.Character, out var chamber))
                chamber.Disconnect();
        }
#endif

        public void OnLoadCompleted() { }
        public void Dispose()
        {
            EndRound();
            harmony?.UnpatchSelf();
            harmony = null;
            AICCloneChamber.Bodies.Clear();
            AICCloneChamber.Clones.Clear();
            AICCloneChamber.PendingRestores.Clear();
            AICCloneChamber.StasisOccupants.Clear();
        }
    }
}
