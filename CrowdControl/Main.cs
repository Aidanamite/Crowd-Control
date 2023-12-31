using HarmonyLib;
using HMLLibrary;
using RaftModLoader;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UltimateWater;
using UnityEngine;
using UnityEngine.AzureSky;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;


namespace CrowdControl
{
    public class Main : Mod
    {
        public const string prefKey = "mod_CrowdControl_hasLoadedBefore";
        static TcpClient client;
        static NetworkStream stream;
        public static string prefix = "[Crowd Control Support]: ";
        public static System.Random rand = new System.Random();
        public static ChunkPoint spawnChunk;
        public static float sharkPacify = 0;
        public static Vector4 windDirection = Vector4.zero;
        public static EventRequest eventRequest;
        public static List<TimedEffect> effectTimers;
        public static List<AnimalEffect> spawnedAnimals;
        public static bool waves;
        public static Block cacheAttackableBlock;
        public static Transform buildPivot;
        public static BlockQuad cacheBuildPos;
        public static bool logNetworking = false;
        Harmony harmony;
        bool loaded = false;
        public void Awake()
        {
            modlistEntry.jsonmodinfo.isModPermanent = false;
            var filename = modlistEntry.modinfo.modFile.Name.ToLower();
            if (!ModManagerPage.OnStartUserPermanentMods.list.Contains(filename) && PlayerPrefs.HasKey(prefKey))
                return;
            loaded = true;
            if (!PlayerPrefs.HasKey(prefKey))
            {
                ModManagerPage.OnStartUserPermanentMods.list.Add(filename);
                ModManagerPage.SetModPermanent(true, filename);
                PlayerPrefs.SetInt(prefKey, 1);
            }
            ModManagerPage.RefreshModState(modlistEntry);
            loaded = true;
            eventRequest = null;
            effectTimers = new List<TimedEffect>();
            spawnedAnimals = new List<AnimalEffect>();
            try
            {
                client = new TcpClient("127.0.0.1", 58430);
                stream = client.GetStream();
            }
            catch
            {
                Debug.LogError(prefix + "Initial connection failed. Please make sure the Crowd Control Client app is running then retry using the \"reconnectCrowdControl\" command");
            }
            harmony = new Harmony("com.aidanamite.CrowdControl");
            harmony.PatchAll();
            Log("Mod has been loaded!");
        }

        public void Update()
        {
            if (!loaded)
            {
                UnloadMod();
                return;
            }
            ClientUpdate();
            float timeDif = Time.deltaTime;
            var message = RAPI.ListenForNetworkMessagesOnChannel(MessageType.ChannelID);
            if (message != null)
            {
                if (logNetworking)
                    Debug.Log("Recieved message on mod's network channel with id " + message.message.Type);
                if (message.message.Type == MessageType.MessageID)
                {
                    var msg = message.message as Message_InitiateConnection;
                    if (logNetworking)
                        Debug.Log("Recieved message type: " + msg.appBuildID);
                    if (msg.appBuildID == MessageType.Execute)
                    {
                        var info = new Message_ExecuteEvent(msg);
                        if (info.MessageID > -1)
                            eventRequest = (message.steamid, info.MessageID, info.Duration);
                        ExecuteEvent(info.EventID);
                        eventRequest = null;
                    }
                    else if (msg.appBuildID == MessageType.Executed && client.Connected)
                    {
                        var info = new Message_EventExecuted(msg);
                        if (logNetworking)
                            Debug.Log("Recieved event reply");
                        foreach (AnimalEffect item in spawnedAnimals)
                            if (item.id == info.MessageID)
                                item.animalId = info.ExtraData;
                        byte[] responce = Encoding.UTF8.GetBytes("{\"id\":" + info.MessageID + ",\"status\":" + (int)EffectResult.Success + ",\"message\":\"Event was executed\",\"type\":0}");
                        byte[] final = new byte[responce.Length + 1];
                        responce.CopyTo(final, 0);
                        if (client.Connected)
                            stream.Write(final, 0, final.Length);
                    }
                    else if (msg.appBuildID == MessageType.Wind)
                    {
                        var info = new Message_Wind(msg);
                        windDirection = new Vector4((float)Math.Sin(info.Angle), 0, (float)Math.Cos(info.Angle), info.Time);
                    }
                    else if (msg.appBuildID == MessageType.Upgrade)
                    {
                        uint ObjectIndex = new Message_Upgrade(msg).ObjectIndex;
                        BlockCreator.GetPlacedBlocks().Find((Block b) => b.ObjectIndex == ObjectIndex).Reinforced = true;
                    }
                }
            }
            if (RAPI.GetLocalPlayer() == null)
            {
                if (spawnChunk != null)
                    spawnChunk = null;
                if (effectTimers.Count > 0)
                {
                    foreach (var e in effectTimers)
                    {
                        try
                        {
                            e.stop?.Invoke();
                        }
                        catch { }
                        try
                        {
                            e.reverseEffect?.Execute();
                        }
                        catch { }
                    }
                    effectTimers.Clear();
                }
                if (spawnedAnimals.Count > 0)
                    spawnedAnimals.Clear();
            }
            else
            {
                spawnChunk = getValidIsland();
                if (sharkPacify > 0)
                    sharkPacify -= timeDif;
                for (int i = effectTimers.Count - 1; i >= 0; i--)
                {
                    effectTimers[i].time -= timeDif;
                    if (effectTimers[i].time <= 0)
                    {
                        eventRequest = "RaftConsole";
                        effectTimers[i].stop?.Invoke();
                        effectTimers[i].reverseEffect?.Execute();
                        eventRequest = null;
                        effectTimers.RemoveAt(i);
                    }
                }
                foreach (AnimalEffect effect in spawnedAnimals)
                    if (effect.nameTag != null)
                        effect.nameTag.UpdateFacing();
                if (windDirection.w != 0)
                {
                    windDirection.w -= timeDif;
                    if (windDirection.w < 0)
                        windDirection.w = 0;
                }
            }
        }

        public void OnModUnload()
        {
            if (!loaded)
                return;
            if (client != null)
                client.Dispose();
            harmony?.UnpatchAll(harmony.Id);
            Log("Mod has been unloaded!");
        }

        public static void sendMessage(Message msg)
        {
            Raft_Network network = RAPI.GetLocalPlayer().Network;
            network.SendP2P(network.HostID, msg, EP2PSend.k_EP2PSendReliable, MessageType.Channel);
        }

        public static void sendReply(int extra = -1)
        {
            if (logNetworking)
                Debug.Log("Sending Reply");
            if (eventRequest != null)
                new Message_EventExecuted(eventRequest.messageId, extra).Message.SendP2P(eventRequest.sender, MessageType.Channel);
        }

        public static void sendRequest(string eventID)
        {
            if (logNetworking)
                Debug.Log("sending request " + eventID);
            new Message_ExecuteEvent(events.IndexOf(eventID), eventRequest.messageId, eventRequest.duration).Message.Broadcast(MessageType.Channel);
        }

        public static void ClientUpdate()
        {
            if (client != null && client.Connected && stream.DataAvailable)
            {
                List<byte> bytes = new List<byte>();
                while (stream.DataAvailable)
                {
                    byte data = (byte)stream.ReadByte();
                    if (data == 0)
                        break;
                    else
                        bytes.Add(data);
                }
                string str = Encoding.UTF8.GetString(bytes.ToArray());
                JSONObject msg;
                int msgID;
                try
                {
                    byte[] responce = null;
                    msg = new JSONObject(str);
                    if (logNetworking)
                        Debug.Log("Recieved message from crowd control: " + str);
                    msgID = (int)msg.GetField("id").n;
                    try
                    {
                        string msgCode = msg.GetField("code").str;
                        if (RAPI.GetLocalPlayer() != null && events[msgCode].IsReady)
                        {
                            RequestType msgType = (RequestType)msg.GetField("type").n;
                            string strMsg = "";
                            bool flag = true;
                            if (msgType == RequestType.Start)
                            {
                                eventRequest = (msgID, msg.GetField("viewer").str, msg.HasField("duration") ? Mathf.RoundToInt(msg.GetField("duration").f) : 0);
                                flag = !ExecuteEvent(msgCode);
                                eventRequest = null;
                                strMsg = "Event was executed";
                            }
                            else if (msgType == RequestType.Test)
                                strMsg = "Event is executable";
                            else if (msgType == RequestType.Stop)
                            {
                                foreach (TimedEffect item in effectTimers)
                                    if (item.id == msgID)
                                    {
                                        item.time = 0;
                                        break;
                                    }
                                foreach (AnimalEffect item in spawnedAnimals)
                                    if (item.id == msgID)
                                    {
                                        if (item.animalId == -1)
                                        {
                                            flag = false;
                                            responce = Encoding.UTF8.GetBytes("{\"id\":" + msgID + ",\"status\":" + (int)EffectResult.Retry + ",\"message\":\"Entity not created yet\",\"type\":0}");
                                        }
                                        else
                                        {
                                            NetworkIDManager.SendIDBehaviourDead((uint)item.animalId, typeof(AI_NetworkBehaviour), true);
                                        }
                                        break;
                                    }
                                if (msgCode == "pacifyShark")
                                    sharkPacify = 0;
                                if (msgCode == "changeWind")
                                {
                                    windDirection.w = 0;
                                    new Message_Wind(0, 0).Message.Broadcast(MessageType.Channel);
                                }
                                strMsg = "Event is not running";
                            }
                            else
                                strMsg = "Nothing happened";
                            if (flag)
                                responce = Encoding.UTF8.GetBytes("{\"id\":" + msgID + ",\"status\":" + (int)EffectResult.Success + ",\"message\":\"" + strMsg + "\",\"type\":0}");
                        }
                        else
                            responce = Encoding.UTF8.GetBytes("{\"id\":" + msgID + ",\"status\":" + (int)EffectResult.Failure + ",\"message\":\"Event is not executable\",\"type\":0}");
                    }
                    catch (Exception err)
                    {
                        Debug.LogError("Event message failed to parse\n" + str + "\n" + err.GetType().Name + ": " + err.Message + "\n" + err.StackTrace);
                        responce = Encoding.UTF8.GetBytes("{\"id\":" + msgID + ",\"status\":" + (int)EffectResult.Failure + ",\"message\":\"Event failed due to an unknown error\",\"type\":0}");
                    }
                    if (responce != null)
                    {
                        byte[] final = new byte[responce.Length + 1];
                        responce.CopyTo(final, 0);
                        stream.Write(final, 0, final.Length);
                    }
                }
                catch (Exception err)
                {
                    Debug.LogError("Event message failed to parse\n" + str + "\n" + err.GetType().Name + ": " + err.Message + "\n" + err.StackTrace);
                }
            }
        }

