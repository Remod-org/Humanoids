# Humanoids
Adds interactive NPCs which can be modded by other plugins.

## Status
 - Includes an integrated GUI for NPC management (no 3rd-party plugin required)
 - NPCs can currently:
  - Walk up and down a chosen road
  - Ride horses
  - Participate in an NPC band using NPCPlay.cs

 - Most plugins that use NPCs such as:
  - GUIShop
  - Quests
  - ServerRewards

## Work in progress
 - Defend and attack

## Future
 - Driving


## Commands
 - /noid -- This is the primary command for all configuration of Humanoids.
  - gui  -- This is the recommended command, i.e. /noid gui

## Permissions
There are no configurable permissions at this time.

## API

  private bool IsHumanoid(BasePlayer player)

  private ulong SpawnHumanoid(Vector3 position, Quaternion currentRot, string name = "noid", bool ephemeral = false, ulong clone = 0)

  private string GetHumanoidName(ulong npcid)

  private bool RemoveHumanoidById(ulong npcid)

  private bool RemoveHumanoidByName(string name)

  private void SetHumanoidInfo(ulong npcid, string toset, string data, string rot = null)

  private void GiveHumanoid(ulong npcid, string itemname, string loc = "wear", ulong skinid = 0, int count = 1)

