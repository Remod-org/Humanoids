#region License (GPL v3)
/*
    Loot Protection - Prevent access to player containers
    Copyright (c) 2020 RFC1920 <desolationoutpostpve@gmail.com>

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/
#endregion License (GPL v3)
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Humanoids", "RFC1920", "1.0.7")]
    [Description("Adds interactive NPCs which can be modded by other plugins")]
    class Humanoids : RustPlugin
    {
        #region vars
        [PluginReference]
        private readonly Plugin Kits, RoadFinder;
//        private static readonly PathFinding PathFinding;

        private DynamicConfigFile data;
        private ConfigData configData;
        public static Dictionary<string, AmmoTypes> ammoTypes = new Dictionary<string, AmmoTypes>();
        private List<ulong> isopen = new List<ulong>();

        private const string permNPCGuiUse = "humanoid.use";
        const string NPCGUI = "npc.editor";
        const string NPCGUK = "npc.kitselect";
        const string NPCGUL = "npc.locoselect";
        const string NPCGUN = "npc.kitsetnum";
        const string NPCGUS = "npc.select";
        const string NPCGUV = "npc.setval";

        public static Humanoids Instance = null;
        private Dictionary<ulong, HumanoidInfo> npcs = new Dictionary<ulong, HumanoidInfo>();

        private static Dictionary<string, Road> roads = new Dictionary<string, Road>();
        private static SortedDictionary<string, Vector3> monPos = new SortedDictionary<string, Vector3>();
        private SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
        private SortedDictionary<string, Vector3> cavePos = new SortedDictionary<string, Vector3>();

        private static Vector3 Vector3Down;
        static int playerMask = LayerMask.GetMask("Player (Server)");
        private static int groundLayer = LayerMask.GetMask("Construction", "Terrain", "World");
        static int obstructionMask = LayerMask.GetMask(new[] { "Construction", "Deployed", "Clutter" });
        static int terrainMask = LayerMask.GetMask(new[] { "Terrain", "Tree" });
        #endregion

        #region Message
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        private void Message(IPlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));
        #endregion

        private void DoLog(string message)
        {
            if (configData.Options.debug) Interface.Oxide.LogInfo(message);
        }

        #region global
        private void OnServerInitialized()
        {
            LoadConfigVariables();
            AddCovalenceCommand("npc", "cmdNPC");

            Instance = this;

            //var lhobjects = UnityEngine.Object.FindObjectsOfType(typeof(BasePlayer));
            //foreach(var lo in lhobjects)
            //{
            //    if((lo as BasePlayer).displayName.Contains("RFC")) continue;
            //    UnityEngine.Object.Destroy(lo);
            //}
            LoadData();
            var tmpnpcs = new Dictionary<ulong, HumanoidInfo>(npcs);
            foreach (KeyValuePair<ulong, HumanoidInfo> npc in tmpnpcs)
            {
                if (npc.Value.npcid == 0) continue;
                DoLog($"Spawning npc {npc.Value.npcid}");
                ulong nid = 0;
                SpawnNPC(npc.Value, out nid);
            }
            tmpnpcs.Clear();
            SaveData();

            FindMonuments();

            if (RoadFinder)
            {
                object x = RoadFinder.CallHook("GetRoads");
                var json = JsonConvert.SerializeObject(x);
                roads = JsonConvert.DeserializeObject<Dictionary<string, Road>>(json);
            }
        }

        void OnNewSave()
        {
            if (!configData.Options.zeroOnWipe) return;
            var HumanoidObjs = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (var obj in HumanoidObjs)
            {
                obj.info.loc = Vector3.zero;
            }
        }

        private void Unload()
        {
            //ServerMgr.Instance.StopAllCoroutines();
            var HumanoidObjs = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach(var obj in HumanoidObjs)
            {
                DoLog($"Deleting {obj.info.displayName}:{obj.info.npcid}");
                obj.movement.moving = false;
                obj.player.Kill();
            }
            foreach (var player in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(player, NPCGUI);
                CuiHelper.DestroyUi(player, NPCGUK);
                CuiHelper.DestroyUi(player, NPCGUL);
                CuiHelper.DestroyUi(player, NPCGUN);
                CuiHelper.DestroyUi(player, NPCGUS);
                CuiHelper.DestroyUi(player, NPCGUV);
                if(isopen.Contains(player.userID)) isopen.Remove(player.userID);
            }

//            SaveData();
        }

        private void LoadData()
        {
            DoLog("LoadData called");
            data = Interface.Oxide.DataFileSystem.GetFile(Name + "/humanoids");
            data.Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            data.Settings.Converters = new JsonConverter[] { new UnityQuaternionConverter(), new UnityVector3Converter() };

            try
            {
                npcs = data.ReadObject<Dictionary<ulong, HumanoidInfo>>();
            }
            catch
            {
                npcs = new Dictionary<ulong, HumanoidInfo>();
            }
            data.Clear();
            foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
            {
               DoLog($"{pls.Value.npcid.ToString()}");
            }
        }
        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/humanoids", npcs);
            //Puts("SAVE DATA START");
            //data.WriteObject(npcs);
            //Puts("SAVE DATA END");
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["npcgui"] = "Humanoid GUI",
                ["npcguisel"] = "HumanoidGUI NPC Select ",
                ["npcguikit"] = "HumanoidGUI Kit Select",
                ["npcguiloco"] = "HumanoidGUI LocoMode Select",
                ["close"] = "Close",
                ["none"] = "None",
                ["needselect"] = "Select NPC",
                ["select"] = "Select",
                ["editing"] = "Editing",
                ["mustselect"] = "Please press 'Select' to choose an NPC.",
                ["guihelp1"] = "For blue buttons, click to toggle true/false.",
                ["guihelp2"] = "For all values above in gray, you may type a new value and press enter.",
                ["guihelp3"] = "For kit, press the button to select a kit.",
                ["add"] = "Add",
                ["new"] = "Create New",
                ["remove"] = "Remove",
                ["spawnhere"] = "Spawn Here",
                ["tpto"] = "Teleport to NPC",
                ["name"] = "Name",
                ["online"] = "Online",
                ["offline"] = "Offline",
                ["deauthall"] = "DeAuthAll",
                ["remove"] = "Remove"
            }, this);
        }
        #endregion

        object RaycastAll<T> (Ray ray) where T : BaseEntity
        {
            var hits = Physics.RaycastAll (ray);
            GamePhysics.Sort (hits);
            var distance = 100f;
            object target = false;
            foreach (var hit in hits) {
                var ent = hit.GetEntity ();
                if (ent is T && hit.distance < distance) {
                    target = ent;
                    break;
                }
            }

            return target;
        }

        #region Oxide Hooks
        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if(player == null || input == null) return;
//            if(input.current.buttons > 0)
//                DoLog($"OnPlayerInput: {input.current.buttons}");
            if(!input.WasJustPressed(BUTTON.USE)) return;

            List<BasePlayer> hps = new List<BasePlayer>();
            Vis.Entities(player.transform.position, 2.5f, hps);
            foreach (var pl in hps)
            {
                if (pl == player) continue;
                var hp = pl.gameObject.GetComponent<HumanoidPlayer>();
                if (hp == null) return;

                if (hp.movement.sitting) hp.movement.Stand();
                if (hp.info.entrypause)
                {
                    DoLog($"Trying to pause {hp.info.displayName}");
                    hp.movement.Stop(true);
                }
                hp.LookTowards(player.transform.position, true);
                Message(player.IPlayer, hp.info.displayName);
                Interface.Oxide.CallHook("OnUseNPC", hp.player, player);
                break;
            }
        }

        // More decisions needed here on what to do (return fire, etc.)
//        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
//        {
//            var pl = hitinfo.HitEntity as BasePlayer;
//            if (pl = null) return null;
//            var hp = pl.GetComponentInParent<HumanoidPlayer>();
//            if (hp = null) return null;
//            if (hp.info.invulnerable) return true;
//
//            return null;
//        }

        private object CanLootPlayer(BasePlayer target, BasePlayer player)
        {
            if (player == null || target == null) return null;
            List<BaseEntity> pls = new List<BaseEntity>();
            Vis.Entities(player.transform.position, 3f, pls);
            foreach (var pl in pls)
            {
                var hp = pl.GetComponentInParent<HumanoidPlayer>();
                if (hp != null && hp.info.lootable)
                {
                    DoLog($"Player {player.displayName}:{player.UserIDString} looting NPC {hp.info.displayName}");
                    NextTick(player.EndLooting);
                    return null;
                }
            }
            return true;
        }

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            if (player == null || corpse == null) return null;
            List<BaseEntity> pls = new List<BaseEntity>();
            Vis.Entities(player.transform.position, 3f, pls);
            foreach (var pl in pls)
            {
                DoLog($"Player {player.displayName}:{player.UserIDString} looting NPC {corpse.name}:{corpse.playerSteamID.ToString()}");
                var hp = pl.GetComponentInParent<HumanoidPlayer>();
                if (hp == null) return true;
                if (hp.info.lootable)
                {
                    NextTick(player.EndLooting);
                    return null;
                }
            }

            return true;
        }
        private object OnUserCommand(BasePlayer player, string command, string[] args)
        {
            if (command != "npc" && isopen.Contains(player.userID)) return true;
            return null;
        }

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (command != "npc" && isopen.Contains(player.userID)) return true;
            return null;
        }
        #endregion

        #region commands
        [Command("npc")]
        void cmdNPC(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (args.Length > 0)
            {
                string debug = string.Join(",", args); DoLog($"{debug}");

                switch (args[0])
                {
                    case "show":
                        foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
                        {
                            (iplayer.Object as BasePlayer).SendConsoleCommand("ddraw.text", 30, Color.green, pls.Value.loc + new Vector3(0, 1.55f, 0), $"<size=20>{pls.Value.displayName}</size>");
                            (iplayer.Object as BasePlayer).SendConsoleCommand("ddraw.text", 30, Color.green, pls.Value.loc + new Vector3(0, 1.5f, 0), $"<size=20>{pls.Value.loc.ToString()}</size>");
                        }
                        break;
                    case "gui":
                    case "select":
                        CuiHelper.DestroyUi(player, NPCGUI);
                        CuiHelper.DestroyUi(player, NPCGUS);
                        NPCSelectGUI(player);
                        break;
                    case "selclose":
                        CuiHelper.DestroyUi(player, NPCGUS);
                        break;
                    case "list":
                        foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
                        {
                            var ns = pls.Value.displayName + "(" + pls.Key.ToString() + ")";
                            Message(iplayer, ns);
                        }
                        break;
                    case "spawn":
                    case "create":
                    case "new":
                        var npc = new HumanoidInfo(0, player.transform.position, player.transform.rotation);
                        ulong x = 0;
                        SpawnNPC(npc, out x);
                        break;
                    case "edit":
                        {
                            ulong npcid = 0;
                            if (args.Length > 1)
                            {
                                try
                                {
                                    npcid = ulong.Parse(args[1]);
                                }
                                catch { }
                                if(npcid > 0)
                                {
                                    NpcEditGUI(player, npcid);
                                    break;
                                }
                                else
                                {
                                    foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
                                    {
                                        DoLog($"Checking match to {pls.Value.displayName}");
                                        if (pls.Value.displayName.ToLower() == args[1].ToLower())
                                        {
                                            NpcEditGUI(player, pls.Value.npcid);
                                            break;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                DoLog("Looking for npc...");
                                RaycastHit hit;
                                if (Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, playerMask))
                                {
                                    var pl = hit.GetEntity().ToPlayer();
                                    foreach (KeyValuePair<ulong, HumanoidInfo> Npc in npcs)
                                    {
                                        if (Npc.Value.userid == pl.userID)
                                        {
                                            NpcEditGUI(player, Npc.Key);
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    case "delete":
                    case "remove":
                        {
                            if (args.Length > 1)
                            {
                                ulong npcid = ulong.Parse(args[1]);
                                foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
                                {
                                    if (pls.Value.displayName == args[1])
                                    {
                                        RemoveNPC(pls.Value);
                                        break;
                                    }
                                    else if(npcid == pls.Value.npcid)
                                    {
                                        RemoveNPC(pls.Value);
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                RaycastHit hit;
                                if (Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, playerMask))
                                {
                                    var pl = hit.GetEntity().ToPlayer();
                                    if (pl.userID > 0)
                                    {
                                        var isnpc = npcs[pl.userID];
                                        if (isnpc != null)
                                        {
                                            RemoveNPC(isnpc);
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        break;
                    case "npcselkit":
                        if(args.Length > 2)
                        {
                            NPCKitGUI(player, ulong.Parse(args[1]), args[2]);
                        }
                        break;
                    case "npcselloco":
                        if(args.Length > 1)
                        {
                            NPCLocoGUI(player, ulong.Parse(args[1]));
                        }
                        break;
                    case "kitsel":
                        if(args.Length > 2)
                        {
                            CuiHelper.DestroyUi(player, NPCGUK);
                            var userid = ulong.Parse(args[1]);
                            npc = npcs[userid];
                            var kitname = args[2];
                            npc.kit = kitname;
                            var hp = FindHumanoidByID(userid);
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, userid);
                        }
                        break;
                    case "locomode":
                        if(args.Length > 2)
                        {
                            CuiHelper.DestroyUi(player, NPCGUL);
                            var userid = ulong.Parse(args[1]);
                            npc = npcs[userid];
                            LocoMode locomode;
                            Enum.TryParse(args[2], out locomode);
                            npc.locomode = locomode;
                            var hp = FindHumanoidByID(userid);
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, userid);
                        }
                        break;
                    case "spawnhere":
                        if(args.Length > 1)
                        {
                            ulong npcid = ulong.Parse(args[1]);
                            string newSpawn = player.transform.position.x.ToString() + "," + player.transform.position.y + "," + player.transform.position.z.ToString();
                            Vector3 ns = player.transform.position;
                            Quaternion newRot;
                            TryGetPlayerView(player, out newRot);

                            var hp = FindHumanoidByID(npcid);
                            SetHumanoidInfo(npcid, "spawn", newSpawn);
                            //Teleport(hp.player, ns);
                            //hp.player.EndSleeping();
                            //npcs[npcid].loc = ns;
                            npcs[npcid].rot = newRot;
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, npcid);
                        }
                        break;
                    case "tpto":
                        if(args.Length > 1)
                        {
                            ulong npcid = ulong.Parse(args[1]);
                            CuiHelper.DestroyUi(player, NPCGUI);
                            Teleport(player, npcs[npcid].loc);
                        }
                        break;
                    case "npctoggle":
                        if(args.Length > 3)
                        {
                            var userid = ulong.Parse(args[1]);
                            string toset = args[2];
                            string newval = args[3] == "True" ? "false" : "true";
                            SetHumanoidInfo(userid, args[2], args[3]);
                            NpcEditGUI(player, userid);
                            SaveData();
                        }
                        break;
                    case "npcset":
                        if(args.Length > 1)
                        {
                            var userid = ulong.Parse(args[1]);
                            SetHumanoidInfo(userid, args[2], args[4]);
                        }
                        break;
                    case "selkitclose":
                        CuiHelper.DestroyUi(player, NPCGUK);
                        break;
                    case "sellococlose":
                        CuiHelper.DestroyUi(player, NPCGUL);
                        break;
                    case "close":
                        IsOpen(player.userID, false);
                        CuiHelper.DestroyUi(player, NPCGUS);
                        CuiHelper.DestroyUi(player, NPCGUI);
                        CuiHelper.DestroyUi(player, NPCGUK);
                        break;
                }
            }
        }

        private static bool GetBoolValue(string value)
        {
            if(value == null) return false;
            value = value.Trim().ToLower();
            switch(value)
            {
                case "t":
                case "true":
                case "1":
                case "yes":
                case "y":
                case "on":
                    return true;
                case "false":
                case "0":
                case "n":
                case "no":
                case "off":
                default:
                    return false;
            }
        }

        public static Quaternion StringToQuaternion(string sQuaternion)
        {
            // Remove the parentheses
            if(sQuaternion.StartsWith("(") && sQuaternion.EndsWith(")"))
            {
                sQuaternion = sQuaternion.Substring(1, sQuaternion.Length - 2);
            }

            // split the items
            string[] sArray = sQuaternion.Split(',');

            // store as a Vector3
            Quaternion result = new Quaternion(
                float.Parse(sArray[0]),
                float.Parse(sArray[1]),
                float.Parse(sArray[2]),
                float.Parse(sArray[3])
            );

            return result;
        }
        #endregion

        #region Our Inbound Hooks
        private object IsHumanoid(BasePlayer player)
        {
            return player.GetComponentInParent<HumanoidPlayer>() != null;
        }
        private ulong SpawnHumanoid(Vector3 position, Quaternion currentRot, string name = "NPC", ulong clone = 0)
        {
            foreach(KeyValuePair<ulong, HumanoidInfo> pair in npcs)
            {
                if(pair.Value.displayName == name && clone == 0) return pair.Key;
            }
            //public HumanoidInfo(ulong uid, Vector3 position, Quaternion rotation)
            ulong npcid = 0;
            HumanoidInfo hi = new HumanoidInfo(npcid, position, currentRot);
            hi.displayName = name;
            SpawnNPC(hi, out npcid);
            return npcid;
        }
        private string GetHumanoidName(ulong npcid)
        {
            var hp = FindHumanoidByID(npcid);
            if(hp == null) return null;
            return hp.info.displayName;
        }
        private bool RemoveHumanoidById(ulong npcid)
        {
            DoLog($"RemoveHumanoidById called for {npcid.ToString()}");
            var hp = FindHumanoidByID(npcid);
            if (hp == null) return false;
            var npc = hp.GetComponentInParent<BasePlayer>();
            if (npc == null) return false;
            KillNpc(npc);
            npcs.Remove(hp.info.npcid);
            SaveData();
            return true;
        }
        private bool RemoveHumanoidByName(string name)
        {
            DoLog($"RemoveHumanoidByName called for {name}");
            var hp = FindHumanoidByName(name);
            if (hp == null) return false;
            var npc = hp.GetComponentInParent<BasePlayer>();
            if (npc == null) return false;
            KillNpc(npc);
            npcs.Remove(hp.info.npcid);
            SaveData();
            return true;
        }

        private void SetHumanoidInfo(ulong npcid, string toset, string data, string rot = null)
        {
            DoLog($"SetHumanoidInfo called for {npcid.ToString()} {toset},{data}");
            var hp = FindHumanoidByID(npcid);
            if(hp == null) return;

            switch(toset)
            {
                case "kit":
                    hp.info.kit = data;
                    break;
                case "band":
                    hp.info.band = Convert.ToInt32(data);
                    break;
                case "entrypausetime":
                    hp.info.entrypausetime = Convert.ToInt32(data);
                    break;
                case "name":
                case "displayName":
                    hp.info.displayName = data;
                    break;
                case "invulnerable":
                case "invulnerability":
                    hp.info.invulnerable = !GetBoolValue(data);
                    break;
                case "lootable":
                    hp.info.lootable = !GetBoolValue(data);
                    break;
                case "entrypause":
                    hp.info.entrypause = !GetBoolValue(data);
                    break;
                case "hostile":
                    hp.info.hostile = !GetBoolValue(data);
                    break;
                case "defend":
                    hp.info.defend = !GetBoolValue(data);
                    hp.info.canmove = hp.info.defend;
                    break;
                case "evade":
                    hp.info.evade = !GetBoolValue(data);
                    hp.info.canmove = hp.info.evade;
                    break;
                case "follow":
                    hp.info.follow = !GetBoolValue(data);
                    hp.info.canmove = hp.info.follow;
                    break;
                case "canmove":
                    hp.info.canmove = !GetBoolValue(data);
                    break;
                case "cansit":
                    hp.info.cansit = !GetBoolValue(data);
                    hp.info.canmove = hp.info.cansit;
                    hp.info.locomode = LocoMode.Sit;
                    break;
                case "canride":
                    hp.info.canride = !GetBoolValue(data);
                    hp.info.canmove = hp.info.canride;
                    hp.info.locomode = LocoMode.Ride;
                    break;
                case "needsammo":
                case "needsAmmo":
                    hp.info.needsammo = !GetBoolValue(data);
                    break;
                case "attackdistance":
                    hp.info.attackDistance = Convert.ToSingle(data);
                    break;
                case "maxdistance":
                    hp.info.maxDistance = Convert.ToSingle(data);
                    break;
                case "damagedistance":
                    hp.info.damageDistance = Convert.ToSingle(data);
                    break;
                case "locomode":
                    switch (Convert.ToInt32(data))
                    {
                        case 0:
                        default:
                            hp.info.locomode = LocoMode.Default;
                            break;
                        case 1:
                            hp.info.locomode = LocoMode.Follow;
                            break;
                        case 2:
                            hp.info.locomode = LocoMode.Sit;
                            break;
                        case 4:
                            hp.info.locomode = LocoMode.Stand;
                            break;
                        case 8:
                            hp.info.locomode = LocoMode.Road;
                            break;
                        case 16:
                            hp.info.locomode = LocoMode.Ride;
                            break;
                        case 32:
                            hp.info.locomode = LocoMode.Monument;
                            break;
                    }
                    break;
                case "speed":
                    hp.info.speed = Convert.ToSingle(data);
                    break;
                case "spawn":
                case "loc":
                    hp.info.loc = StringToVector3(data);
                    break;
                case "rot":
                    break;
            }
            Puts("Saving Data");
            SaveData();
            Puts("Respawning");
            RespawnNPC(hp.player);
        }
        private void GiveHumanoid(ulong npcid, string itemname, string loc = "wear")
        {
            Item item = ItemManager.CreateByName(itemname, 1, 0);
            var npc = FindHumanoidByID(npcid);
            DoLog($"GiveHumanoid called: {npc.info.displayName}, {itemname}, {loc}");
            if (npc.player != null)
            {
                switch (loc)
                {
                    case "belt":
                        item.MoveToContainer(npc.player.inventory.containerBelt, -1, true);
                        if (item.info.category == ItemCategory.Weapon) npc.EquipFirstWeapon();
                        if (item.info.category == ItemCategory.Fun) npc.EquipFirstInstrument();
                        break;
                    case "wear":
                    default:
                        item.MoveToContainer(npc.player.inventory.containerWear, -1, true);
                        break;
                }
                npc.player.inventory.ServerUpdate(0f);
            }
        }
        private object npcPlayNote(ulong npcid, int band, int note, int sharp, int octave, float noteval, float duration = 0.2f)
        {
            if (npcs.ContainsKey(npcid))
            {
                var hp = FindHumanoidByID(npcid);
                if(hp.info.band != band) return false;
                hp.PlayNote(note, sharp, octave, noteval, duration);
            }
            return null;
        }
        #endregion

        #region GUI
        private void IsOpen(ulong uid, bool set=false)
        {
            if(set)
            {
#if DEBUG
                Puts($"Setting isopen for {uid}");
#endif
                if(!isopen.Contains(uid)) isopen.Add(uid);
                return;
            }
#if DEBUG
            Puts($"Clearing isopen for {uid}");
#endif
            isopen.Remove(uid);
        }

        void NpcEditGUI(BasePlayer player, ulong npc = 0)
        {
            if(player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUI);

            string npcname = Lang("needselect");
            if(npc > 0)
            {
                npcname = Lang("editing") + " " + npcs[npc].displayName;
            }

            CuiElementContainer container = UI.Container(NPCGUI, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            if (npc == 0)
            {
                UI.Button(ref container, NPCGUI, UI.Color("#cc3333", 1f), Lang("new"), 12, "0.79 0.95", "0.85 0.98", $"npc new");
            }
            else
            {
                UI.Button(ref container, NPCGUI, UI.Color("#cc3333", 1f), Lang("remove"), 12, "0.79 0.95", "0.85 0.98", $"npc remove {npc.ToString()}");
            }
            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("select"), 12, "0.86 0.95", "0.92 0.98", $"npc select");
            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"npc close");
            UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("npc") + ": " + npcname, 24, "0.2 0.92", "0.7 1");

            int col = 0;
            int row = 0;

            if(npc == 0)
            {
                UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("mustselect"), 24, "0.2 0.47", "0.7 0.53");
            }
            else
            {
                Dictionary<string, string> npcinfo = new Dictionary<string, string>
                {
                    { "displayName", npcs[npc].displayName },
                    { "kit", npcs[npc].kit },
                    { "loc", npcs[npc].loc.ToString() },
                    { "invulnerable", npcs[npc].invulnerable.ToString() },
                    { "lootable", npcs[npc].lootable.ToString() },
                    { "hostile", npcs[npc].hostile.ToString() },
                    { "defend", npcs[npc].defend.ToString() },
                    { "evade", npcs[npc].evade.ToString() },
                    { "follow", npcs[npc].follow.ToString() },
                    { "followtime",  npcs[npc].followTime.ToString() },
                    { "canmove",  npcs[npc].canmove.ToString() },
                    { "cansit",  npcs[npc].cansit.ToString() },
                    { "canride",  npcs[npc].canride.ToString() },
                    { "needsAmmo",  npcs[npc].needsammo.ToString() },
                    { "entrypause",  npcs[npc].entrypause.ToString() },
                    { "entrypausetime",  npcs[npc].entrypausetime.ToString() },
                    { "attackDistance",  npcs[npc].attackDistance.ToString() },
                    { "maxDistance",  npcs[npc].maxDistance.ToString() },
                    { "damageDistance",  npcs[npc].damageDistance.ToString() },
                    { "locomode",  npcs[npc].locomode.ToString() },
                    { "speed",  npcs[npc].speed.ToString() },
                    { "band",  npcs[npc].band.ToString() }
                };
                Dictionary<string, bool> isBool = new Dictionary<string, bool>
                {
                    { "enable", true },
                    { "invulnerable", true },
                    { "lootable", true },
                    { "hostile", true },
                    { "defend", true },
                    { "evade", true },
                    { "follow", true },
                    { "canmove", true },
                    { "cansit", true },
                    { "canride", true },
                    { "needsAmmo", true },
                    { "dropWeapon", true },
                    { "entrypause", true },
                    { "raiseAlarm", true }
                };
                Dictionary<string, bool> isLarge = new Dictionary<string, bool>
                {
                    { "hello", true },
                    { "bye", true },
                    { "hurt", true },
                    { "use", true },
                    { "kill", true }
                };

                foreach(KeyValuePair<string,string> info in npcinfo)
                {
                    if(row > 11)
                    {
                        row = 0;
                        col++;
                        col++;
                    }
                    float[] posl = GetButtonPositionP(row, col);
                    float[] posb = GetButtonPositionP(row, col + 1);

                    if(!isLarge.ContainsKey(info.Key))
                    {
                        UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), info.Key, 12, $"{posl[0]} {posl[1]}", $"{posl[0] + ((posl[2] - posl[0]) / 2)} {posl[3]}");
                    }
                    if(info.Key == "kit")
                    {
                        if(plugins.Exists("Kits"))
                        {
                            string kitname = info.Value != null ? info.Value : Lang("none");
                            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), kitname, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc npcselkit {npc.ToString()} {kitname}");
                        }
                        else
                        {
                            UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("none"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        }
                    }
                    else if(info.Key == "locomode")
                    {
                        string locomode = info.Value != null ? info.Value : Lang("none");
                        UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), locomode, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc npcselloco {npc.ToString()}");
                    }
                    else if(info.Key == "loc")
                    {
                        row++;
                        UI.Label(ref container, NPCGUI, UI.Color("#535353", 1f), info.Value, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        UI.Input(ref container, NPCGUI, UI.Color("#ffffff", 1f), info.Value, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc spawn {npc.ToString()} {info.Key} ");
                        posb = GetButtonPositionP(row, col + 1);
                        UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("spawnhere"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc spawnhere {npc.ToString()} ");
                        if (StringToVector3(info.Value) != Vector3.zero)
                        {
                            row++;
                            posb = GetButtonPositionP(row, col + 1);
                            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("tpto"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc tpto {npc.ToString()} ");
                        }
                    }
                    else if(isLarge.ContainsKey(info.Key))
                    {
//                        string oldval = info.Value != null ? info.Value : Lang("unset");
//                        UI.Label(ref container, NPCGUI, UI.Color("#535353", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]))} {posb[3]}");
//                        UI.Input(ref container, NPCGUI, UI.Color("#ffffff", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]))} {posb[3]}", $"npc npcset {npc.ToString()} {info.Key} {oldval} ");
                    }
                    else if(isBool.ContainsKey(info.Key))
                    {
                        UI.Button(ref container, NPCGUI, UI.Color("#222255", 1f), info.Value, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc npctoggle {npc.ToString()} {info.Key} {info.Value}");
                    }
                    else
                    {
                        string oldval = info.Value != null ? info.Value : Lang("unset");
                        UI.Label(ref container, NPCGUI, UI.Color("#535353", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        UI.Input(ref container, NPCGUI, UI.Color("#ffffff", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc npcset {npc.ToString()} {info.Key} ");
                    }
                    row++;
                }
                UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("guihelp1"), 12, "0.02 0.08", "0.9 0.11");
                UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("guihelp2"), 12, "0.02 0.04", "0.9 0.07");
                UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("guihelp3"), 12, "0.02 0", "0.9 0.03");
            }

            CuiHelper.AddUi(player, container);
        }

        void NPCMessageGUI(BasePlayer player, ulong npc, string field, string message)
        {
        }

        void NPCSelectGUI(BasePlayer player)
        {
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUS);

            string description = Lang("npcguisel");
            CuiElementContainer container = UI.Container(NPCGUS, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUS, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Label(ref container, NPCGUS, UI.Color("#22cc44", 1f), Lang("musician"), 12, "0.72 0.92", "0.77 1");
            UI.Label(ref container, NPCGUS, UI.Color("#2244cc", 1f), Lang("standard"), 12, "0.79 0.92", "0.86 1");
            UI.Button(ref container, NPCGUS, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"npc selclose");
            int col = 0;
            int row = 0;

            foreach(KeyValuePair<ulong,HumanoidInfo> npc in npcs)
            {
                if(row > 10)
                {
                    row = 0;
                    col++;
                }
                var hBand = npc.Value.band.ToString();
                if(hBand == "99") continue;
                string color = "#2244cc";
                if(hBand != "0") color = "#22cc44";

                var hName = npc.Value.displayName;
                float[] posb = GetButtonPositionP(row, col);
                UI.Button(ref container, NPCGUS, UI.Color(color, 1f), hName, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc edit {npc.Value.displayName}");
                row++;
            }
            float[] posn = GetButtonPositionP(row, col);
            UI.Button(ref container, NPCGUS, UI.Color("#cc3333", 1f), Lang("new"), 12, $"{posn[0]} {posn[1]}", $"{posn[0] + ((posn[2] - posn[0]) / 2)} {posn[3]}", $"npc new");

            CuiHelper.AddUi(player, container);
        }

        void NPCKitGUI(BasePlayer player, ulong npc, string kit)
        {
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUK);

            string description = Lang("npcguikit");
            CuiElementContainer container = UI.Container(NPCGUK, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUK, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Button(ref container, NPCGUK, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"npc selkitclose");

            int col = 0;
            int row = 0;

            List<string> kits = new List<string>();
            Kits?.CallHook("GetKitNames", kits);
            foreach(var kitinfo in kits)
            {
                if(row > 10)
                {
                    row = 0;
                    col++;
                }
                float[] posb = GetButtonPositionP(row, col);

                if(kit == null) kit = Lang("none");
                if(kitinfo == kit)
                {
                    UI.Button(ref container, NPCGUK, UI.Color("#d85540", 1f), kitinfo, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc kitsel {npc.ToString()} {kitinfo}");
                }
                else
                {
                    UI.Button(ref container, NPCGUK, UI.Color("#424242", 1f), kitinfo, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc kitsel {npc.ToString()} {kitinfo}");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        void NPCLocoGUI(BasePlayer player, ulong npc)
        {
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUL);

            string description = Lang("npcguiloco");
            CuiElementContainer container = UI.Container(NPCGUL, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUL, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Button(ref container, NPCGUL, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"npc sellococlose");

            int col = 0;
            int row = 0;

            var hp = FindHumanoidByID(npc);
            foreach (LocoMode mode in (LocoMode[]) Enum.GetValues(typeof(LocoMode)))
            {
                float[] posb = GetButtonPositionP(row, col);

                if(hp.info.locomode == mode)
                {
                    UI.Button(ref container, NPCGUL, UI.Color("#d85540", 1f), mode.ToString(), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc locomode {npc.ToString()} {mode}");
                }
                else
                {
                    UI.Button(ref container, NPCGUL, UI.Color("#424242", 1f), mode.ToString(), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"npc locomode {npc.ToString()} {mode}");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        private int RowNumber(int max, int count) => Mathf.FloorToInt(count / max);
        private float[] GetButtonPosition(int rowNumber, int columnNumber)
        {
            float offsetX = 0.05f + (0.096f * columnNumber);
            float offsetY = (0.80f - (rowNumber * 0.064f));

            return new float[] { offsetX, offsetY, offsetX + 0.196f, offsetY + 0.03f };
        }
        private float[] GetButtonPositionP(int rowNumber, int columnNumber)
        {
            float offsetX = 0.05f + (0.126f * columnNumber);
            float offsetY = (0.87f - (rowNumber * 0.064f));

            return new float[] { offsetX, offsetY, offsetX + 0.226f, offsetY + 0.03f };
        }

        private bool TryGetPlayerView(BasePlayer player, out Quaternion viewAngle)
        {
            viewAngle = new Quaternion(0f, 0f, 0f, 0f);
            if(player.serverInput?.current == null) return false;
            viewAngle = Quaternion.Euler(player.serverInput.current.aimAngles);
            return true;
        }

        public void Teleport(BasePlayer player, Vector3 position)
        {
            if(player.net?.connection != null) player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);

            player.SetParent(null, true, true);
            player.EnsureDismounted();
            player.Teleport(position);
            player.UpdateNetworkGroup();
            player.StartSleeping();
            player.SendNetworkUpdateImmediate(false);

            if(player.net?.connection != null) player.ClientRPCPlayer(null, player, "StartLoading");
        }

        private void StartSleeping(BasePlayer player)
        {
            if (player.IsSleeping()) return;
            player.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, true);
            if (!BasePlayer.sleepingPlayerList.Contains(player)) BasePlayer.sleepingPlayerList.Add(player);
            player.CancelInvoke("InventoryUpdate");
        }

        public static Vector3 StringToVector3(string sVector)
        {
            // Remove the parentheses
            if (sVector.StartsWith("(") && sVector.EndsWith(")"))
            {
                sVector = sVector.Substring(1, sVector.Length - 2);
            }

            // split the items
            string[] sArray = sVector.Split(',');

            // store as a Vector3
            Vector3 result = new Vector3(
                float.Parse(sArray[0]),
                float.Parse(sArray[1]),
                float.Parse(sArray[2]));

            return result;
        }
        #endregion

        #region utility
        public static IEnumerable<TValue> RandomValues<TKey, TValue>(IDictionary<TKey, TValue> dict)
        {
            System.Random rand = new System.Random();
            List<TValue> values = Enumerable.ToList(dict.Values);
            int size = dict.Count;
            while (true)
            {
                yield return values[rand.Next(size)];
            }
        }

        private ulong FindHumanoidByPlayerID(ulong playerid)
        {
            var allBasePlayer = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (var humanplayer in allBasePlayer)
            {
                DoLog($"Is {humanplayer.player.displayName} a Humanoid?");
                if (humanplayer.player.userID == playerid)
                {
                    return humanplayer.info.npcid;
                }
            }
            return 0;
        }
        private HumanoidPlayer FindHumanoidByID(ulong userid, bool playerid = false)
        {
            var allBasePlayer = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach(var humanplayer in allBasePlayer)
            {
                DoLog($"Is {humanplayer.player.displayName} a Humanoid?");
                if (playerid)
                {
                    if (humanplayer.player.userID != userid) continue;
                }
                else
                {
                    if (humanplayer.info.npcid != userid) continue;
                }
                return humanplayer;
            }
            return null;
        }

        public HumanoidPlayer FindHumanoidByName(string name)
        {
            var allBasePlayer = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach(var humanplayer in allBasePlayer)
            {
                if (humanplayer.info.displayName != name) continue;
                return humanplayer;
            }
            return null;
        }

        private void RemoveNPC(HumanoidInfo info)
        {
            if(npcs.ContainsKey(info.npcid))
            {
                //storedData.Humanoids.Remove(npcs[info.userid]);
                //npcs[info.userid] = null;
                npcs.Remove(info.npcid);
            }
            var npc = FindHumanoidByID(info.npcid);
            if(npc?.player != null && !npc.player.IsDestroyed)
            {
                npc.player.KillMessage();
            }
            SaveData();
        }
        public void RespawnNPC(BasePlayer player)
        {
            DoLog($"Attempting to respawn humanoid...");
            var n = FindHumanoidByID(player.userID, true);
            var info = n.info;
            if (player != null && info != null)
            {
                KillNpc(player);
                ulong x = 0;
                SpawnNPC(info, out x);
                player.EndSleeping();
            }
        }
        private void KillNpc(BasePlayer player)
        {
            var players = new List<BasePlayer>();
            Vis.Entities(player.transform.position, 0.01f, players);
            foreach (var pl in players)
            {
                pl.KillMessage();
            }
        }
        private void SpawnNPC(HumanoidInfo info, out ulong npcid)
        {
            DoLog($"Attempting to spawn new humanoid...");
            if (info.npcid == 0)
            {
                info.npcid = (ulong)UnityEngine.Random.Range(0, 2147483647);
            }
            npcid = info.npcid;

            var player = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", info.loc, info.rot).ToPlayer();
            DoLog($"Player object created...");
            var npc = player.gameObject.AddComponent<HumanoidPlayer>();
            DoLog($"Humanoid object added to player...");
            npc.SetInfo(info);

            player.Spawn();

            info.userid = player.userID;
            if (info.loc != Vector3.zero)
            {
                DoLog($"Setting location to {info.loc}");
                player.transform.position = info.loc;
            }
            UpdateInventory(npc);

            DoLog($"Spawned NPCid {info.npcid} with userid {player.UserIDString}");

            if (npcs.ContainsKey(info.npcid))
            {
                npcs[info.npcid] = npc.info;
            }
            else
            {
                npcs.Add(info.npcid, npc.info);
            }
            SaveData();
        }

        private void UpdateInventory(HumanoidPlayer hp)
        {
            DoLog("UpdateInventory called...");
            //if (hp.player.inventory == null) return;
            if (hp.info == null) return;
            hp.player.inventory.DoDestroy();
            hp.player.inventory.ServerInit(hp.player);

            if (!string.IsNullOrEmpty(hp.info.kit))
            {
                DoLog($"  Trying to give kit '{hp.info.kit}' to {hp.player.userID}");
                Kits?.Call("GiveKit", hp.player, hp.info.kit);

                if (hp.EquipFirstInstrument() == null)
                {
                    if (hp.EquipFirstWeapon() == null)
                    {
                        hp.EquipFirstTool();
                    }
                }
            }
            hp.player.SV_ClothingChanged();
            if (hp.info.protections != null)
            {
                hp.player.baseProtection.Clear();
                foreach (var protection in hp.info.protections)
                {
                    hp.player.baseProtection.Add(protection.Key, protection.Value);
                }
            }
            hp.player.inventory.ServerUpdate(0f);
        }
        #endregion

        #region classes
        public enum LocoMode
        {
            Default = 0,
            Follow = 1,
            Sit = 2,
            Stand = 4,
            Road = 8,
            Ride = 16,
            Monument = 32
        }
        public class HumanoidInfo
        {
            // Basic
            public ulong npcid;
            public ulong userid;
            public string displayName;
            public string kit;
            public Dictionary<DamageType, float> protections = new Dictionary<DamageType, float>();
            public Timer pausetimer;

            // Logic
            public bool enable = true;
            public bool canmove = false;
            public bool cansit = false;
            public bool canride = false;

            public bool hostile = false;
            public bool defend = false;
            public bool evade = false;
            public bool follow = false;
            public bool needsammo = false;
            public bool invulnerable = true;
            public bool lootable = true;
            public bool entrypause = true;

            // Location and movement
            public float speed;
            public Vector3 spawnloc;
            public Vector3 loc;
            public Quaternion rot;
            public Vector3 targetloc;
            public float maxDistance = 100f;
            public float attackDistance = 30f;
            public float damageDistance = 20f;
            public float followTime = 30f;
            public float entrypausetime = 5f;
            public string roadname;
            public string monstart;
            public string monend;
            public LocoMode locomode;
            public string waypoint;

            public int band = 0;

            public HumanoidInfo(ulong uid, Vector3 position, Quaternion rotation)
            {
                npcid = uid;
                displayName = "Noid";
                invulnerable = true;
                //health = 50;
                hostile = false;
                needsammo = true;
                //dropWeapon = true;
                //respawn = true;
                //respawnSeconds = 60;
                loc = position;
                rot = rotation;
                //collisionRadius = 10;
                damageDistance = 3;
                //damageAmount = 10;
                attackDistance = 100;
                maxDistance = 200;
                //hitchance = 0.75f;
                speed = 3;
                //stopandtalk = true;
                //stopandtalkSeconds = 3;
                enable = true;
                //persistent = true;
                lootable = true;
                entrypause = true;
                entrypausetime = 5f;
                defend = false;
                evade = false;
                //evdist = 0f;
                follow = false;
                followTime = 30f;
                cansit = false;
                canride = false;
                //damageInterval = 2;

                for(var i = 0; i < (int)DamageType.LAST; i++)
                {
                    protections[(DamageType)i] = 0f;
                }
            }
        }

        public class HumanoidMovement : MonoBehaviour
        {
            private HumanoidPlayer npc;
            public List<Vector3> Paths = new List<Vector3>();
            public Vector3 StartPos = new Vector3(0f, 0f, 0f);
            public Vector3 EndPos = new Vector3(0f, 0f, 0f);
            public Vector3 lastPos = new Vector3(0f, 0f, 0f);
            public Vector3 nextPos = new Vector3(0f, 0f, 0f);
            private Vector3 currPos = new Vector3(0f, 0f, 0f);
            private float waypointDone = 0f;
            public float elapsedTime = -1f;
            private float tripTime = 0f;

            // Real-time status
            public bool moving = false;
            public bool sitting = false;
            public bool riding = false;
            public bool swimming = false;
            public bool paused = false;

            public List<WaypointInfo> cachedWaypoints;
            private int currentWaypoint = -1;

            public float followDistance = 3.5f;
            private float lastHit = 0f;

            public int noPath = 0;
            public bool shouldMove = true;

            private float startedReload = 0f;
            private float startedFollow = 0f;

            private Collider collider;

            public BaseCombatEntity attackEntity = null;
            public BaseEntity followEntity = null;
            public Vector3 targetPosition = Vector3.zero;

            public List<Vector3> pathFinding;

            private HeldEntity firstWeapon = null;
            public bool startmoving = true;

            public float wpupdatetime = 0;

            public void Awake()
            {
                npc = GetComponent<HumanoidPlayer>();
                //UpdateWaypoints();
                StartPos = npc.info.loc;
                npc.player.transform.rotation = npc.info.rot;
                npc.player.modelState.onground = true;
            }

            public void FixedUpdate()
            {
                if(npc.player.IsWounded() || npc.player.IsDead())
                {
                    Stop();
                }
                else
                {
                    DetermineMove(); // based on locomode
                    Move();
                }
            }

            public void Move()
            {
                if (elapsedTime == 0f && startmoving)
                {
                    startmoving = false;
                    //FindNextWaypoint();
                }
                Execute_Move();
                //if (waypointDone >= 1f) elapsedTime = 0f;
            }

            private void FindNextWaypoint()
            {
                if (paused) return;
                if (Time.realtimeSinceStartup - wpupdatetime < 1 / npc.info.speed) return;
                wpupdatetime = Time.realtimeSinceStartup;
//                if (currentWaypoint == -1 && npc.info.locomode == LocoMode.Road)
//                {
//                    Instance.DoLog("ROADWALKER BUMP");
//                    currentWaypoint++;
//                }
                currentWaypoint++;
                Instance.DoLog($"FindNextWaypoint({currentWaypoint.ToString()}), Paths.Count == {Paths.Count.ToString()}, time: {wpupdatetime}");
                if (Paths.Count == 0 || currentWaypoint >= Paths.Count)
                {
                    // At the end, walk the path the other way
                    if(Paths.Count > 0)
                    {
                        Paths.Reverse();
                        StartPos = Paths[0];
                        currentWaypoint = 0;
                    }
//                    StartPos = EndPos = Vector3.zero;
//                    enable = false;
//                    return;
                }

                SetMovementPoint(Paths[currentWaypoint], npc.info.speed);
            }

            public void SetMovementPoint(Vector3 endpos, float s)
            {
                StartPos = npc.info.loc;
                if (endpos != StartPos)
                {
                    EndPos = endpos;
                    tripTime = Vector3.Distance(EndPos, StartPos)/s;
                    //npc.info.rot = Quaternion.LookRotation(EndPos - StartPos);
                    //if (npc.player != null) SetViewAngle(npc.player, npc.info.rot);
                    elapsedTime = 0f;
                    waypointDone = 0f;
                }
                //Paths.RemoveAt(0);
                var d = Vector3.Distance(EndPos, StartPos);
                var ts = Time.realtimeSinceStartup;
//                Instance.DoLog($"SetMovementPoint({currentWaypoint.ToString()}) Start: {StartPos.ToString()}, current {npc.info.loc.ToString()}, End: {endpos.ToString()}), time: {ts}");
            }

            private void Execute_Move()
            {
                if (!npc.info.canmove) return;
                if (!npc.info.enable) return;

                currPos = Vector3.Lerp(StartPos, EndPos, waypointDone);
                //currPos.y = GetMoveY(currPos); // Adjust for terrain height

                elapsedTime += Time.deltaTime;
                waypointDone = Mathf.InverseLerp(0f, tripTime, elapsedTime);
                float dte = FlatDistance(currPos, EndPos);

                if (dte == 0 && waypointDone >= 1)// && dfs > 0)
                {
                    FindNextWaypoint();
                    return;
                }

                //entity.transform.position = currPos;
                npc.player.transform.position = currPos;
                npc.info.loc = currPos;
                //Instance.DoLog($"Current location: {npc.info.loc}");
                //Instance.DoLog($"Execute_Move from {StartPos.ToString()} to {EndPos.ToString()} within {tripTime.ToString()}s\n\tcurrent: {npc.player.transform.position.ToString()}, next: {currPos.ToString()}, elapsed: {elapsedTime.ToString()} ");

                npc.LookTowards(EndPos);
                npc.player.MovePosition(currPos);
                //npc.player.SendNetworkUpdate();
                var newEyesPos = currPos + new Vector3(0, 1.6f, 0);
                npc.player.eyes.position.Set(newEyesPos.x, newEyesPos.y, newEyesPos.z);
                npc.player.EnablePlayerCollider();

                //npc.player.modelState.onground = !IsSwimming();
            }

            public static float FlatDistance(Vector3 pos1, Vector3 pos2)
            {
                pos1.y = 0;// pos1.z;
                pos2.y = 0;// pos2.z;
                return Vector2.Distance(pos1, pos2);
            }

            public void SetViewAngle(BasePlayer player, Quaternion ViewAngles)
            {
                if (player == null) return;
                player.viewAngles = ViewAngles.eulerAngles;
                player.SendNetworkUpdate();
            }

            public void Enable()
            {
                //if(GetSpeed() <= 0) return;
                npc.info.enable = true;
            }
            public void Disable()
            {
                npc.info.enable = false;
            }

            public bool IsSitting()
            {
                if (sitting == true)
                {
                    Instance.DoLog($"{npc.info.displayName} is sitting.");
                    return true;
                }
                if (npc.player.isMounted)
                {
                    Instance.DoLog($"{npc.info.displayName} is mounted.");
                    return true;
                }
                else
                {
                    Instance.DoLog($"{npc.info.displayName} is not sitting.");
                }

                return false;
            }
            public void Stand()
            {
                if (sitting)
                {
                    var mounted = npc.player.GetMounted();
                    mounted.DismountPlayer(npc.player);
                    mounted.SetFlag(BaseEntity.Flags.Busy, false, false);
                    sitting = false;
                }
            }
            public void Sit()
            {
                if (sitting) return;
                if (!npc.info.cansit) return;
                //Instance.DoLog($"[HumanoidMovement] {npc.player.displayName} wants to sit...");
                // Find a place to sit
                List<BaseChair> chairs = new List<BaseChair>();
                List<StaticInstrument> pidrxy = new List<StaticInstrument>();
                Vis.Entities(npc.info.loc, 10f, chairs);

                foreach(var mountable in chairs.Distinct().ToList())
                {
                    Instance.DoLog($"[HumanoidMovement] {npc.player.displayName} trying to sit in chair...");
                    if(mountable.IsMounted())
                    {
                        Instance.DoLog($"[HumanoidMovement] Someone is sitting here.");
                        continue;
                    }
                    Instance.DoLog($"[HumanoidMovement] Found an empty chair.");
                    mountable.MountPlayer(npc.player);
                    npc.player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
                    npc.player.eyes.NetworkUpdate(mountable.mountAnchor.transform.rotation);
                    npc.player.ClientRPCPlayer<Vector3>(null, npc.player, "ForcePositionTo", npc.player.transform.position);
                    mountable.SetFlag(BaseEntity.Flags.Busy, true, false);
                    sitting = true;
                    break;
                }

                Vis.Entities(npc.info.loc, 2f, pidrxy);
                foreach(var mountable in pidrxy.Distinct().ToList())
                {
                    Instance.DoLog($"[HumanoidMovement] {npc.player.displayName} trying to sit at instrument...");
                    if(mountable.IsMounted())
                    {
                        Instance.DoLog($"[HumanoidMovement] Someone is sitting here.");
                        continue;
                    }
                    mountable.MountPlayer(npc.player);
                    npc.player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
                    npc.player.eyes.NetworkUpdate(mountable.mountAnchor.transform.rotation);
                    npc.player.ClientRPCPlayer(null, npc.player, "ForcePositionTo", npc.player.transform.position);
                    mountable.SetFlag(BaseEntity.Flags.Busy, true, false);
                    sitting = true;
                    Instance.DoLog($"[HumanoidMovement] Setting instrument for {npc.player.displayName} to {mountable.ShortPrefabName}");
                    npc.instrument = mountable.ShortPrefabName;
                    npc.ktool = mountable;//.GetParentEntity() as StaticInstrument;
                    break;
                }
            }

            public void HandlePause(bool kill = false)
            {
                if (kill)
                {
                    Instance.DoLog("HandlePause end called!");
                    paused = false;
                    moving = true;
                    //npc.info.pausetimer.Destroy();
                    //Instance.DoLog("Timer killed!");
                    return;
                }
                else
                {
                    Instance.DoLog("HandlePause start called!");
                }
                if(npc.info.entrypausetime > 0)
                {
                    paused = true;
                    moving = false;
                    //npc.info.pausetimer = Instance.timer.Once(npc.info.entrypausetime, () => HandlePause(true));
                    Instance.timer.Once(npc.info.entrypausetime, () => HandlePause(true));
                }
            }
            public void Stop(bool pause = false)
            {
                if(pause)
                {
                    HandlePause();
                    npc.player.SignalBroadcast(BaseEntity.Signal.Gesture, "wave");
                }
                if (moving)
                {
                    Instance.DoLog($"Stop called for {npc.info.displayName}");
                    //npc.info.targetloc = Vector3.zero;
                    npc.target = null;
                    moving = false;
                }
            }

            public void Ride()
            {
                if(npc.info.canride == false) return;
                var horse = npc.player.GetMountedVehicle() as RidableHorse;
                if(horse == null)
                {
                    // Find a place to sit
                    List<RidableHorse> horses = new List<RidableHorse>();
                    Vis.Entities(npc.info.loc, 15f, horses);
                    foreach(var mountable in horses.Distinct().ToList())
                    {
                        if(mountable.GetMounted() != null)
                        {
                            continue;
                        }
                        mountable.AttemptMount(npc.player);
                        npc.player.SetParent(mountable, true, true);
                        riding = true;
                        break;
                    }
                }

                if(horse == null)
                {
                    riding = false;
                    npc.player.SetParent(null, true, true);
                    return;
                }
                Vector3 targetDir = new Vector3();
                Vector3 targetLoc = new Vector3();
                Vector3 targetHorsePos = new Vector3();
                float distance = 0f;
                bool rand = true;

                if(npc.target != null)
                {
                    distance = Vector3.Distance(npc.info.loc, npc.target.transform.position);
                    targetDir = npc.target.transform.position;
                }
                else
                {
                    distance = Vector3.Distance(npc.info.loc, npc.info.targetloc);
                    targetDir = npc.info.targetloc - horse.transform.position;
//                    rand = true;
                }

                bool hasMoved = targetDir != Vector3.zero && Vector3.Distance(horse.transform.position, npc.info.loc) > 0.5f;
                bool isVisible = npc.target != null && npc.target.IsVisible(npc.player.eyes.position, (npc.target as BasePlayer).eyes.position, 200);
                var randompos = UnityEngine.Random.insideUnitCircle * npc.info.damageDistance;
                if(npc.target != null)
                {
                    if(isVisible)
                    {
                        targetLoc = npc.target.transform.position;
                        rand = false;
                    }
                    else
                    {
                        if(Vector3.Distance(npc.info.loc, targetHorsePos) > 10 && !hasMoved)
                        {
                            npc.target = null;
                            targetLoc = new Vector3(randompos.x, 0, randompos.y);
                            targetLoc += npc.info.spawnloc;
                            targetHorsePos = targetLoc;
                        }
                        else
                        {
                            targetLoc = npc.target.transform.position;
                        }
                    }
                }
                else
                {
                    if(Vector3.Distance(npc.player.transform.position, targetHorsePos) > 10 && hasMoved)
                    {
                        targetLoc = targetHorsePos;
                    }
                    else
                    {
                        targetLoc = new Vector3(randompos.x, 0, randompos.y);
                        targetLoc += npc.player.transform.position;
                        targetHorsePos = targetLoc;
                    }
                }

                float angle = Vector3.SignedAngle(targetDir, horse.transform.forward, Vector3.up);
                //float angle = Vector3.SignedAngle(npc.player.transform.forward, targetDir, Vector3.forward);
                //float angle = Vector3.SignedAngle(targetDir, horse.transform.forward, Vector3.forward);

                InputMessage message = new InputMessage() { buttons = 0 };
                if(distance > npc.info.damageDistance)
                {
                    message.buttons = 2; // FORWARD
                }
                if(distance > 40 && !rand)
                {
                    message.buttons = 130; // SPRINT FORWARD
                }
                if(horse.currentRunState == BaseRidableAnimal.RunState.sprint && distance < npc.info.maxDistance)
                {
                    message.buttons = 0; // STOP ?
                }
                if(angle > 30 && angle < 180)
                {
                    message.buttons += 8; // LEFT
                }
                if(angle < -30 && angle > -180)
                {
                    message.buttons += 16; // RIGHT
                }

                horse.RiderInput(new InputState() { current = message }, npc.player);
            }

            public void UpdateWaypoints()
            {
                if(string.IsNullOrEmpty(npc.info.waypoint)) return;
                var cwaypoints = Interface.Oxide.CallHook("GetWaypointsList", npc.info.waypoint);
                if(cwaypoints == null) cachedWaypoints = null;
                else
                {
                    cachedWaypoints = new List<WaypointInfo>();
                    var lastPos = npc.info.loc;
                    var speed = GetSpeed();
                    foreach(var cwaypoint in (List<object>)cwaypoints)
                    {
                        foreach(var pair in (Dictionary<Vector3, float>)cwaypoint)
                        {
                            cachedWaypoints.Add(new WaypointInfo(pair.Key, pair.Value));
                        }
                    }

                    if(cachedWaypoints.Count <= 0) cachedWaypoints = null;

                    Instance.DoLog($"[HumanoidMovement] Waypoints: {cachedWaypoints.Count.ToString()} {npc.player.displayName}");
                }
            }

            public List<Item> GetAmmo(Item item)
            {
                var ammos = new List<Item>();
                AmmoTypes ammoType;
                if(!ammoTypes.TryGetValue(item.info.shortname, out ammoType)) return ammos;
                npc.player.inventory.FindAmmo(ammos, ammoType);
                return ammos;
            }

            private float GetSpeed(float speed = -1)
            {
                if(sitting) speed = 0;
//                if(returning) speed = 7;
                else if(speed == -1) speed = npc.info.speed;
                if(IsSwimming()) speed = speed / 2f;

                return speed;
            }

            private bool IsSwimming()
            {
                return WaterLevel.Test(npc.player.transform.position + new Vector3(0, 0.65f, 0));
            }

            public void FindRoad()
            {
                if (!Instance.RoadFinder) return;
                if (roads.Count == 0) return;
                // Pick a random monument...
                string roadname = null;
                if (npc.info.monstart == null)
                {
                    System.Random rand = new System.Random();
                    KeyValuePair<string, Vector3> pair = monPos.ElementAt(rand.Next(monPos.Count));
                    npc.info.monstart = pair.Key;
                    Instance.DoLog($"[HumanoidMovement] Chose monument {npc.info.monstart} at {monPos[npc.info.monstart].ToString()}");
                }
                else
                {
                    Instance.DoLog($"[HumanoidMovement] Starting at monument {npc.info.monstart} at {monPos[npc.info.monstart].ToString()}");
                }
                float distance = 10000f;
                // Find closest road start
                foreach (KeyValuePair<string, Road> road in roads)
                {
                    var currdist = Vector3.Distance(monPos[npc.info.monstart], road.Value.points[0]);
                    distance = Math.Min(distance, currdist);
                    //Instance.DoLog($"[HumanoidMovement] {road.Key} distance to {npc.info.monstart} == {currdist.ToString()}");

                    if (currdist <= distance)
                    {
                        roadname = road.Key;
                        //Instance.DoLog($"[HumanoidMovement] {road.Key} is closest");
                    }
                }

                roadname = "Road 5"; // TESTING
                int i = 0;
                foreach (var point in roads[roadname].points)
                {
                    Instance.DoLog($"point {i}: {point.ToString()}");
                    i++;
                }
                //npc.info.targetloc = roads[roadname].points[0];
                npc.info.loc = roads[roadname].points[0];
                npc.info.roadname = roadname;
                Instance.DoLog($"[HumanoidMovement] Moving {npc.info.displayName} to monument {npc.info.monstart} to walk {npc.info.roadname}");
                npc.player.MovePosition(npc.info.loc);
                moving = true;

                // Setup road points as waypoints
                Paths = roads[roadname].points;
                StartPos = Paths.First();
                EndPos = Paths[1];
                tripTime = Vector3.Distance(StartPos, EndPos) / GetSpeed(npc.info.speed);
                npc.info.enable = true;
            }

            public float GetMoveY(Vector3 position)
            {
                if(swimming)
                {
                    float point = TerrainMeta.WaterMap.GetHeight(position) - 0.65f;
                    float groundY = GetGroundY(position);
                    if(groundY > point)
                    {
                        return groundY;
                    }

                    return point - 0.65f;
                }

                float y = TerrainMeta.HeightMap.GetHeight(position);
                return y;
                //return GetGroundY(position);
            }

            public float GetGroundY(Vector3 position)
            {
                //position = position + Vector3.up;
                RaycastHit hitinfo;
                if(Physics.Raycast(position, Vector3Down, out hitinfo, 100f, groundLayer))
                {
                    return hitinfo.point.y;
                }
                Instance.DoLog($"[HumanoidMovement] GetGroundY: {position.y.ToString()}");
                return position.y - .5f;
            }

            public void FindVictim()
            {
                List<BasePlayer> victims = new List<BasePlayer>();
                Vis.Entities(npc.info.loc, 50f, victims);
                foreach (var pl in victims)
                {
                    npc.info.targetloc = pl.transform.position;
                    break;
                }
                List<BaseAnimalNPC> avictims = new List<BaseAnimalNPC>();
                Vis.Entities(npc.info.loc, 50f, avictims);
                foreach (var pl in victims)
                {
                    npc.info.targetloc = pl.transform.position;
                    break;
                }
            }

            public bool IsLayerBlocked(Vector3 position, float radius, int mask)
            {
                var colliders = Pool.GetList<Collider>();
                Vis.Colliders(position, radius, colliders, mask, QueryTriggerInteraction.Collide);

                bool blocked = colliders.Count > 0;

                Pool.FreeList(ref colliders);

                return blocked;
            }

            private bool CanSee()
            {
                var weapon = npc.player.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
                var pos = npc.info.loc + npc.player.GetOffset();
                if (weapon?.MuzzlePoint != null)
                {
                    pos += Quaternion.LookRotation(npc.info.targetloc - npc.info.loc) * weapon.MuzzlePoint.position;
                }

                if (Physics.Linecast(npc.info.loc, npc.info.targetloc, obstructionMask))
                {
                    return false;
                }
                if (Vector3.Distance(npc.info.loc, npc.info.targetloc) < 30f)//npc.info.damageDistance)
                {

                    //if (!IsLayerBlocked(info.targetloc, 10f, obstructionMask))
                    //{
                    //    //npc.Evade();
                    //}

                    npc.LookTowards(npc.info.targetloc);
                    return true;
                }
                List<BasePlayer> nearPlayers = new List<BasePlayer>();
                Vis.Entities(npc.info.loc, npc.info.maxDistance, nearPlayers, playerMask);
                foreach (var player in nearPlayers)
                {
                    //if (!IsLayerBlocked(info.targetloc, npc.info.attackDistance, obstructionMask))
                    //{
                    //    //npc.Evade();
                    //}

                    npc.LookTowards(npc.info.targetloc);
                    return true;
                }
                List<BaseAnimalNPC> nearAnimals = new List<BaseAnimalNPC>();
                Vis.Entities(npc.info.loc, npc.info.maxDistance, nearAnimals, playerMask);
                foreach (var player in nearAnimals)
                {
                    //if (!IsLayerBlocked(info.targetloc, npc.info.attackDistance, obstructionMask))
                    //{
                    //    //npc.Evade();
                    //}

                    npc.LookTowards(npc.info.targetloc);
                    return true;
                }
                return false;
            }

            public void DetermineMove()
            {
                if (!npc.info.canmove) return;
                if (paused) return;
                if (npc.player == null) return;
                //Instance.DoLog($"Determining move based on locomode of {npc.info.locomode.ToString()}");

                //                if(npc.info.band > 0)
                //                {
                //                    npc.EquipFirstInstrument();
                //                }
                //                else if(npc.info.hostile)
                //                {
                //                    npc.EquipFirstWeapon();
                //                }
                //                else
                //                {
                //                    npc.EquipFirstTool();
                //                }

                switch (npc.info.locomode)
                {
                    case LocoMode.Sit:
                        npc.info.cansit = true;
                        npc.info.canride = false;
                        npc.info.canmove = true;
                        Sit();
                        break;
                    case LocoMode.Ride:
                        npc.info.cansit = false;
                        npc.info.canride = true;
                        npc.info.canmove = true;
                        Ride();
                        break;
                    case LocoMode.Follow:
                        npc.info.cansit = false;
                        npc.info.canride = false;
                        npc.info.canmove = true;
                        break;
                    case LocoMode.Stand:
                        npc.info.cansit = false;
                        npc.info.canride = false;
                        npc.info.canmove = false;
                        Stand();
                        break;
                    case LocoMode.Road:
                        npc.info.cansit = false;
                        npc.info.canride = false;
                        npc.info.canmove = true;
                        if (!moving)
                        {
                            moving = true;
                            FindRoad();
                        }
                        break;
                    case LocoMode.Monument:
                        npc.info.cansit = false;
                        npc.info.canride = false;
                        npc.info.canmove = true;
                        break;
                    case LocoMode.Default:
                    default:
                        npc.info.cansit = false;
                        npc.info.canride = false;
                        npc.info.canmove = false;
                        break;
                }
            }
        }

        public class HumanoidPlayer : MonoBehaviour
        {
            public HumanoidInfo info;
            public HumanoidMovement movement;
            public ProtectionProperties protection;
            //public BaseEntity entity;
            public BasePlayer player;

            public BaseCombatEntity target;

            // Music
            public InstrumentTool itool;
            public StaticInstrument ktool;
            public string instrument;

            public void Awake()
            {
                Instance.DoLog("[HumanoidPlayer] Getting player object...");
                //entity = GetComponent<BaseEntity>();
                player = GetComponent<BasePlayer>();
                Instance.DoLog("[HumanoidPlayer] Adding player protection...");
                protection = ScriptableObject.CreateInstance<ProtectionProperties>();
                //Instance.DoLog("Setting player modelState...");
                player.modelState.onground = true;
            }
            public void OnDisable()
            {
                //StopAllCoroutines();
                player = null;
                itool = null;
                ktool = null;
            }

            public void SetInfo(HumanoidInfo info, bool update = false)
            {
                //Instance.DoLog($"SetInfo called for {player.UserIDString}");
                this.info = info;
                Instance.DoLog("[HumanoidPlayer] Info var set.");
                if(info == null) return;
                player.displayName = info.displayName;
                SetViewAngle(info.rot);
                Instance.DoLog("[HumanoidPlayer] view angle set.");
                player.syncPosition = true;
                //player.EnablePlayerCollider();
                if (!update)
                {
                    Instance.DoLog($"[HumanoidPlayer] Not an update...");
                    //player.xp = ServerMgr.Xp.GetAgent(info.userid);
                    Instance.DoLog($"[HumanoidPlayer]   setting stats...");
                    player.stats = new PlayerStatistics(player);
                    Instance.DoLog($"[HumanoidPlayer]   setting userid...");
                    player.userID = info.userid;
                    Instance.DoLog($"[HumanoidPlayer]   setting useridstring...");
                    player.UserIDString = player.userID.ToString();
                    Instance.DoLog($"[HumanoidPlayer]   moving...");
                    player.MovePosition(info.loc);
                    Instance.DoLog($"[HumanoidPlayer]   setting eyes...");
                    player.eyes = player.eyes ?? player.GetComponent<PlayerEyes>();
                    //player.eyes.position = info.spawnInfo.position + new Vector3(0, 1.6f, 0);
                    var newEyes = info.loc + new Vector3(0, 1.6f, 0);
                    Instance.DoLog($"[HumanoidPlayer]   setting eye position...");
                    player.eyes.position.Set(newEyes.x, newEyes.y, newEyes.z);
                    Instance.DoLog($"[HumanoidPlayer]   ending sleep...");
                    player.EndSleeping();
                    protection.Clear();
                }
                if(movement != null) Destroy(movement);
                Instance.DoLog($"[HumanoidPlayer] Adding player movement to {player.displayName}...");
                movement = player.gameObject.AddComponent<HumanoidMovement>();
                //pathfollower = player.gameObject.AddComponent<PathFollower>();
            }

            public void LookTowards(Vector3 pos, bool wave = false)
            {
                if(pos != info.loc)
                {
                    SetViewAngle(Quaternion.LookRotation(pos - info.loc));
                    if (wave) player.SignalBroadcast(BaseEntity.Signal.Gesture, "wave");
                }
            }

            public void SetViewAngle(Quaternion viewAngles)
            {
                if(viewAngles.eulerAngles == default(Vector3)) return;
                player.viewAngles = viewAngles.eulerAngles;
                info.rot = viewAngles;
                player.SendNetworkUpdate();
            }

            public HeldEntity GetFirstWeapon()
            {
                if(player.inventory?.containerBelt == null) return null;
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld() && HasAmmo(item) && (item.info.category == ItemCategory.Weapon))
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstTool()
            {
                if(player.inventory?.containerBelt == null) return null;
                foreach(Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld() && item.info.category == ItemCategory.Tool)
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstInstrument()
            {
                if(player.inventory?.containerBelt == null) return null;
                foreach (Item item in player.inventory.containerBelt.itemList)
                {
                    if(item.CanBeHeld() && item.info.category == ItemCategory.Fun)
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public void UnequipAll()
            {
                if (player.inventory?.containerBelt == null) return;
                foreach (Item item in player.inventory.containerBelt.itemList)
                {
                    if (item.CanBeHeld())
                    {
                        (item.GetHeldEntity() as HeldEntity)?.SetHeld(false);
                    }
                }
            }

            public bool HasAmmo(Item item)
            {
                if(!info.needsammo) return true;
                var weapon = item.GetHeldEntity() as BaseProjectile;
                if(weapon == null) return true;
                return weapon.primaryMagazine.contents > 0 || weapon.primaryMagazine.CanReload(player);
            }

            public void SetActive(uint id)
            {
                player.svActiveItemID = id;
                player.SendNetworkUpdate();
                player.SignalBroadcast(BaseEntity.Signal.Reload, string.Empty);
            }

            public HeldEntity EquipFirstWeapon()
            {
                HeldEntity weapon = GetFirstWeapon();
                if(weapon != null)
                {
                    UnequipAll();
                    weapon.SetHeld(true);
                }
                return weapon;
            }

            public HeldEntity EquipFirstTool()
            {
                HeldEntity tool = GetFirstTool();
                if(tool != null)
                {
                    UnequipAll();
                    tool.SetHeld(true);
                }
                return tool;
            }

            public HeldEntity EquipFirstInstrument()
            {
                HeldEntity instr = GetFirstInstrument();
                if (instr != null)
                {
                    UnequipAll();
                    instr.SetOwnerPlayer(player);
                    instr.SetVisibleWhileHolstered(true);
                    instr.SetHeld(true);
                    instr.UpdateHeldItemVisibility();
                    var item = instr.GetItem();
                    SetActive(item.uid);
                    itool = instr as InstrumentTool; // THIS ONE!
                    instrument = instr.ShortPrefabName;
                }
                return instr;
            }

            public void PlayNote(int note, int sharp, int octave, float noteval, float duration = 0.2f)
            {
                switch (instrument)
                {
                    case "drumkit.deployed.static":
                    case "drumkit.deployed":
                    case "xylophone.deployed":
                        if (ktool != null)
                        {
                            ktool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", note, sharp, octave, noteval);
                        }
                        break;
                    case "cowbell.deployed":
                        if (ktool != null)
                        {
                            ktool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", 2, 0, 0, 1);
                        }
                        break;
                    case "piano.deployed.static":
                    case "piano.deployed":
                        if (ktool != null)
                        {
                            ktool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", note, sharp, octave, noteval);
                            Instance.timer.Once(duration, () =>
                            {
                                ktool.ClientRPC<int, int, int, float>(null, "Client_StopNote", note, sharp, octave, noteval);
                            });
                        }
                        break;
                    default:
                        if (itool != null)
                        {
                            itool.ClientRPC<int, int, int, float>(null, "Client_PlayNote", note, sharp, octave, noteval);
                            Instance.timer.Once(duration, () =>
                            {
                                itool.ClientRPC<int, int, int, float>(null, "Client_StopNote", note, sharp, octave, noteval);
                            });
                        }
                        break;
                }
            }
        }

        public class WaypointInfo
        {
            public float Speed;
            public Vector3 Position;

            public WaypointInfo(Vector3 position, float speed)
            {
                Speed = speed;
                Position = position;
            }
        }

        public class Road
        {
            public List<Vector3> points = new List<Vector3>();
            public float width;
            public float offset;
            public int topo;
        }

//        private class StoredData
//        {
//            public List<HumanoidInfo> Humanoids = new List<HumanoidInfo>();
//        }

        private class UnityQuaternionConverter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var quaternion = (Quaternion)value;
                writer.WriteValue($"{quaternion.x} {quaternion.y} {quaternion.z} {quaternion.w}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if(reader.TokenType == JsonToken.String)
                {
                    var values = reader.Value.ToString().Trim().Split(' ');
                    return new Quaternion(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]), Convert.ToSingle(values[3]));
                }
                var o = JObject.Load(reader);
                return new Quaternion(Convert.ToSingle(o["rx"]), Convert.ToSingle(o["ry"]), Convert.ToSingle(o["rz"]), Convert.ToSingle(o["rw"]));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Quaternion);
            }
        }

        private class UnityVector3Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var vector =(Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if(reader.TokenType == JsonToken.String)
                {
                    var values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
                }
                var o = JObject.Load(reader);
                return new Vector3(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]), Convert.ToSingle(o["z"]));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }

        public static class UI
        {
            public static CuiElementContainer Container(string panel, string color, string min, string max, bool useCursor = false, string parent = "Overlay")
            {
                CuiElementContainer container = new CuiElementContainer()
                {
                    {
                        new CuiPanel
                        {
                            Image = { Color = color },
                            RectTransform = {AnchorMin = min, AnchorMax = max},
                            CursorEnabled = useCursor
                        },
                        new CuiElement().Parent = parent,
                        panel
                    }
                };
                return container;
            }
            public static void Panel(ref CuiElementContainer container, string panel, string color, string min, string max, bool cursor = false)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    CursorEnabled = cursor
                },
                panel);
            }
            public static void Label(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiLabel
                {
                    Text = { Color = color, FontSize = size, Align = align, Text = text },
                    RectTransform = { AnchorMin = min, AnchorMax = max }
                },
                panel);

            }
            public static void Button(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = command, FadeIn = 0f },
                    RectTransform = { AnchorMin = min, AnchorMax = max },
                    Text = { Text = text, FontSize = size, Align = align }
                },
                panel);
            }
            public static void Input(ref CuiElementContainer container, string panel, string color, string text, int size, string min, string max, string command, TextAnchor align = TextAnchor.MiddleCenter)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiInputFieldComponent
                        {
                            Align = align,
                            CharsLimit = 30,
                            Color = color,
                            Command = command + text,
                            FontSize = size,
                            IsPassword = false,
                            Text = text
                        },
                        new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max },
                        new CuiNeedsCursorComponent()
                    }
                });
            }
            public static void Icon(ref CuiElementContainer container, string panel, string color, string imageurl, string min, string max)
            {
                container.Add(new CuiElement
                {
                    Name = CuiHelper.GetGuid(),
                    Parent = panel,
                    Components =
                    {
                        new CuiRawImageComponent
                        {
                            Url = imageurl,
                            Sprite = "assets/content/textures/generic/fulltransparent.tga",
                            Color = color
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = min,
                            AnchorMax = max
                        }
                    }
                });
            }
            public static string Color(string hexColor, float alpha)
            {
                if(hexColor.StartsWith("#"))
                {
                    hexColor = hexColor.Substring(1);
                }
                int red = int.Parse(hexColor.Substring(0, 2), NumberStyles.AllowHexSpecifier);
                int green = int.Parse(hexColor.Substring(2, 2), NumberStyles.AllowHexSpecifier);
                int blue = int.Parse(hexColor.Substring(4, 2), NumberStyles.AllowHexSpecifier);
                return $"{(double)red / 255} {(double)green / 255} {(double)blue / 255} {alpha}";
            }
        }
        #endregion

        #region Helpers
        string RandomString()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            List<char> charList = chars.ToList();

            string random = "";

            for (int i = 0; i <= UnityEngine.Random.Range(5, 10); i++)
            {
                random = random + charList[UnityEngine.Random.Range(0, charList.Count - 1)];
            }
            return random;
        }

        private void FindRoads()
        {
//            foreach (PathList x in TerrainMeta.Path.Roads)
//            {
//                var roadname = x.Name;
//                roads.Add(roadname, new Road()
//                {
//                    topo   = x.Topology,
//                    width  = x.Width,
//                    offset = x.TerrainOffset
//                });
//                //Puts(roadname);
//                foreach (var point in x.Path.Points)
//                {
//                    //Puts($"  {point.ToString()}");
//                    roads[roadname].points.Add(point);
//                }
//            }
        }

        void FindMonuments()
        {
            Vector3 extents = Vector3.zero;
            float realWidth = 0f;
            string name = null;
            foreach (MonumentInfo monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (monument.name.Contains("power_sub")) continue;
                realWidth = 0f;
                name = null;

                if(monument.name == "OilrigAI")
                {
                    name = "Small Oilrig";
                    realWidth = 100f;
                }
                else if(monument.name == "OilrigAI2")
                {
                    name = "Large Oilrig";
                    realWidth = 200f;
                }
                else
                {
                    name = Regex.Match(monument.name, @"\w{6}\/(.+\/)(.+)\.(.+)").Groups[2].Value.Replace("_", " ").Replace(" 1", "").Titleize();
                }
                if(monPos.ContainsKey(name)) continue;
                if(cavePos.ContainsKey(name)) name = name + RandomString();

                extents = monument.Bounds.extents;
                if(realWidth > 0f)
                {
                    extents.z = realWidth;
                }

                if(monument.name.Contains("cave"))
                {
                    cavePos.Add(name, monument.transform.position);
                }
                else if(monument.name.Contains("compound") && monPos["outpost"] == null)
                {
                    List<BaseEntity> ents = new List<BaseEntity>();
                    Vis.Entities(monument.transform.position, 50, ents);
                    foreach(BaseEntity entity in ents)
                    {
                        if(entity.PrefabName.Contains("piano"))
                        {
                            monPos.Add("outpost", entity.transform.position + new Vector3(1f, 0.1f, 1f));
                            monSize.Add("outpost", extents);
                        }
                    }
                }
                else if(monument.name.Contains("bandit") && monPos["bandit"] == null)
                {
                    List<BaseEntity> ents = new List<BaseEntity>();
                    Vis.Entities(monument.transform.position, 50, ents);
                    foreach(BaseEntity entity in ents)
                    {
                        if(entity.PrefabName.Contains("workbench"))
                        {
                            monPos.Add("bandit", Vector3.Lerp(monument.transform.position, entity.transform.position, 0.45f) + new Vector3(0, 1.5f, 0));
                            monSize.Add("bandit", extents);
                        }
                    }
                }
                else
                {
                    if(extents.z < 1)
                    {
                        extents.z = 50f;
                    }
                    monPos.Add(name, monument.transform.position);
                    monSize.Add(name, extents);
                }
            }
            monPos.OrderBy(x => x.Key);
            monSize.OrderBy(x => x.Key);
            cavePos.OrderBy(x => x.Key);
        }
        #endregion

        #region config
        protected override void LoadDefaultConfig()
        {
            Puts("Creating new config file.");
            var config = new ConfigData
            {
                Version = Version
            };
        }

        private void LoadConfigVariables()
        {
            configData = Config.ReadObject<ConfigData>();

            configData.Version = Version;
            SaveConfig(configData);
        }

        private void SaveConfig(ConfigData config)
        {
            Config.WriteObject(config, true);
        }

        public class ConfigData
        {
            public Options Options = new Options();
            public VersionNumber Version;
        }

        public class Options
        {
            public bool debug = false;
            public bool zeroOnWipe = true;
        }
        #endregion
    }
}