        [ConsoleCommand(name: "runevent", docs: "Syntax: 'runevent <eventName> [eventDuration]' Triggers one of the Crowd Control events given the event's ID name")]
        public static string MyCommand(string[] args)
        {
            if (args.Length < 1)
                return prefix + "Not enough parameters";
            eventRequest = "RaftConsole";
            if (args.Length > 1 && float.TryParse(args[1], out var d))
                eventRequest.duration = d;
            ExecuteEvent(args[0]);
            eventRequest = null;
            return prefix + "Execution attempt complete";
        }

        [ConsoleCommand(name: "stopevent", docs: "Syntax: 'stopevent <id>' Stops an event given its id")]
        public static string MyCommand2(string[] args)
        {
            if (args.Length > 1)
                return prefix + "Too many parameters";
            if (args.Length < 1)
                return prefix + "Not enough parameters";
            int port;
            if (!int.TryParse(args[0], out port))
                return prefix + "Could not parse \"" + args[0] + "\" as a number";
            try
            {
                foreach (AnimalEffect item in spawnedAnimals)
                    if (item.id == port)
                    {
                        if (item.animalId == -1)
                            return prefix + "Event has not occured yet";
                        else
                        {
                            NetworkIDManager.SendIDBehaviourDead((uint)item.animalId, typeof(AI_NetworkBehaviour), true);
                        }
                        return prefix + "Successfully stopped event " + args[0];
                    }
                return prefix + "Did not find event " + args[0];
            }
            catch (Exception err)
            {
                return prefix + "Something went wrong\n" + err.GetType().Name + ": " + err.Message + "\n" + err.StackTrace;
            }
        }

        [ConsoleCommand(name: "reconnectCrowdControl", docs: "Syntax: 'reconnectCrowdControl' Attempts to connect the crowd control client to the stored address")]
        public static string MyCommand3(string[] args)
        {
            try
            {
                client = new TcpClient("127.0.0.1", 58430);
                stream = client.GetStream();
                return prefix + "Successfully connected";
            }
            catch
            {
                return prefix + "Connection failed. Please make sure the Crowd Control Client app is running then retry";
            }
        }

        [ConsoleCommand(name: "toggleCrowdControlNetworkLogging", docs: "Syntax: 'toggleCrowdControlNetworkLogging' Toggles on/off network logging messages in the console. Primaily used for debugging issues")]
        public static string MyCommand4(string[] args)
        {
            return prefix + "Network logging is now " + ((logNetworking = !logNetworking) ? "enabled" : "disabled");
        }

        public static List<string> BadItems = new List<string>
    {
        "DevSpear",
        "DevHat",
        "ThrowableAnchor",
        "BeachBall",
        "Block_FoundationArmor",
        "Repair",
        "Barrel",
        "DropItem",
        "Blueprint",
        "Seed_Grass",
        "Block_Upgrade_Thatch",
        "Block_Upgrade_Wood",
        "Block_Upgrade_Tier3"
    };

        public static List<Item_Base> VisionHats = new List<Item_Base>
    {
        ItemManager.GetItemByIndex(306),
        ItemManager.GetItemByIndex(205),
        ItemManager.GetItemByIndex(127),
        ItemManager.GetItemByIndex(271)
    };

        static class readyTypes
        {
            public static Func<bool> None = delegate { return true; };
            public static Func<bool> PlayerIsHoldingItem = delegate { return RAPI.GetLocalPlayer().Inventory.GetSelectedHotbarSlot().HasValidItemInstance(); };
            public static Func<bool> PlayerHasAnyItem = delegate { foreach (Slot slot in RAPI.GetLocalPlayer().Inventory.allSlots) if (slot.active && slot.HasValidItemInstance() && !slot.locked) return true; return false; };
            public static Func<bool> PlayerIsNotDead = delegate { return !RAPI.GetLocalPlayer().Stats.IsDead; };
            public static Func<bool> RaftIsAttackable = delegate { return FindObjectsOfType<Block_Foundation>().Length > 4 && getTargetableBlock(); };
            public static Func<bool> HasNearbyIsland = delegate { return spawnChunk != null; };
            public static Func<bool> PlayerHasAnyTool = delegate { foreach (Slot slot in RAPI.GetLocalPlayer().Inventory.allSlots) if (slot.HasToolItem()) return true; return false; };
            public static Func<bool> CanUpgradeRaft = delegate { RaftBounds bounds = FindObjectOfType<RaftBounds>(); if (bounds == null) return false; foreach (Block block in bounds.walkableBlocks) if (block is Block_Foundation && !block.Reinforced) return true; return false; };
            public static Func<bool> PlayerCanEquipHat = delegate { foreach (Slot_Equip slot in RAPI.GetLocalPlayer().Inventory.equipSlots) if (!slot.HasValidItemInstance() || slot.itemInstance.settings_equipment.EquipType == EquipSlotType.Head) return true; return false; };
            public static Func<bool> SharkIsAlive = delegate { foreach (AI_NetworkBehaviour entity in FindObjectsOfType<AI_NetworkBehaviour>()) if (entity is AI_NetworkBehavior_Shark && !entity.networkEntity.IsDead) return true; return false; };
            public static Func<bool> GravityEffectActive = delegate { foreach (TimedEffect effect in effectTimers) if (effect.reverseEffect.id.StartsWith("gravity")) return false; return true; };
            public static Func<bool> SpeedEffectActive = delegate { foreach (TimedEffect effect in effectTimers) if (effect.reverseEffect.id.StartsWith("speed")) return false; return true; };
            public static Func<bool> RaftSpeedEffectActive = delegate { foreach (TimedEffect effect in effectTimers) if (effect.reverseEffect.id.StartsWith("raftSpeed")) return false; return true; };
            public static Func<bool> WavePowerEffectActive = delegate { foreach (TimedEffect effect in effectTimers) if (effect.reverseEffect.id == "wavePowerOff") return false; return true; };
            public static Func<bool> WindEffectActive = delegate { return windDirection.w == 0; };
            public static Func<bool> PacifyEffectActive = delegate { return sharkPacify == 0; };
            public static Func<bool> CanDropAnchors = CanDropAnchor;
            public static Func<bool> CanRaiseAnchors = CanRaiseAnchor;
            public static Func<bool> CanTurnOnEngines = CanEngineOn;
            public static Func<bool> CanTurnOffEngines = CanEngineOff;
            public static Func<bool> HasEngines = delegate { return FindObjectOfType<MotorWheel>() != null || FindObjectOfType<Sail>() != null; };
            public static Func<bool> HasFoundation = delegate { var c = 6; foreach (var b in BlockCreator.GetPlacedBlocks()) if (b is Block_Foundation && c-- <= 1) return true; return false; };
            public static Func<bool> HasFoundation2 = delegate { var f = false; var c = 6; foreach (var b in BlockCreator.GetPlacedBlocks()) { if (b is Block_Foundation) c--; if (b is Block_Foundation && b.Reinforced) f = true; if (f && c < 1) return true; } return false; };
            public static Func<bool> HasFloor = delegate { return BlockCreator.GetPlacedBlocks().Exists(x => x.IsWalkable() && !(x is Block_Foundation)); };
            public static Func<bool> HasEmptySlots = delegate { var c = 5; foreach (var i in RAPI.GetLocalPlayer().Inventory.allSlots) if (i.active && !i.HasValidItemInstance()) if (c-- <= 1) return true; return false; };
        };

