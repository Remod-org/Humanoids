#region License (GPL v3)
/*
    Humanoids - NPC Players that can walk, fight, navigate, etc.
    Copyright (c) 2021 RFC1920 <desolationoutpostpve@gmail.com>

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
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Humanoids", "RFC1920", "1.1.2")]
    [Description("Adds interactive NPCs which can be modded by other plugins")]
    class Humanoids : RustPlugin
    {
        #region vars
        [PluginReference]
        private readonly Plugin Kits, RoadFinder;

        private DynamicConfigFile data;
        private ConfigData configData;
        public static Dictionary<string, AmmoTypes> ammoTypes = new Dictionary<string, AmmoTypes>();
        private List<ulong> isopen = new List<ulong>();

        private const string permNPCGuiUse = "humanoid.use";
        private const string NPCGUI = "npc.editor";
        private const string NPCGUK = "npc.kitselect";
        private const string NPCGUM = "npc.monselect";
        private const string NPCGUL = "npc.locoselect";
        private const string NPCGUN = "npc.kitsetnum";
        private const string NPCGUR = "npc.roadselect";
        private const string NPCGUS = "npc.select";
        private const string NPCGUV = "npc.setval";
        private readonly List<string> guis = new List<string>() { NPCGUI, NPCGUK, NPCGUL, NPCGUM, NPCGUN, NPCGUR, NPCGUS, NPCGUV };

        public static Humanoids Instance = null;
        private static Dictionary<ulong, HumanoidInfo> npcs = new Dictionary<ulong, HumanoidInfo>();

        // This is critical to the speed of operations on FindHumanoidByID/Name
        private Dictionary<ulong, HumanoidPlayer>  hpcacheid = new Dictionary<ulong, HumanoidPlayer>();
        private Dictionary<string, HumanoidPlayer> hpcachenm = new Dictionary<string, HumanoidPlayer>();

        private static Dictionary<string, Road> roads = new Dictionary<string, Road>();
        private static SortedDictionary<string, Vector3> monPos = new SortedDictionary<string, Vector3>();
        private static SortedDictionary<string, Vector3> monSize = new SortedDictionary<string, Vector3>();
        private static SortedDictionary<string, Vector3> cavePos = new SortedDictionary<string, Vector3>();

        private static Vector3 Vector3Down;
        private readonly static int playerMask = LayerMask.GetMask("Player (Server)");
        private readonly static int groundLayer = LayerMask.GetMask("Construction", "Terrain", "World");
        private readonly static int obstructionMask = LayerMask.GetMask(new[] { "Construction", "Deployed", "Clutter" });
        private readonly static int terrainMask = LayerMask.GetMask(new[] { "Terrain", "Tree" });
        #endregion

        #region Message
        private string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);
        private void Message(IPlayer player, string key, params object[] args) => player.Reply(Lang(key, player.Id, args));

        private void DoLog(string message)
        {
            if (configData.Options.debug) Interface.Oxide.LogInfo(message);
        }
        #endregion

        #region global
        private void OnServerInitialized()
        {
            LoadConfigVariables();
            AddCovalenceCommand("noid", "cmdNPC");

            Instance = this;

            LoadData();

            HumanoidPlayer[] allHumanoids = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (HumanoidPlayer obj in allHumanoids)
            {
                UnityEngine.Object.Destroy(obj);
            }
            HumanoidMovement[] allMove = Resources.FindObjectsOfTypeAll<HumanoidMovement>();
            foreach (HumanoidMovement obj in allMove)
            {
                UnityEngine.Object.Destroy(obj);
            }

            Dictionary<ulong, HumanoidInfo> tmpnpcs = new Dictionary<ulong, HumanoidInfo>(npcs);
            foreach (KeyValuePair<ulong, HumanoidInfo> npc in tmpnpcs)
            {
                if (npc.Value.userid == 0) continue;
                DoLog($"Spawning npc {npc.Value.userid}");
                ulong nid = 0;
                SpawnNPC(npc.Value, out nid);
            }
            tmpnpcs.Clear();
            SaveData();

            FindMonuments();

            if (RoadFinder)
            {
                object x = RoadFinder.CallHook("GetRoads");
                string json = JsonConvert.SerializeObject(x);
                roads = JsonConvert.DeserializeObject<Dictionary<string, Road>>(json);
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                foreach (string gui in guis)
                {
                    CuiHelper.DestroyUi(player, gui);
                }

                if (isopen.Contains(player.userID)) isopen.Remove(player.userID);
            }
        }

        void OnNewSave()
        {
            if (!configData.Options.zeroOnWipe) return;
            HumanoidPlayer[] allHumanoids = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (HumanoidPlayer obj in allHumanoids)
            {
                if (obj == null) continue;
                obj.info.loc = Vector3.zero;
            }
        }

        //private void OnServerShutdown()
        //{
        //    Unload();
        //}

        private void Unload()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                foreach (string gui in guis)
                {
                    CuiHelper.DestroyUi(player, gui);
                }

                if (isopen.Contains(player.userID)) isopen.Remove(player.userID);
            }

            HumanoidPlayer[] hp = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (HumanoidPlayer obj in hp)
            {
                Puts($"Killing player object {obj.player.userID.ToString()}");
                obj.movement.Stand();
                obj.GetComponent<BasePlayer>().Kill();
            }

            //SaveData();
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
               DoLog($"{pls.Value.userid.ToString()}");
            }
        }
        private void SaveData()
        {
            Dictionary<ulong, HumanoidInfo> tmpnpcs = new Dictionary<ulong, HumanoidInfo>();
            foreach(var x in npcs)
            {
                if (!x.Value.ephemeral)
                {
                    tmpnpcs.Add(x.Key, x.Value);
                }
            }
            Interface.Oxide.DataFileSystem.WriteObject(Name + "/humanoids", tmpnpcs);
            tmpnpcs.Clear();
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["npcgui"] = "Humanoid GUI",
                ["npcguisel"] = "HumanoidGUI NPC Select ",
                ["npcguikit"] = "HumanoidGUI Kit Select",
                ["npcguimon"] = "HumanoidGUI Monument Select",
                ["npcguiroad"] = "HumanoidGUI Road Select",
                ["npcguiloco"] = "HumanoidGUI LocoMode Select",
                ["close"] = "Close",
                ["none"] = "None",
                ["noid"] = "noid",
                ["start"] = "Start",
                ["end"] = "End",
                ["humanoids"] = "Humanoids",
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

//        object RaycastAll<T> (Ray ray) where T : BaseEntity
//        {
//            var hits = Physics.RaycastAll (ray);
//            GamePhysics.Sort (hits);
//            var distance = 100f;
//            object target = false;
//            foreach (var hit in hits) {
//                var ent = hit.GetEntity ();
//                if (ent is T && hit.distance < distance) {
//                    target = ent;
//                    break;
//                }
//            }
//
//            return target;
//        }

//        private List<BasePlayer> FindLocalHumanoids(Vector3 position, float range)
//        {
//            List<BasePlayer> pls = new List<BasePlayer>();
//            Vis.Entities(position, range, pls);
//            pls = pls.ToArray().OrderBy((d) => (d.transform.position - position).sqrMagnitude).ToList();
//
//            return pls;
//        }

        #region Oxide Hooks
        private void OldOnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            //            if (input.current.buttons > 0)
            //                DoLog($"OnPlayerInput: {input.current.buttons}");
            if (!input.WasJustPressed(BUTTON.USE)) return;

            List<BasePlayer> pls = new List<BasePlayer>();
            Vis.Entities(player.transform.position, 3f, pls);
            pls = pls.ToArray().OrderBy((d) => (d.transform.position - player.transform.position).sqrMagnitude).ToList();

            for (int i = 0; i < pls.Count; i++)
            {
                if (pls[i] == player) continue;
                HumanoidPlayer hp = pls[i].GetComponent<HumanoidPlayer>();
                if (hp == null) continue;

                if (hp.movement.sitting)
                {
                    DoLog($"Trying to stand {hp.info.displayName}");
                    hp.movement.Stand();
                }
                if (hp.info.entrypause)
                {
                    DoLog($"Trying to pause {hp.info.displayName}");
                    hp.movement.Stop(true);
                }
                hp.LookTowards(player.transform.position, true);
                //Message(player.IPlayer, hp.info.displayName);
                Interface.Oxide.CallHook("OnUseNPC", hp.player, player);
                SaveData();
                break;
            }
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            //            if (input.current.buttons > 0)
            //                DoLog($"OnPlayerInput: {input.current.buttons}");
            if (!input.WasJustPressed(BUTTON.USE)) return;

            RaycastHit hit;
            if (Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, playerMask))
            {
                BasePlayer pl = hit.GetEntity().ToPlayer();
                HumanoidPlayer hp = pl.GetComponent<HumanoidPlayer>();
                if (hp == null) return;

                if (hp.movement.sitting)
                {
                    DoLog($"Trying to stand {hp.info.displayName}");
                    hp.movement.Stand();
                }
                if (hp.info.entrypause)
                {
                    DoLog($"Trying to pause {hp.info.displayName}");
                    hp.movement.Stop(true);
                }
                hp.LookTowards(player.transform.position, true);
                //Message(player.IPlayer, hp.info.displayName);
                Interface.Oxide.CallHook("OnUseNPC", hp.player, player);
                SaveData();
            }
        }

        // More decisions needed here on what to do (return fire, etc.)
//        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
//        {
//            if (entity == null) return null;
//            if (hitinfo == null) return null;
//
//            var pl = hitinfo.HitEntity as BasePlayer;
//            if (pl = null) return null;
//            var hp = pl.GetComponentInParent<HumanoidPlayer>();
//            if (hp = null) return null;
//
//            if (hp.info.invulnerable) return true;
//
//            hp.movement.attacked = true;
//            hp.movement.attackEntity = hitinfo.HitEntity as BaseCombatEntity;
//
//            return null;
//        }
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitinfo)
        {
            var hp = entity.GetComponent<HumanoidPlayer>();
            if (hp != null)
            {
                DoLog($"{hitinfo.HitEntity.ShortPrefabName} attacking Humanoid {hp.info.displayName}");
                if (hitinfo.Initiator is BaseCombatEntity && !(hitinfo.Initiator is Barricade) && hp.info.defend)
                {
                    DoLog("Setting attacked to true");
                    hp.movement.attacked = true;
                }
                //if (hp.info.message_hurt != null && hp.info.message_hurt.Count != 0)
                //{
                //    if (hitinfo.InitiatorPlayer != null)
                //    {
                //        SendMessage(hp, hitinfo.InitiatorPlayer, GetRandomMessage(hp.info.message_hurt));
                //    }
                //}
                Interface.Oxide.CallHook("OnHitNPC", entity.GetComponent<BaseCombatEntity>(), hitinfo);
                if (hp.info.invulnerable)
                {
                    hitinfo.damageTypes = new DamageTypeList();
                    hitinfo.DoHitEffects = false;
                    hitinfo.HitMaterial = 0;
                }
                else
                {
                    hp.protection.Scale(hitinfo.damageTypes);
                }

                if (hp.movement.sitting && !hp.movement.riding)
                {
                    hp.movement.Stand();
                }
                //hp.movement.Evade();
            }
        }

        private object CanLootPlayer(BasePlayer target, BasePlayer player)
        {
            if (player == null || target == null) return null;
            HumanoidPlayer hp = target.GetComponentInParent<HumanoidPlayer>();
            if (hp != null && !hp.info.lootable)
            {
                DoLog($"Player {player.displayName}:{player.UserIDString} looting NPC {hp.info.displayName}");
                NextTick(player.EndLooting);
                return true;
            }
            return null;
        }

        private object CanLootEntity(BasePlayer player, LootableCorpse corpse)
        {
            if (player == null || corpse == null) return null;
            HumanoidPlayer hp = corpse.GetComponentInParent<HumanoidPlayer>();
            if (hp == null) return null;
            DoLog($"Player {player.displayName}:{player.UserIDString} looting NPC {corpse.name}:{corpse.playerSteamID.ToString()}");
            if (hp.info.lootable)
            {
                NextTick(player.EndLooting);
                return null;
            }

            return true;
        }

        private void OnLootPlayer(BasePlayer looter, BasePlayer target)
        {
            if (npcs.ContainsKey(target.userID))
            {
                Interface.Oxide.CallHook("OnLootNPC", looter.inventory.loot, target, target.userID);
            }
        }

        private void OnLootEntity(BasePlayer looter, BaseEntity entity)
        {
            if (looter == null || !(entity is PlayerCorpse)) return;
            ulong userId = ((PlayerCorpse)entity).playerSteamID;
            HumanoidInfo hi = null;
            if (npcs.TryGetValue(userId, out hi))
            if (hi != null)
            {
                Interface.Oxide.CallHook("OnLootNPC", looter.inventory.loot, entity, userId);
            }
        }

        private object OnUserCommand(BasePlayer player, string command, string[] args)
        {
            if (command != "noid" && isopen.Contains(player.userID)) return true;
            return null;
        }

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (command != "noid" && isopen.Contains(player.userID)) return true;
            return null;
        }
        #endregion

        #region commands
        [Command("noid")]
        void cmdNPC(IPlayer iplayer, string command, string[] args)
        {
            BasePlayer player = iplayer.Object as BasePlayer;
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
                        IsOpen(player.userID, false);
                        break;
                    case "list":
                        foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
                        {
                            string ns = pls.Value.displayName + "(" + pls.Key.ToString() + ")";
                            Message(iplayer, ns);
                        }
                        break;
                    case "spawn":
                    case "create":
                    case "new":
                        HumanoidInfo npc = new HumanoidInfo(0, player.transform.position, player.transform.rotation);
                        ulong x = 0;
                        SpawnNPC(npc, out x);
                        NPCSelectGUI(player);
                        break;
                    case "edit":
                        {
                            ulong npcid = 0;
                            if (args.Length > 1)
                            {
                                ulong.TryParse(args[1], out npcid);
                                if (npcid > 0)
                                {
                                    NpcEditGUI(player, npcid);
                                    break;
                                }
                                else
                                {
                                    List<string> newarg = new List<string>(args);
                                    newarg.RemoveAt(0);
                                    string nom = string.Join(" ", newarg);
                                    foreach (KeyValuePair<ulong, HumanoidInfo> pls in npcs)
                                    {
                                        DoLog($"Checking match of {nom} to {pls.Value.displayName}");
                                        if (pls.Value.displayName.ToLower() == nom.ToLower())
                                        {
                                            NpcEditGUI(player, pls.Value.userid);
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
                                    BasePlayer pl = hit.GetEntity().ToPlayer();
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
                                    else if (npcid == pls.Value.userid)
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
                                    BasePlayer pl = hit.GetEntity().ToPlayer();
                                    if (pl.userID > 0)
                                    {
                                        HumanoidInfo isnpc = npcs[pl.userID];
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
                        if (args.Length > 2)
                        {
                            NPCKitGUI(player, ulong.Parse(args[1]), args[2]);
                        }
                        break;
                    case "npcselmonstart":
                        if (args.Length > 2)
                        {
                            NPCMonGUI(player, ulong.Parse(args[1]), args[2], "start");
                        }
                        break;
                    case "npcselmonend":
                        if (args.Length > 2)
                        {
                            NPCMonGUI(player, ulong.Parse(args[1]), args[2], "end");
                        }
                        break;
                    case "npcselroad":
                        if (args.Length > 2)
                        {
                            NPCRoadGUI(player, ulong.Parse(args[1]), args[2] + " " + args[3]);
                        }
                        break;
                    case "npcselloco":
                        if (args.Length > 1)
                        {
                            NPCLocoGUI(player, ulong.Parse(args[1]));
                        }
                        break;
                    case "kitsel":
                        if (args.Length > 2)
                        {
                            CuiHelper.DestroyUi(player, NPCGUK);
                            ulong userid = ulong.Parse(args[1]);
                            npc = npcs[userid];
                            string kitname = args[2];
                            npc.kit = kitname;
                            HumanoidPlayer hp = FindHumanoidByID(userid);
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, userid);
                        }
                        break;
                    case "monsel":
                        if (args.Length > 2)
                        {
                            CuiHelper.DestroyUi(player, NPCGUM);
                            ulong userid = ulong.Parse(args[1]);
                            npc = npcs[userid];
                            switch(args[3])
                            {
                                case "end":
                                    npc.monend = args[2];
                                    break;
                                case "start":
                                default:
                                    npc.monstart = args[2];
                                    break;
                            }
                            HumanoidPlayer hp = FindHumanoidByID(userid);
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, userid);
                        }
                        break;
                    case "roadsel":
                        if (args.Length > 2)
                        {
                            CuiHelper.DestroyUi(player, NPCGUR);
                            ulong userid = ulong.Parse(args[1]);
                            npc = npcs[userid];
                            string roadname = args[2] + " " + args[3];
                            npc.roadname = roadname;
                            HumanoidPlayer hp = FindHumanoidByID(userid);
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, userid);
                        }
                        break;
                    case "locomode":
                        if (args.Length > 2)
                        {
                            CuiHelper.DestroyUi(player, NPCGUL);
                            ulong userid = ulong.Parse(args[1]);
                            npc = npcs[userid];
                            LocoMode locomode;
                            Enum.TryParse(args[2], out locomode);
                            npc.locomode = locomode;
                            HumanoidPlayer hp = FindHumanoidByID(userid);
                            SaveData();
                            RespawnNPC(hp.player);
                            NpcEditGUI(player, userid);
                        }
                        break;
                    case "spawnhere":
                        if (args.Length > 1)
                        {
                            ulong npcid = ulong.Parse(args[1]);
                            string newSpawn = player.transform.position.x.ToString() + "," + player.transform.position.y + "," + player.transform.position.z.ToString();
                            Vector3 ns = player.transform.position;
                            Quaternion newRot;
                            TryGetPlayerView(player, out newRot);

                            HumanoidPlayer hp = FindHumanoidByID(npcid);
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
                        if (args.Length > 1)
                        {
                            ulong npcid = ulong.Parse(args[1]);
                            CuiHelper.DestroyUi(player, NPCGUI);
                            CuiHelper.DestroyUi(player, NPCGUK);
                            CuiHelper.DestroyUi(player, NPCGUL);
                            CuiHelper.DestroyUi(player, NPCGUN);
                            CuiHelper.DestroyUi(player, NPCGUR);
                            CuiHelper.DestroyUi(player, NPCGUS);
                            CuiHelper.DestroyUi(player, NPCGUV);
                            IsOpen(player.userID, false);
                            Teleport(player, npcs[npcid].loc);
                        }
                        break;
                    case "npctoggle":
                        if (args.Length > 3)
                        {
                            ulong userid = ulong.Parse(args[1]);
                            string toset = args[2];
                            string newval = args[3] == "True" ? "false" : "true";
                            SetHumanoidInfo(userid, args[2], args[3]);
                            NpcEditGUI(player, userid);
                            SaveData();
                        }
                        break;
                    case "npcset":
                        if (args.Length > 1)
                        {
                            ulong userid = ulong.Parse(args[1]);
                            SetHumanoidInfo(userid, args[2], args[4]);
                        }
                        break;
                    case "selkitclose":
                        CuiHelper.DestroyUi(player, NPCGUK);
                        break;
                    case "selmonclose":
                        CuiHelper.DestroyUi(player, NPCGUM);
                        break;
                    case "selroadclose":
                        CuiHelper.DestroyUi(player, NPCGUR);
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
        #endregion

        #region Our Inbound Hooks
        private bool IsHumanoid(BasePlayer player) => player.GetComponentInParent<HumanoidPlayer>() != null;
        private ulong SpawnHumanoid(Vector3 position, Quaternion currentRot, string name = "noid", bool ephemeral = false, ulong clone = 0)
        {
            foreach (KeyValuePair<ulong, HumanoidInfo> pair in npcs)
            {
                if (pair.Value.displayName == name && clone == 0)
                {
                    return pair.Key;
                }
            }
            Puts("GOT HERE");

            ulong npcid = 0;
            HumanoidInfo hi = new HumanoidInfo(npcid, position, currentRot)
            {
                displayName = name,
                ephemeral = ephemeral
            };
            SpawnNPC(hi, out npcid);
            return npcid;
        }
        private string GetHumanoidName(ulong npcid)
        {
            DoLog($"Looking for humanoid: {npcid.ToString()}");
            HumanoidPlayer hp = FindHumanoidByID(npcid);
            if (hp == null) return null;
            DoLog($"Found humanoid: {hp.info.displayName}");
            return hp.info.displayName;
        }
        private bool RemoveHumanoidById(ulong npcid)
        {
            DoLog($"RemoveHumanoidById called for {npcid.ToString()}");
            HumanoidPlayer hp = FindHumanoidByID(npcid);
            if (hp == null) return false;
            BasePlayer npc = hp.GetComponentInParent<BasePlayer>();
            if (npc == null) return false;
            KillNpc(npc);
            npcs.Remove(hp.info.userid);
            SaveData();
            return true;
        }
        private bool RemoveHumanoidByName(string name)
        {
            DoLog($"RemoveHumanoidByName called for {name}");
            HumanoidPlayer hp = FindHumanoidByName(name);
            if (hp == null) return false;
            BasePlayer npc = hp.GetComponentInParent<BasePlayer>();
            if (npc == null) return false;
            KillNpc(npc);
            npcs.Remove(hp.info.userid);
            SaveData();
            return true;
        }
        private void SetHumanoidInfo(ulong npcid, string toset, string data, string rot = null)
        {
            DoLog($"SetHumanoidInfo called for {npcid.ToString()} {toset},{data}");
            HumanoidPlayer hp = FindHumanoidByID(npcid);
            if (hp == null) return;

            switch (toset)
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
                case "allowsit":
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
            DoLog("Saving Data");
            SaveData();
            DoLog("Respawning");
            RespawnNPC(hp.player);
        }
        private void GiveHumanoid(ulong npcid, string itemname, string loc = "wear")
        {
            Item item = ItemManager.CreateByName(itemname, 1, 0);
            HumanoidPlayer npc = FindHumanoidByID(npcid);
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
            //Puts($"npcPlayNote called for {npcid.ToString()}");
            if (npcs.ContainsKey(npcid))
            {
                HumanoidPlayer hp = FindHumanoidByID(npcid);
                if (hp.info.band != band) return false;
                hp.PlayNote(note, sharp, octave, noteval, duration);
            }
            return null;
        }
        #endregion

        #region GUI
        // Determine open GUI to limit interruptions
        private void IsOpen(ulong uid, bool set=false)
        {
            if (set)
            {
                DoLog($"Setting isopen for {uid}");
                if (!isopen.Contains(uid)) isopen.Add(uid);
                return;
            }
            DoLog($"Clearing isopen for {uid}");
            isopen.Remove(uid);
        }

        void NPCSelectGUI(BasePlayer player)
        {
            if (player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUS);

            string description = Lang("npcguisel");
            CuiElementContainer container = UI.Container(NPCGUS, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUS, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Label(ref container, NPCGUS, UI.Color("#22cc44", 1f), Lang("musician"), 12, "0.72 0.92", "0.77 1");
            UI.Label(ref container, NPCGUS, UI.Color("#2244cc", 1f), Lang("standard"), 12, "0.79 0.92", "0.86 1");
            UI.Button(ref container, NPCGUS, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"noid selclose");
            int col = 0;
            int row = 0;

            foreach (KeyValuePair<ulong, HumanoidInfo> npc in npcs)
            {
                if (row > 10)
                {
                    row = 0;
                    col++;
                }
                string hBand = npc.Value.band.ToString();
                if (hBand == "99") continue;
                string color = "#2244cc";
                if (hBand != "0") color = "#22cc44";

                string hName = npc.Value.displayName;
                float[] posb = GetButtonPositionP(row, col);
                UI.Button(ref container, NPCGUS, UI.Color(color, 1f), hName, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid edit {npc.Value.displayName}");
                row++;
            }
            float[] posn = GetButtonPositionP(row, col);
            UI.Button(ref container, NPCGUS, UI.Color("#cc3333", 1f), Lang("new"), 12, $"{posn[0]} {posn[1]}", $"{posn[0] + ((posn[2] - posn[0]) / 2)} {posn[3]}", $"noid new");

            CuiHelper.AddUi(player, container);
        }

        void NpcEditGUI(BasePlayer player, ulong npc = 0)
        {
            if (player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUI);

            string npcname = Lang("needselect");
            if (npc > 0)
            {
                npcname = Lang("editing") + " " + npcs[npc].displayName + "(" + npc.ToString() + ")";
            }

            CuiElementContainer container = UI.Container(NPCGUI, UI.Color("2b2b2b", 1f), "0.05 0.05", "0.95 0.95", true, "Overlay");
            if (npc == 0)
            {
                UI.Button(ref container, NPCGUI, UI.Color("#cc3333", 1f), Lang("new"), 12, "0.79 0.95", "0.85 0.98", $"noid new");
            }
            else
            {
                UI.Button(ref container, NPCGUI, UI.Color("#cc3333", 1f), Lang("remove"), 12, "0.79 0.95", "0.85 0.98", $"noid remove {npc.ToString()}");
            }
            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("select"), 12, "0.86 0.95", "0.92 0.98", $"noid select");
            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("close"), 12, "0.93 0.95", "0.99 0.98", $"noid close");
            UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("npcgui") + ": " + npcname, 24, "0.2 0.92", "0.7 1");

            int col = 0;
            int row = 0;

            if (npc == 0)
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
                    { "roadname",  npcs[npc].roadname },
                    { "monstart",  npcs[npc].monstart },
                    { "monend",  npcs[npc].monend },
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

                foreach (KeyValuePair<string, string> info in npcinfo)
                {
                    if (row > 11)
                    {
                        row = 0;
                        col++;
                        col++;
                    }
                    float[] posl = GetButtonPositionP(row, col);
                    float[] posb = GetButtonPositionP(row, col + 1);

                    if (!isLarge.ContainsKey(info.Key))
                    {
                        UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), info.Key, 12, $"{posl[0]} {posl[1]}", $"{posl[0] + ((posl[2] - posl[0]) / 2)} {posl[3]}");
                    }
                    if (info.Key == "kit")
                    {
                        if (plugins.Exists("Kits"))
                        {
                            string kitname = info.Value != null ? info.Value : Lang("none");
                            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), kitname, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npcselkit {npc.ToString()} {kitname}");
                        }
                        else
                        {
                            UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("none"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        }
                    }
                    else if (info.Key == "roadname" && (npcs[npc].locomode == LocoMode.Road || npcs[npc].locomode == LocoMode.Ride))
                    {
                        if (plugins.Exists("RoadFinder"))
                        {
                            string roadname = info.Value != null ? info.Value : Lang("none");
                            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), roadname, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npcselroad {npc.ToString()} {roadname}");
                        }
                        else
                        {
                            UI.Label(ref container, NPCGUI, UI.Color("#ffffff", 1f), Lang("none"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        }
                    }
                    else if (info.Key == "monstart" && npcs[npc].locomode == LocoMode.Monument)
                    {
                        string monname = info.Value != null ? info.Value : Lang("none");
                        UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), monname, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npcselmonstart {npc.ToString()} {monname}");
                    }
                    else if (info.Key == "monend" && npcs[npc].locomode == LocoMode.Monument)
                    {
                        string monname = info.Value != null ? info.Value : Lang("none");
                        UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), monname, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npcselmonend {npc.ToString()} {monname}");
                    }
                    else if (info.Key == "locomode")
                    {
                        string locomode = info.Value != null ? info.Value : Lang("none");
                        UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), locomode, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npcselloco {npc.ToString()}");
                    }
                    else if (info.Key == "loc")
                    {
                        row++;
                        UI.Label(ref container, NPCGUI, UI.Color("#535353", 1f), info.Value, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        UI.Input(ref container, NPCGUI, UI.Color("#ffffff", 1f), info.Value, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid spawn {npc.ToString()} {info.Key} ");
                        posb = GetButtonPositionP(row, col + 1);
                        UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("spawnhere"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid spawnhere {npc.ToString()} ");
                        if (StringToVector3(info.Value) != Vector3.zero)
                        {
                            row++;
                            posb = GetButtonPositionP(row, col + 1);
                            UI.Button(ref container, NPCGUI, UI.Color("#d85540", 1f), Lang("tpto"), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid tpto {npc.ToString()} ");
                        }
                    }
                    else if (isLarge.ContainsKey(info.Key))
                    {
                        //                        string oldval = info.Value != null ? info.Value : Lang("unset");
                        //                        UI.Label(ref container, NPCGUI, UI.Color("#535353", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]))} {posb[3]}");
                        //                        UI.Input(ref container, NPCGUI, UI.Color("#ffffff", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]))} {posb[3]}", $"noid npcset {npc.ToString()} {info.Key} {oldval} ");
                    }
                    else if (isBool.ContainsKey(info.Key))
                    {
                        UI.Button(ref container, NPCGUI, UI.Color("#222255", 1f), info.Value, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npctoggle {npc.ToString()} {info.Key} {info.Value}");
                    }
                    else
                    {
                        string oldval = info.Value != null ? info.Value : Lang("unset");
                        UI.Label(ref container, NPCGUI, UI.Color("#535353", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}");
                        UI.Input(ref container, NPCGUI, UI.Color("#ffffff", 1f), oldval, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid npcset {npc.ToString()} {info.Key} ");
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
            if (player == null) return;
        }

        void NPCMonGUI(BasePlayer player, ulong npc, string mon = null, string pos = "start")
        {
            if (player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUM);

            string description = Lang("npcguimon") + ": " + Lang(pos);
            CuiElementContainer container = UI.Container(NPCGUM, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUM, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Button(ref container, NPCGUM, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"noid selmonclose");

            int col = 0;
            int row = 0;

            foreach (string moninfo in monPos.Keys)
            {
                if (row > 10)
                {
                    row = 0;
                    col++;
                }
                float[] posb = GetButtonPositionP(row, col);

                if (moninfo == mon)
                {
                    UI.Button(ref container, NPCGUM, UI.Color("#d85540", 1f), moninfo, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid monsel {npc.ToString()} {moninfo} {pos}");
                }
                else
                {
                    UI.Button(ref container, NPCGUM, UI.Color("#424242", 1f), moninfo, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid monsel {npc.ToString()} {moninfo} {pos}");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        void NPCKitGUI(BasePlayer player, ulong npc, string kit = null)
        {
            if (player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUK);

            string description = Lang("npcguikit");
            CuiElementContainer container = UI.Container(NPCGUK, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUK, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Button(ref container, NPCGUK, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"noid selkitclose");

            int col = 0;
            int row = 0;

            if (kit == null) kit = Lang("none");
            List<string> kits = new List<string>();
            Kits?.CallHook("GetKitNames", kits);
            foreach (string kitinfo in kits)
            {
                if (row > 10)
                {
                    row = 0;
                    col++;
                }
                float[] posb = GetButtonPositionP(row, col);

                if (kitinfo == kit)
                {
                    UI.Button(ref container, NPCGUK, UI.Color("#d85540", 1f), kitinfo, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid kitsel {npc.ToString()} {kitinfo}");
                }
                else
                {
                    UI.Button(ref container, NPCGUK, UI.Color("#424242", 1f), kitinfo, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid kitsel {npc.ToString()} {kitinfo}");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        void NPCRoadGUI(BasePlayer player, ulong npc, string road  = null)
        {
            if (player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUR);

            string description = Lang("npcguiroad");
            CuiElementContainer container = UI.Container(NPCGUR, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUR, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Button(ref container, NPCGUR, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"noid selroadclose");

            int col = 0;
            int row = 0;

            if (road == null) road = Lang("none");
            foreach (KeyValuePair<string, Road> roadinfo in roads)
            {
                DoLog(roadinfo.Key);
                if (row > 10)
                {
                    row = 0;
                    col++;
                }
                float[] posb = GetButtonPositionP(row, col);

                if (roadinfo.Key == road)
                {
                    UI.Button(ref container, NPCGUR, UI.Color("#d85540", 1f), roadinfo.Key, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid roadsel {npc.ToString()} {roadinfo.Key}");
                }
                else
                {
                    UI.Button(ref container, NPCGUR, UI.Color("#424242", 1f), roadinfo.Key, 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid roadsel {npc.ToString()} {roadinfo.Key}");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
        }

        void NPCLocoGUI(BasePlayer player, ulong npc)
        {
            if (player == null) return;
            IsOpen(player.userID, true);
            CuiHelper.DestroyUi(player, NPCGUL);

            string description = Lang("npcguiloco");
            CuiElementContainer container = UI.Container(NPCGUL, UI.Color("242424", 1f), "0.1 0.1", "0.9 0.9", true, "Overlay");
            UI.Label(ref container, NPCGUL, UI.Color("#ffffff", 1f), description, 18, "0.23 0.92", "0.7 1");
            UI.Button(ref container, NPCGUL, UI.Color("#d85540", 1f), Lang("close"), 12, "0.92 0.93", "0.985 0.98", $"noid sellococlose");

            int col = 0;
            int row = 0;

            HumanoidPlayer hp = FindHumanoidByID(npc);
            foreach (LocoMode mode in (LocoMode[]) Enum.GetValues(typeof(LocoMode)))
            {
                float[] posb = GetButtonPositionP(row, col);

                if (hp.info.locomode == mode)
                {
                    UI.Button(ref container, NPCGUL, UI.Color("#d85540", 1f), mode.ToString(), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid locomode {npc.ToString()} {mode}");
                }
                else
                {
                    UI.Button(ref container, NPCGUL, UI.Color("#424242", 1f), mode.ToString(), 12, $"{posb[0]} {posb[1]}", $"{posb[0] + ((posb[2] - posb[0]) / 2)} {posb[3]}", $"noid locomode {npc.ToString()} {mode}");
                }
                row++;
            }

            CuiHelper.AddUi(player, container);
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

        private HumanoidPlayer FindHumanoidByID(ulong userid, bool playerid = false)
        {
            HumanoidPlayer hp;
            if (hpcacheid.TryGetValue(userid, out hp)) return hp;
            HumanoidPlayer[] allHumanoids = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (HumanoidPlayer humanplayer in allHumanoids)
            {
                DoLog($"Is {humanplayer.player.displayName} a Humanoid?");
                if (humanplayer.player.userID != userid && humanplayer.info.userid != userid) continue;
                hpcacheid[userid] = humanplayer;
                return humanplayer;
            }
            return null;
        }

        public HumanoidPlayer FindHumanoidByName(string name)
        {
            HumanoidPlayer hp;
            if (hpcachenm.TryGetValue(name, out hp)) return hp;
            HumanoidPlayer[] allHumanoids = Resources.FindObjectsOfTypeAll<HumanoidPlayer>();
            foreach (HumanoidPlayer humanplayer in allHumanoids)
            {
                if (humanplayer.info.displayName != name) continue;
                hpcachenm[name] = humanplayer;
                return humanplayer;
            }
            return null;
        }

        private void RemoveNPC(HumanoidInfo info)
        {
            if (npcs.ContainsKey(info.userid))
            {
                //storedData.Humanoids.Remove(npcs[info.userid]);
                //npcs[info.userid] = null;
                npcs.Remove(info.userid);
            }
            hpcacheid.Remove(info.userid);
            hpcachenm.Remove(info.displayName);
            HumanoidPlayer npc = FindHumanoidByID(info.userid);
            if (npc?.player != null && !npc.player.IsDestroyed)
            {
                npc.player.KillMessage();
            }
            SaveData();
        }
        public void RespawnNPC(BasePlayer player)
        {
            DoLog($"Attempting to respawn humanoid...");
            HumanoidPlayer n = FindHumanoidByID(player.userID, true);
            HumanoidInfo info = n.info;
            if (player != null && info != null)
            {
                KillNpc(player);
                hpcacheid.Remove(player.userID);
                hpcachenm.Remove(player.name);
                ulong x = 0;
                SpawnNPC(info, out x);
                player.EndSleeping();
            }
        }
        private void KillNpc(BasePlayer player)
        {
            List<BasePlayer> players = new List<BasePlayer>();
            Vis.Entities(player.transform.position, 0.01f, players);
            foreach (BasePlayer pl in players)
            {
                pl.KillMessage();
            }
        }
        private void SpawnNPC(HumanoidInfo info, out ulong npcid)
        {
            DoLog($"Attempting to spawn new humanoid...");
            if (info.userid == 0)
            {
                info.userid = (ulong)UnityEngine.Random.Range(0, 2147483647);
            }
            List<BasePlayer> lurkers = new List<BasePlayer>();
            Vis.Entities(info.loc, 1, lurkers);
            foreach (BasePlayer lurker in lurkers)
            {
                if (lurker.displayName == info.displayName) lurker.Kill();
            }
            BasePlayer player = GameManager.server.CreateEntity("assets/prefabs/player/player.prefab", info.loc, info.rot).ToPlayer();
            DoLog($"Player object created...");
            HumanoidPlayer npc = player.gameObject.AddComponent<HumanoidPlayer>();
            DoLog($"Humanoid object added to player...");
            npc.SetInfo(info);

            player.Spawn();
            //hpcache[info.userid] = npc;
            //hpcachen[info.displayName] = npc;

            info.userid = player.userID;
            if (info.loc != Vector3.zero)
            {
                DoLog($"Setting location to {info.loc}");
                player.transform.position = info.loc;
            }
            if (info.rot != Quaternion.identity)
            {
                DoLog($"Setting rotation to {info.rot}");
                player.transform.rotation = info.rot;
            }
            UpdateInventory(npc);
            npcid = info.userid;

            DoLog($"Spawned NPC with userid {player.UserIDString}");

            if (npcs.ContainsKey(info.userid))
            {
                npcs[info.userid] = npc.info;
            }
            else
            {
                npcs.Add(info.userid, npc.info);
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
                foreach (KeyValuePair<DamageType, float> protection in hp.info.protections)
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
            Monument = 32,
            Defend = 64,
            Gather = 128
        }

        public class HumanoidInfo
        {
            // Basic
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
            public bool canfly = false;

            public bool ephemeral = false;
            public bool hostile = false;
            public bool ahostile = false;
            public bool defend = false;
            public bool evade = false;
            public bool follow = false;
            public bool needsammo = false;
            public bool invulnerable = true;
            public bool lootable = false;
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
            public float damageAmount = 1f;
            public float followTime = 30f;
            public float entrypausetime = 5f;
            public string roadname;
            public string monstart;
            public string monend;
            public LocoMode locomode;
            public LocoMode defaultLoco;
            public string waypoint;

            public int band = 0;

            public HumanoidInfo(ulong uid, Vector3 position, Quaternion rotation)
            {
                displayName = "Noid";
                invulnerable = true;
                //health = 50;
                hostile = false;
                ahostile = false;
                needsammo = true;
                //dropWeapon = true;
                //respawn = true;
                //respawnSeconds = 60;
                loc = position;
                rot = rotation;
                //collisionRadius = 10;
                damageDistance = 3;
                damageAmount = 10;
                attackDistance = 100;
                maxDistance = 200;
                //hitchance = 0.75f;
                speed = 3;
                //stopandtalk = true;
                //stopandtalkSeconds = 3;
                enable = true;
                //persistent = true;
                lootable = false;
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

                for (int i = 0; i < (int)DamageType.LAST; i++)
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
            public bool attacked = false;
            public bool moving = false;
            public bool sitting = false;
            public bool riding = false;
            public bool swimming = false;
            public bool flying = false;
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
                if (npc.player == null) return;
                if (attacked && npc.info.defend && npc.info.locomode != LocoMode.Defend)
                {
                    Instance.Puts($"Humanoid {npc.player.displayName} was attacked, changing locomode to Defend!");
                    npc.info.defaultLoco = npc.info.locomode;
                    npc.info.locomode = LocoMode.Defend;
                    DetermineMove();
                }
                else
                {
                    DetermineMove(); // based on locomode
                    FindVictim();
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
                //if (npc.info.canride)
                //{
                //    Instance.DoLog($"[HumanoidMovement] {npc.info.displayName} can ride!");
                //    if (!riding)
                //    {
                //        Ride();
                //    }
                //    else
                //    {
                //        var horse = npc.player.GetMountedVehicle() as RidableHorse;
                //        if (horse != null)
                //        {
                //            InputMessage message = new InputMessage() { buttons = 2 };
                //            horse.RiderInput(new InputState() { current = message }, npc.player);
                //            Instance.DoLog($"[HumanoidMovement] {npc.info.displayName} moving horse.");
                //        }
                //    }
                //}
                if (Paths.Count == 0 || currentWaypoint >= Paths.Count)
                {
                    // At the end, walk the path the other way
                    if (Paths.Count > 0)
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
                float d = Vector3.Distance(EndPos, StartPos);
                float ts = Time.realtimeSinceStartup;
//                Instance.DoLog($"SetMovementPoint({currentWaypoint.ToString()}) Start: {StartPos.ToString()}, current {npc.info.loc.ToString()}, End: {endpos.ToString()}), time: {ts}");
            }

            private void DoAttack()
            {
                Instance.Puts("DoAttack() called, which does nothing ;)");
            }

            private void Defend()
            {
                if (attackEntity.IsDead() || attackEntity.IsDestroyed)
                {
                    attacked = false;
                    npc.info.locomode = npc.info.defaultLoco;
                    return;
                }

                HeldEntity weapon = npc.player.GetHeldEntity() ?? null;
                if (weapon as BaseProjectile == null)
                {
                    weapon = npc.EquipFirstWeapon();
                    if (weapon == null)
                    {
                        weapon = npc.EquipFirstTool();
                    }
                }
                if (weapon == null)
                {
                    return; // If only you could smack a player with a fish...
                }

                npc.LookTowards(attackEntity.transform.position);
                FiringEffect(attackEntity, weapon as BaseProjectile, npc.info.damageAmount, false); // miss is based on a hitchance calc. false for now
                DoAttack();
            }

            private void FiringEffect(BaseCombatEntity target, BaseProjectile weapon, float da, bool miss)
            {
                ItemModProjectile component = weapon.primaryMagazine.ammoType.GetComponent<ItemModProjectile>();
                Vector3 source = npc.player.transform.position + npc.player.GetOffset();
                if (weapon.MuzzlePoint != null)
                {
                    source += Quaternion.LookRotation(target.transform.position - npc.player.transform.position) * weapon.MuzzlePoint.position;
                }
                Vector3 direction = (target.transform.position + npc.player.GetOffset() - source).normalized;
                Vector3 vector32 = direction * (component.projectileVelocity * weapon.projectileVelocityScale);

                if (miss)
                {
                    float aimCone = weapon.GetAimCone();
                    vector32 += Quaternion.Euler(UnityEngine.Random.Range((float)(-aimCone * 0.5), aimCone * 0.5f), UnityEngine.Random.Range((float)(-aimCone * 0.5), aimCone * 0.5f), UnityEngine.Random.Range((float)(-aimCone * 0.5), aimCone * 0.5f)) * npc.player.eyes.HeadForward();
                }

                Effect.server.Run(weapon.attackFX.resourcePath, weapon, StringPool.Get(weapon.handBone), Vector3.zero, Vector3.forward);
                Effect effect = new Effect();
                effect.Init(Effect.Type.Projectile, source, vector32.normalized);
                effect.scale = vector32.magnitude;
                effect.pooledString = component.projectileObject.resourcePath;
                effect.number = UnityEngine.Random.Range(0, 2147483647);
                EffectNetwork.Send(effect);

//                Vector3 dest;
//                if (miss)
//                {
//                    da = 0;
//                    dest = hit;
//                }
//                else
//                {
//                    dest = target.transform.position;
//                }
//                var hitInfo = new HitInfo(npc.player, target, DamageType.Bullet, dmg, dest)
//                {
//                    DidHit = !miss,
//                    HitEntity = target,
//                    PointStart = source,
//                    PointEnd = hit,
//                    HitPositionWorld = dest,
//                    HitNormalWorld = -dir,
//                    WeaponPrefab = GameManager.server.FindPrefab(StringPool.Get(baseProjectile.prefabID)).GetComponent<AttackEntity>(),
//                    Weapon = (AttackEntity)firstWeapon,
//                    HitMaterial = StringPool.Get("Flesh")
//                };
//                target?.OnAttacked(hitInfo);
//                Effect.server.ImpactEffect(hitInfo);
            }

            private void Execute_Move()
            {
                if (!npc.info.canmove) return;
                if (!npc.info.enable) return;

                currPos = Vector3.Lerp(StartPos, EndPos, waypointDone);
                //currPos.y = GetMoveY(currPos); // Adjust for terrain height

                if ((npc.info.hostile || npc.info.ahostile) && npc.target != null)
                {
                    DoAttack();
                }
                else if (attacked && npc.info.locomode == LocoMode.Defend)
                {
                    Defend();
                }
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
                Vector3 newEyesPos = currPos + new Vector3(0, 1.6f, 0);
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
                //if (GetSpeed() <= 0) return;
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
                    BaseMountable mounted = npc.player.GetMounted();
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
                if (npc.info.band == 0)
                {
                    List<BaseChair> chairs = new List<BaseChair>();
                    Vis.Entities(npc.info.loc, 5f, chairs);

                    foreach (BaseChair mountable in chairs.Distinct().ToList())
                    {
                        Instance.DoLog($"[HumanoidMovement] {npc.player.displayName} trying to sit in chair...");
                        if (mountable.IsMounted())
                        {
                            Instance.DoLog($"[HumanoidMovement] Someone is sitting here.");
                            continue;
                        }
                        Instance.DoLog($"[HumanoidMovement] Found an empty chair.");
                        mountable.MountPlayer(npc.player);
                        npc.player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
                        npc.player.eyes.NetworkUpdate(mountable.mountAnchor.transform.rotation);
                        npc.player.ClientRPCPlayer<Vector3>(null, npc.player, "ForcePositionTo", npc.player.transform.position);
                        //mountable.SetFlag(BaseEntity.Flags.Busy, true, false);
                        sitting = true;
                        break;
                    }
                }

                List<StaticInstrument> pidrxy = new List<StaticInstrument>();
                Vis.Entities(npc.info.loc, 2f, pidrxy);
                foreach (StaticInstrument mountable in pidrxy.Distinct().ToList())
                {
                    Instance.DoLog($"[HumanoidMovement] {npc.player.displayName} trying to sit at instrument...");
                    if (mountable.IsMounted())
                    {
                        Instance.DoLog($"[HumanoidMovement] Someone is sitting here.");
                        continue;
                    }
                    mountable.MountPlayer(npc.player);
                    npc.player.OverrideViewAngles(mountable.mountAnchor.transform.rotation.eulerAngles);
                    npc.player.eyes.NetworkUpdate(mountable.mountAnchor.transform.rotation);
                    npc.player.ClientRPCPlayer(null, npc.player, "ForcePositionTo", npc.player.transform.position);
                    //mountable.SetFlag(BaseEntity.Flags.Busy, true, false);
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
                if (npc.info.entrypausetime > 0)
                {
                    paused = true;
                    moving = false;
                    //npc.info.pausetimer = Instance.timer.Once(npc.info.entrypausetime, () => HandlePause(true));
                    Instance.timer.Once(npc.info.entrypausetime * 2, () => HandlePause(true));
                }
            }

            public void Stop(bool pause = false)
            {
                if (pause)
                {
                    HandlePause();
                    npc.player.SignalBroadcast(BaseEntity.Signal.Gesture, "shrug");
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
                if (npc.info.canride == false) return;
                RidableHorse horse = npc.player.GetMountedVehicle() as RidableHorse;
                if (horse == null)
                {
                    // Find a place to sit
                    List<RidableHorse> horses = new List<RidableHorse>();
                    Vis.Entities(npc.info.loc, 15f, horses);
                    foreach (RidableHorse mountable in horses.Distinct().ToList())
                    {
                        if (mountable.GetMounted() != null)
                        {
                            continue;
                        }
                        mountable.AttemptMount(npc.player);
                        npc.player.SetParent(mountable, true, true);
                        riding = true;
                        break;
                    }
                }

                if (horse == null)
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

                if (npc.target != null)
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
                Vector2 randompos = UnityEngine.Random.insideUnitCircle * npc.info.damageDistance;
                if (npc.target != null)
                {
                    if (isVisible)
                    {
                        targetLoc = npc.target.transform.position;
                        rand = false;
                    }
                    else
                    {
                        if (Vector3.Distance(npc.info.loc, targetHorsePos) > 10 && !hasMoved)
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
                    if (Vector3.Distance(npc.player.transform.position, targetHorsePos) > 10 && hasMoved)
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
                RideMove(horse, distance, angle, rand);
            }

            void RideMove(RidableHorse horse, float distance, float angle, bool rand)
            {
                InputMessage message = new InputMessage() { buttons = 0 };
                if (distance > npc.info.damageDistance)
                {
                    message.buttons = 2; // FORWARD
                }
                if (distance > 40 && !rand)
                {
                    message.buttons = 130; // SPRINT FORWARD
                }
                if (horse.currentRunState == BaseRidableAnimal.RunState.sprint && distance < npc.info.maxDistance)
                {
                    message.buttons = 0; // STOP ?
                }
                if (angle > 30 && angle < 180)
                {
                    message.buttons += 8; // LEFT
                }
                if (angle < -30 && angle > -180)
                {
                    message.buttons += 16; // RIGHT
                }

                horse.RiderInput(new InputState() { current = message }, npc.player);
            }

            public void UpdateWaypoints()
            {
                if (string.IsNullOrEmpty(npc.info.waypoint)) return;
                object cwaypoints = Interface.Oxide.CallHook("GetWaypointsList", npc.info.waypoint);
                if (cwaypoints == null) cachedWaypoints = null;
                else
                {
                    cachedWaypoints = new List<WaypointInfo>();
                    Vector3 lastPos = npc.info.loc;
                    float speed = GetSpeed();
                    foreach (object cwaypoint in (List<object>)cwaypoints)
                    {
                        foreach (KeyValuePair<Vector3, float> pair in (Dictionary<Vector3, float>)cwaypoint)
                        {
                            cachedWaypoints.Add(new WaypointInfo(pair.Key, pair.Value));
                        }
                    }

                    if (cachedWaypoints.Count <= 0) cachedWaypoints = null;

                    Instance.DoLog($"[HumanoidMovement] Waypoints: {cachedWaypoints.Count.ToString()} {npc.player.displayName}");
                }
            }

            public List<Item> GetAmmo(Item item)
            {
                List<Item> ammos = new List<Item>();
                AmmoTypes ammoType;
                if (!ammoTypes.TryGetValue(item.info.shortname, out ammoType)) return ammos;
                npc.player.inventory.FindAmmo(ammos, ammoType);
                return ammos;
            }

            private float GetSpeed(float speed = -1)
            {
                if (sitting) speed = 0;
                //if (returning) speed = 7;
                else if (speed == -1) speed = npc.info.speed;
                if (IsSwimming()) speed = speed / 2f;

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
                if (npc.info.locomode == LocoMode.Monument)
                {
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
                        float currdist = Vector3.Distance(monPos[npc.info.monstart], road.Value.points[0]);
                        distance = Math.Min(distance, currdist);
                        //Instance.DoLog($"[HumanoidMovement] {road.Key} distance to {npc.info.monstart} == {currdist.ToString()}");

                        if (currdist <= distance)
                        {
                            roadname = road.Key;
                            //Instance.DoLog($"[HumanoidMovement] {road.Key} is closest");
                        }
                    }
                }
                else
                {
                    roadname = npc.info.roadname;
                }

                //roadname = "Road 5"; // TESTING
                int i = 0;
                foreach (Vector3 point in roads[roadname].points)
                {
                    Instance.DoLog($"point {i}: {point.ToString()}");
                    i++;
                }
                //npc.info.targetloc = roads[roadname].points[0];
                npc.info.loc = roads[roadname].points[0];
                npc.info.roadname = roadname;
                Instance.DoLog($"[HumanoidMovement] Moving {npc.info.displayName} to monument {npc.info.monstart} to walk {npc.info.roadname}");

                //if (npc.info.canride)
                //{
                //    Instance.DoLog($"[HumanoidMovement] {npc.info.displayName} can ride!");
                //    if (!riding)
                //    {
                //        Ride();
                //    }
                //    else
                //    {
                //        var horse = npc.player.GetMountedVehicle() as RidableHorse;
                //        if (horse != null)
                //        {
                //            InputMessage message = new InputMessage() { buttons = 2 };
                //            horse.RiderInput(new InputState() { current = message }, npc.player);
                //            Instance.DoLog($"[HumanoidMovement] {npc.info.displayName} moving horse.");
                //        }
                //    }
                //}
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
                if (swimming)
                {
                    float point = TerrainMeta.WaterMap.GetHeight(position) - 0.65f;
                    float groundY = GetGroundY(position);
                    if (groundY > point)
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
                if (Physics.Raycast(position, Vector3Down, out hitinfo, 100f, groundLayer))
                {
                    return hitinfo.point.y;
                }
                Instance.DoLog($"[HumanoidMovement] GetGroundY: {position.y.ToString()}");
                return position.y - .5f;
            }

            public void FindVictim()
            {
                if (npc.info.hostile)
                {
                    List<BasePlayer> victims = new List<BasePlayer>();
                    Vis.Entities(npc.info.loc, 50f, victims);
                    foreach (BasePlayer pl in victims)
                    {
                        attackEntity = pl as BaseCombatEntity;
                        npc.info.targetloc = pl.transform.position;
                        break;
                    }
                }
                if (npc.info.ahostile)
                {
                    List<BaseAnimalNPC> avictims = new List<BaseAnimalNPC>();
                    Vis.Entities(npc.info.loc, 50f, avictims);
                    foreach (BaseAnimalNPC an in avictims)
                    {
                        attackEntity = an as BaseCombatEntity;
                        npc.info.targetloc = an.transform.position;
                        break;
                    }
                }
            }

            public bool IsLayerBlocked(Vector3 position, float radius, int mask)
            {
                List<Collider> colliders = Pool.GetList<Collider>();
                Vis.Colliders(position, radius, colliders, mask, QueryTriggerInteraction.Collide);

                bool blocked = colliders.Count > 0;

                Pool.FreeList(ref colliders);

                return blocked;
            }

            private bool CanSee()
            {
                BaseProjectile weapon = npc.player.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
                Vector3 pos = npc.info.loc + npc.player.GetOffset();
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
                foreach (BasePlayer player in nearPlayers)
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
                foreach (BaseAnimalNPC player in nearAnimals)
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

                //                if (npc.info.band > 0)
                //                {
                //                    npc.EquipFirstInstrument();
                //                }
                if (npc.info.hostile || npc.info.ahostile || attacked)
                {
                    npc.EquipFirstWeapon();
                }
                //else
                //{
                //    npc.EquipFirstTool();
                //}

                //if (npc.info.locomode.HasFlag(LocoMode.Sit))
                //{
                //    npc.info.cansit = true;
                //    npc.info.canride = false;
                //    npc.info.canmove = true;
                //    Sit();
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Ride))
                //{
                //    npc.info.cansit = false;
                //    npc.info.canride = true;
                //    npc.info.canmove = true;
                //    Ride();
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Follow))
                //{
                //    npc.info.cansit = false;
                //    npc.info.canride = false;
                //    npc.info.canmove = true;
                //    //Follow();
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Stand))
                //{
                //    npc.info.cansit = false;
                //    npc.info.canride = false;
                //    npc.info.canmove = false;
                //    Stand();
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Road))
                //{
                //    npc.info.cansit = false;
                //    npc.info.canride = true;
                //    npc.info.canmove = true;
                //    if (!moving)
                //    {
                //        moving = true;
                //        FindRoad();
                //    }
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Monument))
                //{
                //    npc.info.cansit = false;
                //    npc.info.canride = false;
                //    npc.info.canmove = true;
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Gather))
                //{
                //    npc.info.canmove = true;
                //    npc.info.cansit = false;
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Defend))
                //{
                //    npc.info.canmove = true;
                //    npc.info.cansit = false;
                //}
                //if (npc.info.locomode.HasFlag(LocoMode.Default))
                //{
                //    npc.info.cansit = false;
                //    npc.info.canride = false;
                //    npc.info.canmove = false;
                //}

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
                        npc.info.canride = true;
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
                    case LocoMode.Defend:
                        npc.info.canmove = true;
                        npc.info.cansit = false;
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
                if (info == null) return;
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
                    Vector3 newEyes = info.loc + new Vector3(0, 1.6f, 0);
                    Instance.DoLog($"[HumanoidPlayer]   setting eye position...");
                    player.eyes.position.Set(newEyes.x, newEyes.y, newEyes.z);
                    Instance.DoLog($"[HumanoidPlayer]   ending sleep...");
                    player.EndSleeping();
                    protection.Clear();
                }
                if (movement != null) Destroy(movement);
                Instance.DoLog($"[HumanoidPlayer] Adding player movement to {player.displayName}...");
                movement = player.gameObject.AddComponent<HumanoidMovement>();
                //pathfollower = player.gameObject.AddComponent<PathFollower>();
            }

            public void LookTowards(Vector3 pos, bool wave = false)
            {
                if (pos != info.loc)
                {
                    SetViewAngle(Quaternion.LookRotation(pos - info.loc));
                    if (wave) player.SignalBroadcast(BaseEntity.Signal.Gesture, "wave");
                }
            }

            public void SetViewAngle(Quaternion viewAngles)
            {
                if (viewAngles.eulerAngles == default(Vector3)) return;
                player.viewAngles = viewAngles.eulerAngles;
                info.rot = viewAngles;
                player.SendNetworkUpdate();
            }

            public HeldEntity GetFirstWeapon()
            {
                if (player.inventory?.containerBelt == null) return null;
                foreach (Item item in player.inventory.containerBelt.itemList)
                {
                    if (item.CanBeHeld() && HasAmmo(item) && (item.info.category == ItemCategory.Weapon))
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstTool()
            {
                if (player.inventory?.containerBelt == null) return null;
                foreach (Item item in player.inventory.containerBelt.itemList)
                {
                    if (item.CanBeHeld() && item.info.category == ItemCategory.Tool)
                    {
                        return item.GetHeldEntity() as HeldEntity;
                    }
                }
                return null;
            }

            public HeldEntity GetFirstInstrument()
            {
                if (player.inventory?.containerBelt == null) return null;
                foreach (Item item in player.inventory.containerBelt.itemList)
                {
                    if (item.CanBeHeld() && item.info.category == ItemCategory.Fun)
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
                if (!info.needsammo) return true;
                BaseProjectile weapon = item.GetHeldEntity() as BaseProjectile;
                if (weapon == null) return true;
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
                if (weapon != null)
                {
                    UnequipAll();
                    weapon.SetHeld(true);
                    Instance.Puts($"EquipFirstWeapon: Successfully equipped {weapon.name} for NPC {player.displayName}({player.userID.ToString()})");
                }
                return weapon;
            }

            public HeldEntity EquipFirstTool()
            {
                HeldEntity tool = GetFirstTool();
                if (tool != null)
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
                    Item item = instr.GetItem();
                    SetActive(item.uid);
                    itool = instr as InstrumentTool; // THIS ONE!
                    instrument = instr.ShortPrefabName;
                }
                return instr;
            }

            public void PlayNote(int note, int sharp, int octave, float noteval, float duration = 0.2f)
            {
                if (duration == 0) duration = 0.2f;
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
                Quaternion quaternion = (Quaternion)value;
                writer.WriteValue($"{quaternion.x} {quaternion.y} {quaternion.z} {quaternion.w}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.String)
                {
                    string[] values = reader.Value.ToString().Trim().Split(' ');
                    return new Quaternion(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]), Convert.ToSingle(values[3]));
                }
                JObject o = JObject.Load(reader);
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
                Vector3 vector =(Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.String)
                {
                    string[] values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
                }
                JObject o = JObject.Load(reader);
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
                if (hexColor.StartsWith("#"))
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
        //private unsafe float Q_rsqrt(float number)
        //{
        //    long i;
        //    float x2, y;
        //    const float threehalfs = 1.5f;

        //    x2 = number * 0.5f;
        //    y = number;
        //    i = * (long *) &y;
        //    i = 0x5f3759df - (i >> 1);
        //    y = * (float * ) &i;
        //    y = y * (threehalfs - (x2 * y * y));

        //    return y;
        //}
        //private float InvSqrt(float x)
        //{
        //    float xhalf = 0.5f * x;
        //    int i = BitConverter.ToInt32(BitConverter.GetBytes(x), 0);
        //    i = 0x5f3759df - (i >> 1); // 0x5f375a86 ?
        //    x = BitConverter.ToSingle(BitConverter.GetBytes(i), 0);
        //    x = x * (1.5f - xhalf * x * x);
        //    return x;
        //}
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatIntUnion
        {
            [FieldOffset(0)]
            public float f;

            [FieldOffset(0)]
            public int tmp;
        }
        public static float InvSqrt(float z)
        {
            if (z == 0) return 0;
            FloatIntUnion u;
            u.tmp = 0;
            float xhalf = 0.5f * z;
            u.f = z;
            u.tmp = 0x5f375a86 - (u.tmp >> 1);
            u.f = u.f * (1.5f - xhalf * u.f * u.f);
            return u.f * z;
        }

        private static bool GetBoolValue(string value)
        {
            if (value == null) return false;
            value = value.Trim().ToLower();
            switch (value)
            {
                case "t":
                case "true":
                case "1":
                case "yes":
                case "y":
                case "on":
                    return true;
                case "f":
                case "false":
                case "0":
                case "no":
                case "n":
                case "off":
                default:
                    return false;
            }
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
            if (player.serverInput?.current == null) return false;
            viewAngle = Quaternion.Euler(player.serverInput.current.aimAngles);
            return true;
        }

        public void Teleport(BasePlayer player, Vector3 position)
        {
            if (player.net?.connection != null) player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);

            player.SetParent(null, true, true);
            player.EnsureDismounted();
            player.Teleport(position);
            player.UpdateNetworkGroup();
            player.StartSleeping();
            player.SendNetworkUpdateImmediate(false);

            if (player.net?.connection != null) player.ClientRPCPlayer(null, player, "StartLoading");
        }

//        private void StartSleeping(BasePlayer player)
//        {
//            if (player.IsSleeping()) return;
//            player.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, true);
//            if (!BasePlayer.sleepingPlayerList.Contains(player)) BasePlayer.sleepingPlayerList.Add(player);
//            player.CancelInvoke("InventoryUpdate");
//        }

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

        public static Quaternion StringToQuaternion(string sQuaternion)
        {
            // Remove the parentheses
            if (sQuaternion.StartsWith("(") && sQuaternion.EndsWith(")"))
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

                if (monument.name == "OilrigAI")
                {
                    name = "Small Oilrig";
                    realWidth = 100f;
                }
                else if (monument.name == "OilrigAI2")
                {
                    name = "Large Oilrig";
                    realWidth = 200f;
                }
                else
                {
                    name = Regex.Match(monument.name, @"\w{6}\/(.+\/)(.+)\.(.+)").Groups[2].Value.Replace("_", " ").Replace(" 1", "").Titleize();
                }
                if (monPos.ContainsKey(name)) continue;
                if (cavePos.ContainsKey(name)) name = name + RandomString();

                extents = monument.Bounds.extents;
                if (realWidth > 0f)
                {
                    extents.z = realWidth;
                }

                if (monument.name.Contains("cave"))
                {
                    cavePos.Add(name, monument.transform.position);
                }
                else if (monument.name.Contains("compound") && monPos["outpost"] == null)
                {
                    List<BaseEntity> ents = new List<BaseEntity>();
                    Vis.Entities(monument.transform.position, 50, ents);
                    foreach (BaseEntity entity in ents)
                    {
                        if (entity.PrefabName.Contains("piano"))
                        {
                            monPos.Add("outpost", entity.transform.position + new Vector3(1f, 0.1f, 1f));
                            monSize.Add("outpost", extents);
                        }
                    }
                }
                else if (monument.name.Contains("bandit") && monPos["bandit"] == null)
                {
                    List<BaseEntity> ents = new List<BaseEntity>();
                    Vis.Entities(monument.transform.position, 50, ents);
                    foreach (BaseEntity entity in ents)
                    {
                        if (entity.PrefabName.Contains("workbench"))
                        {
                            monPos.Add("bandit", Vector3.Lerp(monument.transform.position, entity.transform.position, 0.45f) + new Vector3(0, 1.5f, 0));
                            monSize.Add("bandit", extents);
                        }
                    }
                }
                else
                {
                    if (extents.z < 1)
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
            ConfigData config = new ConfigData
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