        public static EffectList events = new EffectList
    {
        new Effect("spawnShark", _ => spawnShark(),readyTypes.None,true),
        new Effect("spawnItem",delegate { RAPI.GetLocalPlayer().giveItem();},readyTypes.None,false),
        new Effect("spawnItem_food",delegate { RAPI.GetLocalPlayer().giveItem("food"); },readyTypes.None,false),
        new Effect("spawnItem_drink",delegate { RAPI.GetLocalPlayer().giveItem("drink"); },readyTypes.None,false),
        new Effect("spawnItem_edible",delegate { RAPI.GetLocalPlayer().giveItem("edible"); },readyTypes.None,false),
        new Effect("spawnItem_smelted",delegate { RAPI.GetLocalPlayer().giveItem("smelted"); },readyTypes.None,false),
        new Effect("spawnItem_block",delegate { RAPI.GetLocalPlayer().giveItem("block"); },readyTypes.None,false),
        new Effect("spawnItem_equip",delegate { RAPI.GetLocalPlayer().giveItem("equip"); },readyTypes.None,false),
        new Effect("spawnItem_copy",delegate { RAPI.GetLocalPlayer().giveItem("copy"); },readyTypes.None,false),
        new Effect("spawnItem_weapon",delegate { RAPI.GetLocalPlayer().giveItem("weapon"); },readyTypes.None,false),
        new Effect("spawnItem_resource",delegate { RAPI.GetLocalPlayer().giveItem("resource"); },readyTypes.None,false),
        new Effect("dropItem",delegate { RAPI.GetLocalPlayer().DropCurrentItem(); },readyTypes.PlayerIsHoldingItem,false),
        new Effect("dropRandomItem",delegate { RAPI.GetLocalPlayer().DropRandomItem(); },readyTypes.PlayerHasAnyItem,false),
        new Effect("skipTime",_ => skipTime(),readyTypes.None,true),
        new Effect("healthUp",delegate { changeHealth(10); },readyTypes.PlayerIsNotDead,false),
        new Effect("healthUp2",delegate { changeHealth(25); },readyTypes.PlayerIsNotDead,false),
        new Effect("healthDown",delegate { changeHealth(-10); },readyTypes.PlayerIsNotDead,false),
        new Effect("healthDown2",delegate { changeHealth(-25); },readyTypes.PlayerIsNotDead,false),
        new Effect("foodUp",delegate { changeHunger(10); },readyTypes.PlayerIsNotDead,false),
        new Effect("foodUp2",delegate { changeHunger(25); },readyTypes.PlayerIsNotDead,false),
        new Effect("foodDown",delegate { changeHunger(-10); },readyTypes.PlayerIsNotDead,false),
        new Effect("foodDown2",delegate { changeHunger(-25); },readyTypes.PlayerIsNotDead,false),
        new Effect("waterUp",delegate { changeThirst(10); },readyTypes.PlayerIsNotDead,false),
        new Effect("waterUp2",delegate { changeThirst(25); },readyTypes.PlayerIsNotDead,false),
        new Effect("waterDown",delegate { changeThirst(-10); },readyTypes.PlayerIsNotDead,false),
        new Effect("waterDown2",delegate { changeThirst(-25); },readyTypes.PlayerIsNotDead,false),
        new Effect("speedUp",delegate { changeSpeed(true); },readyTypes.SpeedEffectActive,false,reverseEffect: "speedDown"),
        new Effect("speedUp2",delegate { changeSpeed(true); changeSpeed(true); },readyTypes.SpeedEffectActive,false,reverseEffect: "speedDown2"),
        new Effect("speedDown",delegate { changeSpeed(false); },readyTypes.SpeedEffectActive,false,reverseEffect: "speedUp"),
        new Effect("speedDown2",delegate { changeSpeed(false); changeSpeed(false); },readyTypes.SpeedEffectActive,false,reverseEffect: "speedUp2"),
        new Effect("gravityUp",delegate { changeGravity(true, eventRequest == null ? -1 : eventRequest.messageId); },readyTypes.GravityEffectActive,false,reverseEffect: "gravityDown"),
        new Effect("gravityDown",delegate { changeGravity(false, eventRequest == null ? -1 : eventRequest.messageId); },readyTypes.GravityEffectActive,false,reverseEffect: "gravityUp"),
        new Effect("raftSpeedUp",delegate { changeRaftSpeed(true); },readyTypes.RaftSpeedEffectActive,true,reverseEffect: "raftSpeedDown"),
        new Effect("raftSpeedDown",delegate { changeRaftSpeed(false); },readyTypes.RaftSpeedEffectActive,true,reverseEffect: "raftSpeedUp"),
        new Effect("phantomShark",delegate { BlockCreator.RemoveBlockNetwork(cacheAttackableBlock, null, true); },readyTypes.RaftIsAttackable,false),
        new Effect("spawnBear",delegate { trySpawnAnimal(AI_NetworkBehaviourType.Bear); },readyTypes.HasNearbyIsland,true),
        new Effect("spawnMamaBear",delegate { trySpawnAnimal(AI_NetworkBehaviourType.MamaBear); },readyTypes.HasNearbyIsland,true),
        new Effect("spawnStoneBird",delegate { trySpawnAnimal(AI_NetworkBehaviourType.StoneBird); },readyTypes.HasNearbyIsland,true),
        new Effect("spawnStoneBird_Caravan",delegate { trySpawnAnimal(AI_NetworkBehaviourType.StoneBird_Caravan); },readyTypes.HasNearbyIsland,true),
        new Effect("spawnBoar",delegate { trySpawnAnimal(AI_NetworkBehaviourType.Boar); },readyTypes.HasNearbyIsland,true),
        new Effect("spawnButlerBot",delegate { trySpawnAnimal(AI_NetworkBehaviourType.ButlerBot); },readyTypes.HasNearbyIsland,true),
        new Effect("spawnPig",delegate { trySpawnAnimal(AI_NetworkBehaviourType.Pig); },readyTypes.HasNearbyIsland,true),
        new Effect("pacifyShark",pacifyShark,readyTypes.PacifyEffectActive,true),
        new Effect("repairRandomTool",delegate { RAPI.GetLocalPlayer().RepairRandomItem(); },readyTypes.PlayerHasAnyTool,false),
        new Effect("damageRandomTool",delegate { RAPI.GetLocalPlayer().DamageRandomItem(); },readyTypes.PlayerHasAnyTool,false),
        new Effect("teleportToRaft", _ => teleportToRaft(), readyTypes.None, false),
        new Effect("upgradeRaft", _ => upgradeRaft(), readyTypes.CanUpgradeRaft, false),
        new Effect("changeWind", randomizeWind, readyTypes.WindEffectActive, false),
        new Effect("killRandomShark", _ => killRandomShark(), readyTypes.SharkIsAlive, true),
        new Effect("changeHat", _ => changeHat(), readyTypes.PlayerCanEquipHat, false),
        new Effect("raiseAnchors", _ => RaiseAllAnchors(), readyTypes.CanRaiseAnchors, false),
        new Effect("dropAnchors", _ => DropAllAnchors(), readyTypes.CanDropAnchors, false),
        new Effect("enginesOn", _ => AllEnginesOn(), readyTypes.CanTurnOnEngines, false),
        new Effect("enginesOff", _ => AllEnginesOff(), readyTypes.CanTurnOffEngines, false),
        new Effect("changeDirections", _ => ChangeEngineDirections(), readyTypes.HasEngines, false),
        new Effect("wavePower", _ => startBigWaves(), readyTypes.WavePowerEffectActive, false, stopBigWaves),
        new Effect("breakRandomFoundation", delegate{breakRandom(x => x is Block_Foundation); }, readyTypes.HasFoundation, false),
        new Effect("breakRandomFoundation2", delegate{breakRandom(x => x is Block_Foundation && !x.Reinforced); }, readyTypes.HasFoundation2, false),
        new Effect("breakRandomFloor", delegate{breakRandom(x => x.IsWalkable() && !(x is Block_Foundation)); }, readyTypes.HasFloor, false),
        new Effect("teleportOffRaft", _ => teleportOffRaft(), readyTypes.None, false),
        new Effect("fillInventory", _ => fillUpInventory(), readyTypes.HasEmptySlots, false),
        new Effect("flipMouseControls", delegate { Patch_Input.flip = true; }, readyTypes.None, false, delegate { Patch_Input.flip = false; }),
        new Effect("flipCamera", delegate { Patch_Camera.flip = true; }, readyTypes.None, false, delegate { Patch_Camera.flip = false; }),
        new Effect("zoomCamera", delegate { Patch_Zoom.enabled = true; }, readyTypes.None, false, delegate { Patch_Zoom.enabled = false; })
    };

        public static bool ExecuteEvent(int id)
        {
            if (events.Count < id || id < 0)
                Debug.LogError("Did not find event for numeric id " + id);
            Effect e = events[id];
            if (eventRequest != null)
            {
                var e2 = e.GetTimed(eventRequest.messageId,eventRequest.duration);
                if (e2 != null)
                    effectTimers.Add(e2);
            }
            return e.Execute(eventRequest?.duration??0);
        }
        public static bool ExecuteEvent(string id)
        {
            Effect e = events[id];
            if (e == null)
                Debug.LogError("Did not find event for string id " + id);
            if (eventRequest != null)
            {
                var e2 = e.GetTimed(eventRequest.messageId,eventRequest.duration);
                if (e2 != null)
                    effectTimers.Add(e2);
            }
            return e.Execute(eventRequest?.duration ?? 0);
        }


        public static void breakRandom(Predicate<Block> validTarget)
        {
            var l = new List<Block>();
            foreach (var b in BlockCreator.GetPlacedBlocks())
                if (validTarget(b))
                    l.Add(b);
            BlockCreator.RemoveBlockNetwork(l[(int)(rand.NextDouble() * l.Count)], null, true);
        }

        public static void spawnShark()
        {
            if (Raft_Network.IsHost)
            {
                Network_Host_Entities entity = ComponentManager<Network_Host_Entities>.Value;
                AI_NetworkBehaviour anb = entity.CreateAINetworkBehaviour(AI_NetworkBehaviourType.Shark, entity.GetSharkSpawnPosition());
                if (eventRequest.sender.m_SteamID == 0)
                    spawnedAnimals.Add(new AnimalEffect(eventRequest.messageId, eventRequest.viewerName, (int)anb.ObjectIndex));
                if (logNetworking)
                    Debug.Log("shark spawned, sending reply");
                sendReply((int)anb.ObjectIndex);
            }
            else
            {
                if (eventRequest.sender.m_SteamID == 0)
                    spawnedAnimals.Add(new AnimalEffect(eventRequest.messageId, eventRequest.viewerName, -1));
                sendRequest("spawnShark");
            }
        }

        public static void skipTime()
        {
            if (Raft_Network.IsHost)
            {
                AzureSkyTimeOfDayComponent sc = ComponentManager<AzureSkyController>.Value.timeOfDay;
                if (sc.hour > 12)
                {
                    sc.hour -= 12;
                    sc.JumpToNextDay();
                }
                else
                    sc.hour += 12;
                sendReply();
            }
            else
                sendRequest("skipTime");
        }

        public static void changeHealth(float amount)
        {
            RAPI.GetLocalPlayer().Stats.Damage(-amount, RAPI.GetLocalPlayer().PersonController.transform.position, Vector3.up, EntityType.Environment);
        }

        public static void changeHunger(float amount)
        {
            RAPI.GetLocalPlayer().Stats.stat_hunger.Normal.Value += amount;
        }

        public static void changeThirst(float amount)
        {
            RAPI.GetLocalPlayer().Stats.stat_thirst.Normal.Value += amount;
        }

        public static void changeSpeed(bool increase)
        {
            PersonController control = RAPI.GetLocalPlayer().PersonController;
            if (increase)
            {
                control.normalSpeed *= 1.5f;
                control.sprintSpeed *= 1.5f;
                control.swimSpeed *= 1.5f;
            }
            else
            {
                control.normalSpeed /= 1.5f;
                control.sprintSpeed /= 1.5f;
                control.swimSpeed /= 1.5f;
            }
        }

        public static void changeGravity(bool increase, int eventID = -1)
        {
            if (increase)
                RAPI.GetLocalPlayer().PersonController.gravity *= 1.5f;
            else
                RAPI.GetLocalPlayer().PersonController.gravity /= 1.5f;
        }

        public static void changeRaftSpeed(bool increase)
        {
            if (Raft_Network.IsHost)
            {
                if (increase)
                    ComponentManager<Raft>.Value.maxSpeed *= 1.2f;
                else
                    ComponentManager<Raft>.Value.maxSpeed /= 1.2f;
                sendReply();
            }
            else
                sendRequest(increase ? "raftSpeedUp" : "raftSpeedDown");
        }

        public static bool getTargetableBlock()
        {

            RaftBounds bounds = FindObjectOfType<RaftBounds>();
            List<Block> blocks = new List<Block>();
            List<AngleRange> ranges = new List<AngleRange>();
            foreach (Block block in bounds.walkableBlocks)
                if (block is Block_Foundation)
                {
                    blocks.Add(block);
                    ranges.Add(new AngleRange());
                }
            for (int i = 0; i < blocks.Count; i++)
                for (int j = 0; j < blocks.Count; j++)
                {
                    float dist = blocks[i].transform.position.DistanceXZ(blocks[j].transform.position) / 2;
                    if (j != i && dist < 5)
                    {
                        double vary = Math.Acos(dist / 5);
                        double main = Math.Acos(((double)blocks[i].transform.position.x - blocks[j].transform.position.x) / (dist * 2)) * (blocks[i].transform.position.z < blocks[j].transform.position.z ? -1 : 1);
                        ranges[i] -= new AngleRange(main - vary, main + vary);
                        ranges[j] -= new AngleRange(main - vary + Math.PI, main + vary + Math.PI);
                    }
                }
            List<Block> tmp = new List<Block>();
            for (int i = 0; i < blocks.Count; i++)
                if (!ranges[i].Empty)
                {
                    tmp.Add(blocks[i]);
                }
            if (tmp.Count == 0)
                return false;
            cacheAttackableBlock = tmp[(int)(rand.NextDouble() * tmp.Count)];
            return true;
        }

        public static ChunkPoint getValidIsland()
        {
            ChunkManager manager = ComponentManager<ChunkManager>.Value;
            ChunkPoint nearestValidChunk = null;
            float dist = float.MaxValue;
            Vector3 player = RAPI.GetLocalPlayer().PersonController.transform.position;
            List<ChunkPoint> chunkPointsFromPointType = manager.GetAllChunkPointsList();
            foreach (ChunkPoint chunk in chunkPointsFromPointType)
                if (chunk.spawner != null && chunk.spawner.ChunkPointType != ChunkPointType.None)
                {
                    float newDist = player.DistanceXZ(chunk.worldPosition);
                    if (newDist < dist)
                    {
                        nearestValidChunk = chunk;
                        dist = newDist;
                    }
                }
            if (dist > 200)
                return null;
            return nearestValidChunk;
        }

        public static AI_NetworkBehaviour spawnAnimal(AI_NetworkBehaviourType animalType)
        {
            Vector3 spawnPos = Vector3.zero;
            bool flag = false;
            float start = Time.time;
            while (!flag)
            {
                spawnPos = spawnChunk.worldPosition.XZOnly() + new Vector3((float)(rand.NextDouble() - 0.5) * ChunkManager.ChunkSize, 200, (float)(rand.NextDouble() - 0.5) * ChunkManager.ChunkSize);
                foreach (RaycastHit hit in Physics.RaycastAll(spawnChunk.worldPosition.XZOnly() + new Vector3((float)(rand.NextDouble() - 0.5) * ChunkManager.ChunkSize, 200, (float)(rand.NextDouble() - 0.5) * ChunkManager.ChunkSize), Vector3.down, 500))
                {
                    if (spawnPos.y > hit.point.y && hit.point.y > 1 && !hit.collider.transform.ParentedToRaft() && hit.collider.GetComponent<PickupItem>() == null)
                    {
                        flag = true;
                        spawnPos = hit.point;
                    }
                }
                if (Time.time > start + 10)
                    throw new OperationCanceledException("Failed to find animal spawn location in time.");
            }
            return ComponentManager<Network_Host_Entities>.Value.CreateAINetworkBehaviour(animalType, spawnPos);
        }

        public static void trySpawnAnimal(AI_NetworkBehaviourType animal)
        {
            if (logNetworking)
                Debug.Log("trying to spawn animal as " + (Raft_Network.IsHost ? "host" : "client"));
            if (Raft_Network.IsHost)
            {
                AI_NetworkBehaviour anb = spawnAnimal(animal);
                if (eventRequest.sender.m_SteamID == 0)
                    spawnedAnimals.Add(new AnimalEffect(eventRequest.messageId, eventRequest.viewerName, (int)anb.ObjectIndex));
                if (logNetworking)
                    Debug.Log(animal + " spawned, sending reply");
                sendReply((int)anb.ObjectIndex);
            }
            else
            {
                spawnedAnimals.Add(new AnimalEffect(eventRequest.messageId, eventRequest.viewerName, -1));
                if (logNetworking)
                    Debug.Log(animal + " requested");
                sendRequest("spawn" + animal.ToString());
            }
        }

        public static void startBigWaves()
        {
            if (logNetworking)
                Debug.Log("trying to start big waves as " + (Raft_Network.IsHost ? "host" : "client"));
            if (Raft_Network.IsHost)
            {
                waves = true;
                StartModifyWaveSpectrums();
                sendReply();
            }
            else
                sendRequest("wavePower");
        }
        public static void stopBigWaves()
        {
            if (logNetworking)
                Debug.Log("trying to stop big waves as " + (Raft_Network.IsHost ? "host" : "client"));
            if (Raft_Network.IsHost)
            {
                waves = false;
                UnmodifyWaveSpectrums();
                sendReply();
            }
        }

        public static void pacifyShark(float duration)
        {
            if (Raft_Network.IsHost)
            {
                sharkPacify = duration;
                sendReply();
            }
            else
                sendRequest("pacifyShark");
        }

        public static void teleportToRaft()
        {
            Network_Player player = RAPI.GetLocalPlayer();
            RaftBounds bounds = ComponentManager<RaftBounds>.Value;
            Vector3 pos = bounds.walkableBlocks[(int)(rand.NextDouble() * bounds.walkableBlocks.Count)].transform.position + new Vector3(0, 1, 0);
            ComponentManager<Raft_Network>.Value.RPC(new Message_Teleport(Messages.Teleport, player.Network.NetworkIDManager, player, pos), Target.All, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
        }
        public static void teleportOffRaft()
        {
            var p = ComponentManager<RaftBounds>.Value.Center + Vector3.up * 10;
            var r = (float)(360 * rand.NextDouble());
            var player = RAPI.GetLocalPlayer();
            while (Physics.Raycast(p, Vector3.down, 30, LayerMasks.MASK_GroundMask))
                p += Quaternion.Euler(0, r, 0) * Vector3.forward;
            var water = Traverse.Create(typeof(WaterPointGetter)).Field("water").GetValue<Water>();
            p.y = water.GetHeightAt(p.x, p.z, water.Time);
            ComponentManager<Raft_Network>.Value.RPC(new Message_Teleport(Messages.Teleport, player.Network.NetworkIDManager, player, p), Target.All, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
        }

        public static void fillUpInventory()
        {
            var inv = RAPI.GetLocalPlayer().Inventory;
            var ui = new[] { 20, 21, 25, 50, 53, 95 };
            var items = ItemManager.GetAllItems().FindAll(x => x.settings_recipe?.SubCategory == "Utopia" || ui.Contains(x.UniqueIndex));
            foreach (var i in inv.allSlots)
                if (i.active && !i.HasValidItemInstance())
                {
                    var ind = (int)(items.Count * rand.NextDouble());
                    i.SetItem(items[ind], 1);
                    items.RemoveAt(ind);
                    if (items.Count == 0)
                        break;
                }
        }

        public static void upgradeRaft()
        {
            Network_Player player = RAPI.GetLocalPlayer();
            List<Block> blocks = new List<Block>();
            RaftBounds bounds = FindObjectOfType<RaftBounds>();
            foreach (Block block in bounds.walkableBlocks)
                if (block is Block_Foundation && !block.Reinforced)
                    blocks.Add(block);
            Block block_Foundation = blocks[(int)(rand.NextDouble() * blocks.Count)];
            block_Foundation.Reinforced = true;
            new Message_Upgrade(block_Foundation.ObjectIndex).Message.Broadcast(MessageType.Channel);
        }

        public static void randomizeWind(float duration)
        {
            double angle = rand.NextDouble() * 2 * Math.PI;
            windDirection = new Vector4((float)Math.Sin(angle), 0, (float)Math.Cos(angle), duration);
            new Message_Wind((float)angle, 120).Message.Broadcast(MessageType.Channel);
        }

        public static void changeHat()
        {
            Network_Player player = RAPI.GetLocalPlayer();
            List<Slot_Equip> empty = new List<Slot_Equip>();
            List<Slot_Equip> hats = new List<Slot_Equip>();
            foreach (Slot_Equip slot in player.Inventory.equipSlots)
                if (!slot.HasValidItemInstance())
                    empty.Add(slot);
                else if (slot.itemInstance.settings_equipment.EquipType == EquipSlotType.Head)
                    hats.Add(slot);
            if (hats.Count == 0)
                empty[0].SetItem(VisionHats[(int)(rand.NextDouble() * VisionHats.Count)], 1);
            else
            {
                ItemInstance item = hats[0].itemInstance;
                hats[0].SetItem(VisionHats[(int)(rand.NextDouble() * VisionHats.Count)], 1);
                player.Inventory.AddItem(item);
            }
        }

        public static void killRandomShark()
        {
            List<Network_Entity> entities = new List<Network_Entity>();
            foreach (AI_NetworkBehaviour entity in FindObjectsOfType<AI_NetworkBehaviour>())
                if (entity is AI_NetworkBehavior_Shark && !entity.networkEntity.IsDead)
                    entities.Add(entity.networkEntity);
            Network_Entity target = entities[(int)(rand.NextDouble() * entities.Count)];
            Raft_Network network = target.Network;
            Message_NetworkEntity_Damage message = new Message_NetworkEntity_Damage(Messages.DamageEntity, network.NetworkIDManager, ComponentManager<Network_Host_Entities>.Value.ObjectIndex, target.ObjectIndex, target.stat_health.Value, target.transform.position, Vector3.up, EntityType.Environment, null);
            if (Raft_Network.IsHost)
            {
                target.Damage(message.damage, message.HitPosition, message.HitNormal, message.damageInflictorEntityType, null);
                RAPI.SendNetworkMessage(message);
            }
            else
                network.SendP2P(network.HostID, message, EP2PSend.k_EP2PSendReliable, NetworkChannel.Channel_Game);
        }

        public static GameObject CreateText(Transform canvas_transform, float x, float y, string text_to_print, int font_size, Color text_color, float width, float height, Font font, string name = "Text")
        {
            GameObject UItextGO = new GameObject("Text");
            UItextGO.transform.SetParent(canvas_transform, false);
            RectTransform trans = UItextGO.AddComponent<RectTransform>();
            trans.sizeDelta = new Vector2(width, height);
            trans.anchoredPosition = new Vector2(x, y);
            Text text = UItextGO.AddComponent<Text>();
            text.text = text_to_print;
            text.font = font;
            text.fontSize = font_size;
            text.color = text_color;
            text.name = name;
            Shadow shadow = UItextGO.AddComponent<Shadow>();
            shadow.effectColor = new Color();
            return UItextGO;
        }
        public static void AddTextShadow(GameObject textObject, Color shadowColor, Vector2 shadowOffset)
        {
            Shadow shadow = textObject.AddComponent<Shadow>();
            shadow.effectColor = shadowColor;
            shadow.effectDistance = shadowOffset;
        }
        public static void CopyTextShadow(GameObject textObject, GameObject shadowSource)
        {
            Shadow sourcesShadow = shadowSource.GetComponent<Shadow>();
            if (sourcesShadow == null)
                sourcesShadow = shadowSource.GetComponentInChildren<Shadow>();
            AddTextShadow(textObject, sourcesShadow.effectColor, sourcesShadow.effectDistance);
        }

        public static void DropAllAnchors()
        {
            foreach (var anchor in GameObject.FindObjectsOfType<Anchor_Stationary>())
                if (anchor.CanUse && !anchor.AtBottom)
                    anchor.UseAnchor();
        }

        public static bool CanDropAnchor()
        {
            foreach (var anchor in GameObject.FindObjectsOfType<Anchor_Stationary>())
                if (anchor.CanUse && !anchor.AtBottom)
                    return true;
            return false;
        }

        public static void RaiseAllAnchors()
        {
            foreach (var anchor in GameObject.FindObjectsOfType<Anchor_Stationary>())
                if (anchor.CanUse && anchor.AtBottom)
                    anchor.UseAnchor();
        }

        public static bool CanRaiseAnchor()
        {
            foreach (var anchor in GameObject.FindObjectsOfType<Anchor_Stationary>())
                if (anchor.CanUse && anchor.AtBottom)
                    return true;
            return false;
        }

        public static void AllEnginesOn()
        {
            foreach (var engine in RaftVelocityManager.Motors)
                if (!engine.engineSwitchOn)
                    engine.OnButtonPressed_ToggleEngine();
            foreach (var sail in Sail.AllSails)
                if (!sail.open)
                    sail.Toggle();
        }

        public static bool CanEngineOn()
        {
            foreach (var engine in RaftVelocityManager.Motors)
                if (!engine.engineSwitchOn)
                    return true;
            foreach (var sail in Sail.AllSails)
                if (!sail.open)
                    return true;
            return false;
        }

        public static void AllEnginesOff()
        {
            foreach (var engine in RaftVelocityManager.Motors)
                if (engine.engineSwitchOn)
                    engine.OnButtonPressed_ToggleEngine();
            foreach (var sail in Sail.AllSails)
                if (sail.open)
                    sail.Toggle();
        }

        public static bool CanEngineOff()
        {
            foreach (var engine in RaftVelocityManager.Motors)
                if (engine.engineSwitchOn)
                    return true;
            foreach (var sail in Sail.AllSails)
                if (sail.open)
                    return true;
            return false;
        }

        public static void ChangeEngineDirections()
        {
            foreach (var engine in RaftVelocityManager.Motors)
                engine.OnButtonPressed_ChangeDirection();
            foreach (var sail in Sail.AllSails)
                sail.Rotate(180);
        }

        public static Dictionary<WaterWavesSpectrum, (float, float)> edited = new Dictionary<WaterWavesSpectrum, (float, float)>();
        static void RemodifyWaveSpectrums()
        {
            foreach (var s in edited)
            {
                Traverse obj = Traverse.Create(s.Key);
                obj.Field("_Amplitude").SetValue(EditValue(s.Value.Item1));
                obj.Field("_WindSpeed").SetValue(EditValue(s.Value.Item2));
            }
            MarkDirty();
        }
        static void StartModifyWaveSpectrums()
        {
            foreach (var p in Resources.FindObjectsOfTypeAll<WaterProfile>())
            {
                Traverse obj = Traverse.Create(p.Data.Spectrum);
                edited.Add(p.Data.Spectrum, (obj.Field("_Amplitude").GetValue<float>(), obj.Field("_WindSpeed").GetValue<float>()));
            }
            RemodifyWaveSpectrums();
        }
        static void UnmodifyWaveSpectrums()
        {
            foreach (var s in edited)
            {
                Traverse obj = Traverse.Create(s.Key);
                obj.Field("_Amplitude").SetValue(s.Value.Item1);
                obj.Field("_WindSpeed").SetValue(s.Value.Item2);
            }
            edited.Clear();
            MarkDirty();
        }

        static void MarkDirty()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<Water>())
            {
                foreach (var p in w.ProfilesManager.Profiles)
                    p.Profile.Dirty = true;
                w.WindWaves.SpectrumResolver.GetCachedSpectraDirect().Clear();
            }
        }
        public static float EditValue(float originalValue)
        {
            //Log("Data-- " + originalValue.ToString() + ", " + wavePower.ToString() + ", " + waveMulti.ToString() + ", " + ((float)Math.Pow(originalValue, wavePower)).ToString());
            return (float)Math.Pow(originalValue, 2);
        }
    }

    public static class ExtentionMethods
    {
        public static void giveItem(this Network_Player player, string spawnType = "all")
        {
            List<Item_Base> items = player.getSpawnItems(spawnType);
            Item_Base item = items[Main.rand.Next(items.Count - 1)];
            if (Main.logNetworking)
                Debug.Log(item.UniqueIndex);
            player.Inventory.AddItem(new ItemInstance(item, (spawnType == "resource") ? 10 : 1, item.MaxUses));
        }

        public static List<Item_Base> getSpawnItems(this Network_Player player, string spawnType = "all")
        {
            List<Item_Base> items = new List<Item_Base>();
            switch (spawnType)
            {
                case "food":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if ((item.settings_consumeable.HungerYield > 0 || item.settings_consumeable.BonusHungerYield > 0) && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item);
                    break;
                case "drink":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if (item.settings_consumeable.ThirstYield > 0 && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item);
                    break;
                case "edible":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if ((item.settings_consumeable.HungerYield > 0 || item.settings_consumeable.BonusHungerYield > 0 || item.settings_consumeable.ThirstYield > 0) && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item);
                    break;
                case "smelted":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if (item.settings_cookable.CookingResult != null && item.settings_cookable.CookingResult.item != null && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item.settings_cookable.CookingResult.item);
                    break;
                case "block":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if (item.settings_buildable.Placeable && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item);
                    break;
                case "equip":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if (item.settings_equipment.EquipType != EquipSlotType.None && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item);
                    break;
                case "copy":
                    foreach (Slot item in player.Inventory.allSlots)
                        if (item.active && item.HasValidItemInstance() && !Main.BadItems.Contains(item.GetItemBase().UniqueName))
                            items.AddUniqueOnly(item.GetItemBase());
                    break;
                case "weapon":
                    foreach (ItemConnection connect in player.PlayerItemManager.useItemController.allConnections)
                        if ((connect.obj.GetComponent<MeleeWeapon>() != null || connect.obj.GetComponent<ThrowableComponent>() || connect.obj.GetComponent<Throwable>()) && !Main.BadItems.Contains(connect.inventoryItem.UniqueName))
                            items.Add(connect.inventoryItem);
                    break;
                case "resource":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if (item.settings_consumeable.FoodType == FoodType.None && item.settings_cookable.CookingResult.item == null && item.settings_recipe.CraftingCategory == CraftingCategory.Nothing && !item.settings_buildable.Placeable && !item.settings_buildable.HasBuildablePrefabs && item.settings_equipment.EquipType == EquipSlotType.None && !item.settings_recipe.IsBlueprint && !Main.BadItems.Contains(item.UniqueName))
                            items.Add(item);
                    break;
                case "all":
                    foreach (Item_Base item in ItemManager.GetAllItems())
                        if (item.settings_buildable.Placeable == item.settings_buildable.HasBuildablePrefabs && !Main.BadItems.Contains(item.UniqueName) && !item.settings_recipe.IsBlueprint)
                            items.Add(item);
                    break;
            }
            if (items.Count == 0)
                items.Add(ItemManager.GetItemByName("Plank"));
            return items;
        }

        public static void DropRandomItem(this Network_Player player)
        {
            List<Slot> items = new List<Slot>();
            foreach (Slot item in player.Inventory.allSlots)
                if (item.active && item.HasValidItemInstance() && !item.locked)
                    items.Add(item);
            if (player.Inventory.GetSelectedHotbarItem() != null && player.Inventory.GetSelectedHotbarItem().UniqueName != "Hammer")
                ComponentManager<CanvasHelper>.Value.CloseMenu(MenuType.BuildMenu);
            player.Inventory.DropItem(items[Main.rand.Next(items.Count - 1)]);
        }

        public static void DropCurrentItem(this Network_Player player)
        {
            ComponentManager<CanvasHelper>.Value.CloseMenu(MenuType.BuildMenu);
            RAPI.GetLocalPlayer().Inventory.DropCurrentItem();
        }

        public static void RepairRandomItem(this Network_Player player)
        {
            List<Slot> items = new List<Slot>();
            foreach (Slot item in player.Inventory.allSlots)
                if (item.active && item.HasValidItemInstance() && item.itemInstance.settings_consumeable.FoodType == FoodType.None && item.itemInstance.BaseItemMaxUses > 1)
                    items.Add(item);
            Slot Target = items[Main.rand.Next(items.Count - 1)];
            Target.IncrementUses(Target.itemInstance.BaseItemMaxUses - Target.itemInstance.Uses);
        }

        public static void DamageRandomItem(this Network_Player player)
        {
            List<Slot> items = new List<Slot>();
            foreach (Slot item in player.Inventory.allSlots)
                if (item.active && item.HasValidItemInstance() && item.itemInstance.settings_consumeable.FoodType == FoodType.None && item.itemInstance.BaseItemMaxUses > 1)
                    items.Add(item);
            Slot Target = items[Main.rand.Next(items.Count - 1)];
            Target.IncrementUses(-(int)Math.Ceiling(Target.itemInstance.BaseItemMaxUses / 10f));
        }

        public static bool HasToolItem(this Slot slot)
        {
            return slot.active && slot.HasValidItemInstance() && slot.itemInstance.settings_consumeable.FoodType == FoodType.None && slot.itemInstance.BaseItemMaxUses > 1;
        }

        public static void UseAnchor(this Anchor_Stationary anchor)
        {
            new Message_NetworkBehaviour(Messages.StationaryAnchorUse, anchor).Broadcast();
            if (Raft_Network.IsHost)
                Traverse.Create(anchor).Method("Use").GetValue();
        }

        public static void Toggle(this Sail sail)
        {
            new Message_NetworkBehaviour(sail.open ? Messages.Sail_Close : Messages.Sail_Open, sail).Broadcast();
            if (Raft_Network.IsHost)
            {
                if (sail.open)
                    Traverse.Create(sail).Method("Close").GetValue();
                else
                    sail.Open();
            }
        }

        public static void Rotate(this Sail sail, float amount)
        {
            if (Raft_Network.IsHost)
                Traverse.Create(sail).Method("Rotate", new object[] { amount }).GetValue();
            else
                new Message_Sail_Rotate(Messages.Sail_Rotate, sail, amount).Broadcast();
        }

        public static void Broadcast(this Message message, NetworkChannel channel = 0)
        {
            var network = ComponentManager<Raft_Network>.Value;
            if (Main.logNetworking)
                Debug.Log($"broadcasting message as {(Raft_Network.IsHost ? "host" : "client")} to channel {channel} (host id is: {network.HostID})");
            if (Raft_Network.IsHost)
                network.RPC(message, Target.Other, EP2PSend.k_EP2PSendReliable, channel);
            else
                network.SendP2P(network.HostID, message, EP2PSend.k_EP2PSendReliable, channel);
        }

        public static void SendP2P(this Message message, CSteamID steamID, NetworkChannel channel = 0)
        {
            if (Main.logNetworking)
                Debug.Log($"sending message to player {steamID.m_SteamID} on channel {channel}");
            ComponentManager<Raft_Network>.Value.SendP2P(steamID, message, EP2PSend.k_EP2PSendReliable, channel);
        }

        public static string String(this byte[] bytes, int length = -1, int offset = 0)
        {
            string str = "";
            if (length == -1)
                length = (bytes.Length - offset) / 2;
            while (str.Length < length)
            {
                str += BitConverter.ToChar(bytes, offset + str.Length * 2);
            }
            return str;

        }
        public static string String(this List<byte> bytes) => bytes.ToArray().String();
        public static byte[] Bytes(this string str)
        {
            var data = new List<byte>();
            foreach (char chr in str)
                data.AddRange(BitConverter.GetBytes(chr));
            return data.ToArray();
        }
        public static int Integer(this byte[] bytes, int offset = 0) => BitConverter.ToInt32(bytes, offset);
        public static uint UInteger(this byte[] bytes, int offset = 0) => BitConverter.ToUInt32(bytes, offset);
        public static float Float(this byte[] bytes, int offset = 0) => BitConverter.ToSingle(bytes, offset);
        public static Vector3 Vector3(this byte[] bytes, int offset = 0) => new Vector3(bytes.Float(offset), bytes.Float(offset + 4), bytes.Float(offset + 8));
        public static byte[] Bytes(this int value) => BitConverter.GetBytes(value);
        public static byte[] Bytes(this uint value) => BitConverter.GetBytes(value);
        public static byte[] Bytes(this float value) => BitConverter.GetBytes(value);
        public static byte[] Bytes(this Vector3 value)
        {
            var data = new byte[12];
            value.x.Bytes().CopyTo(data, 0);
            value.y.Bytes().CopyTo(data, 4);
            value.z.Bytes().CopyTo(data, 8);
            return data;
        }
    }
    public struct MessageType
    {
        public const int ChannelID = 142;
        public const NetworkChannel Channel = (NetworkChannel)ChannelID;
        public const Messages MessageID = (Messages)88;
        public const int Execute = 0;
        public const int Executed = 1;
        public const int Wind = 2;
        public const int Upgrade = 3;
    }

    class Message_ExecuteEvent
    {
        public Message_InitiateConnection Message
        {
            get
            {
                var data = new List<byte>();
                data.AddRange(eventID.Bytes());
                data.AddRange(messageID.Bytes());
                data.AddRange(duration.Bytes());
                return new Message_InitiateConnection(MessageType.MessageID, MessageType.Execute, data.String());
            }
        }
        public int EventID { get { return eventID; } }
        public int MessageID { get { return messageID; } }
        public float Duration { get { return messageID; } }
        int eventID;
        int messageID;
        float duration;
        public Message_ExecuteEvent(int eventId, int messageId, float duration)
        {
            eventID = eventId;
            messageID = messageId;
            this.duration = duration;
        }
        public Message_ExecuteEvent(Message_InitiateConnection message)
        {
            var data = message.password.Bytes();
            eventID = data.Integer(0);
            messageID = data.Integer(4);
            duration = data.Float(8);
        }
    }

    class Message_EventExecuted
    {
        public Message_InitiateConnection Message
        {
            get
            {
                var data = new List<byte>();
                data.AddRange(messageID.Bytes());
                data.AddRange(extra.Bytes());
                return new Message_InitiateConnection(MessageType.MessageID, MessageType.Executed, data.String());
            }
        }
        public int MessageID { get { return messageID; } }
        public int ExtraData { get { return extra; } }
        int messageID;
        int extra;
        public Message_EventExecuted(int messageId, int extraData = -1)
        {
            messageID = messageId;
            extra = extraData;
        }
        public Message_EventExecuted(Message_InitiateConnection message)
        {
            var data = message.password.Bytes();
            messageID = data.Integer(0);
            extra = data.Integer(4);
        }
    }

    class Message_Wind
    {
        public Message_InitiateConnection Message
        {
            get
            {
                var data = new List<byte>();
                data.AddRange(angle.Bytes());
                data.AddRange(time.Bytes());
                return new Message_InitiateConnection(MessageType.MessageID, MessageType.Wind, data.String());
            }
        }
        public float Angle { get { return angle; } }
        public float Time { get { return time; } }
        public float angle;
        public float time;
        public Message_Wind(float Angle, float Time)
        {
            angle = Angle;
            time = Time;
        }
        public Message_Wind(Message_InitiateConnection message)
        {
            var data = message.password.Bytes();
            angle = data.Float(0);
            time = data.Float(4);
        }
    }

    class Message_Upgrade
    {
        public Message_InitiateConnection Message
        {
            get
            {
                return new Message_InitiateConnection(MessageType.MessageID, MessageType.Upgrade, objectIndex.Bytes().String());
            }
        }
        public uint ObjectIndex { get { return objectIndex; } }
        uint objectIndex;
        public Message_Upgrade(uint ObjectIndex)
        {
            objectIndex = ObjectIndex;
        }
        public Message_Upgrade(Message_InitiateConnection message)
        {
            var data = message.password.Bytes();
            objectIndex = data.UInteger(0);
        }
    }

    [HarmonyPatch(typeof(Helper), "IsValidWaterTarget")]
    public class Patch_SharkPlayerTargeter
    {
        static bool Prefix(ref bool __result)
        {
            if (Main.sharkPacify > 0 && Environment.StackTrace.Contains("at AI_StateMachine_Shark"))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(AI_NetworkBehaviour), "OnDestroy")]
    public class Patch_AnimalDestroy
    {
        static void Prefix(ref AI_NetworkBehaviour __instance)
        {
            for (int i = 0; i < Main.spawnedAnimals.Count; i++)
                if (Main.spawnedAnimals[i].animalId == __instance.ObjectIndex)
                {
                    Main.spawnedAnimals.RemoveAt(i);
                    break;
                }
        }
    }

    [HarmonyPatch(typeof(Block_Foundation), "Reinforced", MethodType.Getter)]
    public class Patch_SharkBlockTargeter
    {
        static void Postfix(ref bool __result)
        {
            if (Main.sharkPacify > 0 && Environment.StackTrace.Contains("at AI_StateMachine_Shark"))
                __result = true;
        }
    }

    [HarmonyPatch(typeof(Raft), "FixedUpdate")]
    public class Patch_RaftMovement
    {
        static bool Prefix(ref Raft __instance)
        {
            if (Main.windDirection.w == 0)
                return true;
            if (!Raft_Network.IsHost)
            {
                return false;
            }
            if (GameModeValueManager.GetCurrentGameModeValue().raftSpecificVariables.isRaftAlwaysAnchored)
            {
                return false;
            }
            if (!Raft_Network.WorldHasBeenRecieved && !GameManager.IsInNewGame)
            {
                return false;
            }
            Rigidbody body = Traverse.Create(__instance).Field("body").GetValue<Rigidbody>();
            if (!__instance.IsAnchored)
            {
                float speed = Traverse.Create(__instance).Field("speed").GetValue<float>();
                if (speed != 0f)
                {
                    Traverse<Vector3> moveDirection = Traverse.Create(__instance).Field<Vector3>("moveDirection");
                    moveDirection.Value = new Vector3(Main.windDirection.x, Main.windDirection.y, Main.windDirection.z);
                    bool flag = RaftVelocityManager.MotorDirection != Vector3.zero;
                    if (!flag)
                    {
                        List<Sail> allSails = Sail.AllSails;
                        Vector3 vector = Vector3.zero;
                        for (int i = 0; i < allSails.Count; i++)
                        {
                            Sail sail = allSails[i];
                            if (sail.open)
                            {
                                vector += sail.GetNormalizedDirection();
                            }
                        }
                        if (vector.z < 0f)
                        {
                            if ((double)Mathf.Abs(vector.x) > 0.7)
                            {
                                vector.z = moveDirection.Value.z;
                                moveDirection.Value = new Vector3(moveDirection.Value.x, moveDirection.Value.y, 0);
                            }
                            else
                            {
                                vector.z = -0.8f;
                            }
                        }
                        moveDirection.Value += vector;
                    }
                    else
                    {
                        moveDirection.Value = RaftVelocityManager.MotorDirection;
                    }
                    Traverse<float> currentMovementSpeed = Traverse.Create(__instance).Field<float>("currentMovementSpeed");
                    currentMovementSpeed.Value = speed;
                    if (flag)
                    {
                        currentMovementSpeed.Value = RaftVelocityManager.motorSpeed;
                        if (RaftVelocityManager.MotorWheelWeightStrength == MotorWheel.WeightStrength.Weak)
                        {
                            currentMovementSpeed.Value *= 0.5f;
                        }
                        if (currentMovementSpeed.Value < speed)
                        {
                            currentMovementSpeed.Value = speed;
                        }
                    }
                    if (speed != 0f)
                    {
                        if (currentMovementSpeed.Value > __instance.maxSpeed)
                        {
                            currentMovementSpeed.Value = __instance.maxSpeed;
                        }
                        moveDirection.Value = Vector3.ClampMagnitude(moveDirection.Value, 1f);
                        body.AddForce(moveDirection.Value * currentMovementSpeed.Value);
                    }
                }
                List<SteeringWheel> steeringWheels = RaftVelocityManager.steeringWheels;
                float num = 0f;
                foreach (SteeringWheel steeringWheel in steeringWheels)
                {
                    num += steeringWheel.SteeringRotation;
                }
                num = Mathf.Clamp(num, -1f, 1f);
                if (num != 0f)
                {
                    Vector3 torque = new Vector3(0f, Mathf.Tan(0.017453292f * num), 0f) * __instance.maxSteeringTorque;
                    body.AddTorque(torque, ForceMode.Acceleration);
                }
            }
            else
            {
                Traverse<Vector3> anchorPosition = Traverse.Create(__instance).Field<Vector3>("anchorPosition");
                float maxDistanceFromAnchorPoint = Traverse.Create(__instance).Field("maxDistanceFromAnchorPoint").GetValue<float>();
                float num2 = __instance.transform.position.DistanceXZ(anchorPosition.Value);
                if (num2 >= maxDistanceFromAnchorPoint * 3f)
                {
                    anchorPosition.Value = __instance.transform.position;
                }
                if (num2 > maxDistanceFromAnchorPoint)
                {
                    Vector3 vector2 = anchorPosition.Value - __instance.transform.position;
                    vector2.y = 0f;
                    body.AddForce(vector2.normalized * 2f);
                }
            }
            if (body.velocity.sqrMagnitude > __instance.maxVelocity)
            {
                body.velocity = Vector3.ClampMagnitude(body.velocity, __instance.maxVelocity);
            }
            Traverse.Create(__instance).Field("eventEmitter_idle").GetValue<FMODUnity.StudioEventEmitter>().SetParameter("velocity", body.velocity.sqrMagnitude / __instance.maxVelocity);
            Traverse.Create(__instance).Field("previousPosition").SetValue(body.transform.position);
            return false;
        }
    }

    [HarmonyPatch(typeof(Streamer), "Update")]
    public class Patch_StreamerUpdate
    {
        static bool Prefix(ref Streamer __instance)
        {
            if (Main.windDirection.w == 0)
                return true;
            Transform streamerTransform = Traverse.Create(__instance).Field("streamerTransform").GetValue<Transform>();
            Quaternion b = Quaternion.LookRotation(-new Vector3(Main.windDirection.x, Main.windDirection.y, Main.windDirection.z), streamerTransform.up);
            streamerTransform.rotation = Quaternion.Lerp(streamerTransform.rotation, b, Time.deltaTime * Traverse.Create(__instance).Field("rotationSpeed").GetValue<float>());
            return false;
        }
    }

    [HarmonyPatch(typeof(WaterWavesSpectrum), MethodType.Constructor, new Type[] { typeof(float), typeof(float), typeof(float), typeof(float) })]
    public class Patch_WaveCalculatorCreate
    {
        static void Prefix(WaterWavesSpectrum __instance, ref float amplitude, ref float windSpeed)
        {
            if (Main.waves)
            {
                Main.edited.Add(__instance, (amplitude, windSpeed));
                amplitude = Main.EditValue(amplitude);
                windSpeed = Main.EditValue(windSpeed);
            }
        }
    }

    [HarmonyPatch(typeof(MouseLook), "Sensitivity", MethodType.Getter)]
    static class Patch_Input
    {
        public static bool flip = false;
        static void Postfix(ref float __result)
        {
            if (flip)
                __result = -__result;
        }
    }

    [HarmonyPatch(typeof(EZCameraShake.CameraShaker), "Update")]
    static class Patch_Camera
    {
        public static bool flip = false;
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            code.InsertRange(code.FindIndex(x => x.opcode == OpCodes.Stloc_0) - 1, new[] {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(Patch_Camera),"Edit"))
            });
            return code;
        }
        static void Edit(EZCameraShake.CameraShaker instance)
        {
            if (flip)
                Traverse.Create(instance).Field("rotAddShake").SetValue(Traverse.Create(instance).Field("rotAddShake").GetValue<Vector3>() + new Vector3(0, 0, 180));
        }
    }

    [HarmonyPatch(typeof(Slider), "value", MethodType.Getter)]
    static class Patch_Zoom
    {
        public static bool enabled = false;
        static void Postfix(Slider __instance, ref float __result)
        {
            if (enabled && __instance == ComponentManager<Settings>.Value.graphicsBox.FOVSlider)
                __result = 20;
        }
    }

    public class Effect
    {
        public string id;
        Action<float> execute;
        Action stop;
        string reverse;
        Func<bool> ready;
        public bool networked { get; private set; }
        public Effect(string ID, Action<float> OnRun, Func<bool> IsReady, bool WaitForNetwork, Action OnStop = null, string reverseEffect = "")
        {
            id = ID;
            execute = OnRun;
            stop = OnStop;
            ready = IsReady;
            networked = WaitForNetwork;
            reverse = reverseEffect;
        }
        public bool IsReady => ready();
        public bool Execute(float duration = 0)
        {
            //Debug.Log("Event.Execute => "+id);
            execute(duration);
            return networked && !Raft_Network.IsHost;
        }
        public TimedEffect GetTimed(int Id, float duration)
        {
            var e = Main.events[reverse];
            //Debug.Log($"Timed event requested.\nReverse id is \"{reverse}\" got {e?.id}\nHas stop action: {stop != null}");
            if (e != null || stop != null)
                return new TimedEffect(Id, e, stop, duration);
            return null;
        }
    }

    public class EffectList : List<Effect>
    {
        public Effect this[string id]
        {
            get
            {
                foreach (Effect effect in this)
                    if (effect.id == id)
                        return effect;
                return null;
            }
        }

        public int IndexOf(string id)
        {
            for (int i = 0; i < this.Count; i++)
                if (this[i].id == id)
                    return i;
            return -1;
        }
    }

    public class TimedEffect
    {
        public int id;
        public float time;
        public Effect reverseEffect;
        public Action stop;
        public TimedEffect(int ID, Effect reverse, Action onStop, float duration)
        {
            id = ID;
            reverseEffect = reverse;
            stop = onStop;
            time = duration;
        }
    }

    public class AnimalEffect
    {
        public int id;
        int _animalId;
        public string name;
        public NameTag nameTag;
        public int animalId
        {
            get
            {
                return _animalId;
            }
            set
            {
                if (value == -1)
                    _animalId = -1;
                else
                {
                    foreach (AI_NetworkBehaviour entity in GameObject.FindObjectsOfType<AI_NetworkBehaviour>())
                        if (entity.ObjectIndex == value)
                            nameTag = new NameTag(entity.networkEntity, name);
                    if (nameTag == null)
                        _animalId = -1;
                    else
                        _animalId = value;
                }
            }
        }
        public AnimalEffect(int ID, string Name, int AnimalID = -1)
        {
            id = ID;
            name = Name;
            animalId = AnimalID;
        }
    }

    public class NameTag
    {
        private Canvas canvas;
        Network_Entity parent;
        GameObject container;

        public NameTag(Network_Entity creature, string name)
        {
            parent = creature;
            container = new GameObject();
            container.transform.SetParent(parent.transform, false);
            container.transform.localPosition = new Vector3(0, 2.25f, 0);
            RectTransform rect = container.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1, 1f);
            canvas = container.AddComponent<Canvas>();
            canvas.transform.SetParent(container.transform, false);
            canvas.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            RectTransform trans = canvas.GetComponent<RectTransform>();
            trans.sizeDelta = new Vector2(1, 1f);
            Text numDisplay = Main.CreateText(container.transform, 0, 0, name, 300, ComponentManager<CanvasHelper>.Value.dropText.color, 3000, 450, ComponentManager<CanvasHelper>.Value.dropText.font).GetComponent<Text>();
            numDisplay.rectTransform.sizeDelta = new Vector2(numDisplay.preferredWidth, numDisplay.preferredHeight);
            Main.CopyTextShadow(numDisplay.gameObject, ComponentManager<CanvasHelper>.Value.dropText.gameObject);
        }

        public void UpdateFacing()
        {
            Vector3 dirVec = Helper.MainCamera.transform.position - canvas.transform.position;
            float angle = Mathf.Acos(dirVec.z / dirVec.XZOnly().magnitude) / (float)Math.PI * 180f + 180;
            container.transform.rotation = Quaternion.Euler(0, dirVec.x < 0 ? -angle : angle, 0);
        }
    }

    public enum RequestType
    {
        Test = 0,
        Start = 1,
        Stop = 2
    }

    public enum EffectResult
    {
        /// <summary>The effect executed successfully.</summary>
        Success = 0,

        /// <summary>The effect failed to trigger, but is still available for use. Viewer(s) will be refunded.</summary>
        Failure = 1,

        /// <summary>Same as "Failure" but the effect is no longer available for use.</summary>
        Unavailable = 2,

        /// <summary>The effect cannot be triggered right now, try again in a few seconds.</summary>
        Retry = 3
    }

    public class AngleRange
    {
        List<Range> ranges = new List<Range>() { new Range(0, Angle.max) };
        public AngleRange(double Min, double Max)
        {
            ranges = new List<Range>() { new Range(Min, Max) };
        }
        public AngleRange() { }
        AngleRange(List<Range> Ranges)
        {
            ranges = Ranges;
        }

        public bool Empty
        {
            get
            {
                return ranges.Count == 0;
            }
        }

        public static AngleRange operator -(AngleRange a, AngleRange b)
        {
            List<Range> tmp = new List<Range>();
            foreach (Range range in b.ranges)
                tmp.AddRange(a - range);
            return new AngleRange(tmp);
        }

        public static List<Range> operator -(AngleRange a, Range b)
        {
            List<Range> tmp = new List<Range>();
            foreach (Range range in a.ranges)
                tmp.AddRange(range - b);
            return tmp;
        }

        public struct Angle
        {
            public const double max = 2 * Math.PI;
            double value;
            public Angle(double angle)
            {
                value = angle % max + (angle < 0 ? max : 0);
                if (value == 0 && angle > 0)
                    value = max;
            }

            public static implicit operator double(Angle a) => a.value;
            public static implicit operator Angle(double a) => new Angle(a);
        }
        public struct Range
        {
            Angle min;
            public Angle Size;

            public Range(double Min, double Max)
            {
                min = Min;
                Size = Max - min;
            }
            public Angle Min
            {
                get { return min; }
                set
                {
                    Size += min - value;
                    min = value;
                }
            }
            public Angle Max
            {
                get { return min + Size; }
                set
                {
                    Size = value - min;
                }
            }
            public double dMax
            {
                get { return min + Size; }
            }
            public static List<Range> operator -(Range a, Range b)
            {
                List<Range> tmp = new List<Range>();
                if (a.Size == Angle.max)
                {
                    tmp.Add(new Range(b.Max, b.min));
                    return tmp;
                }
                if (b.Size == Angle.max)
                    return tmp;
                bool flag1 = a.Contains(b.min);
                bool flag2 = a.Contains(b.Max);
                bool flag3 = b.Contains(a.min);
                bool flag4 = b.Contains(a.Max);
                if (flag3 && flag4 && flag1 == flag2)
                {
                    if (flag1)
                        tmp.Add(new Range(b.Max, b.min));
                    return tmp;
                }
                if (flag3)
                    tmp.Add(new Range(b.Max, a.Max));
                else if (flag1)
                {
                    tmp.Add(new Range(a.min, b.min));
                    if (flag2)
                        tmp.Add(new Range(b.Max, a.Max));
                }
                else
                    tmp.Add(a);
                return tmp;
            }

            public bool Contains(Angle value)
            {
                return dMax > Angle.max ? value > min || value < Max : value > min && value < dMax;
            }
        }
    }

    public class EventRequest
    {
        public CSteamID sender = new CSteamID(0L);
        public int messageId = -1;
        public string viewerName = "";
        public float duration;
        public static implicit operator EventRequest((CSteamID sender, int messageId) tuple)
            => new EventRequest() { sender = tuple.sender, messageId = tuple.messageId };
        public static implicit operator EventRequest((CSteamID sender, int messageId, string viewerName) tuple)
            => new EventRequest() { sender = tuple.sender, messageId = tuple.messageId, viewerName = tuple.viewerName };
        public static implicit operator EventRequest((CSteamID sender, int messageId, string viewerName, int duration) tuple)
            => new EventRequest() { sender = tuple.sender, messageId = tuple.messageId, viewerName = tuple.viewerName, duration = tuple.duration / 1000f };
        public static implicit operator EventRequest((int messageId, string viewerName) tuple)
            => new EventRequest() { messageId = tuple.messageId, viewerName = tuple.viewerName };
        public static implicit operator EventRequest((CSteamID sender, string viewerName) tuple)
            => new EventRequest() { sender = tuple.sender, viewerName = tuple.viewerName };
        public static implicit operator EventRequest(string viewerName)
            => new EventRequest() { viewerName = viewerName };
        public static implicit operator EventRequest((CSteamID sender, int messageId, string viewerName, float duration) tuple)
            => new EventRequest() { sender = tuple.sender, messageId = tuple.messageId, viewerName = tuple.viewerName, duration = tuple.duration };
        public static implicit operator EventRequest((CSteamID sender, int messageId, float duration) tuple)
            => new EventRequest() { sender = tuple.sender, messageId = tuple.messageId, duration = tuple.duration };
        public static implicit operator EventRequest((int messageId, string viewerName, float duration) tuple)
            => new EventRequest() { messageId = tuple.messageId, viewerName = tuple.viewerName, duration = tuple.duration };
        public static implicit operator EventRequest((CSteamID sender, int messageId, int duration) tuple)
            => new EventRequest() { sender = tuple.sender, messageId = tuple.messageId, duration = tuple.duration / 1000f };
        public static implicit operator EventRequest((int messageId, string viewerName, int duration) tuple)
            => new EventRequest() { messageId = tuple.messageId, viewerName = tuple.viewerName, duration = tuple.duration / 1000f };
    }
}