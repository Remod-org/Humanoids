## Status

This is currently an experimental plugin whose development has largely ceased (for now).

See https://www.remod.org/remod-free-plugins/12-humanoids.html for a better README.

Requires RoadFinder (Remod)
Uses Kits, GUIShop, NPCShop

The overall goal is to provide a general purpose NPC plugin with perhaps more features than previous plugins.  Facepunch has added so many new NPCs over the last few years, but there are still a few gaps to be filled.

Includes an integrated GUI for NPC management (no 3rd-party plugin required):

NPC Edit

This includes submenus for kit selection:

and locomotion type:

Note that with regard to locomotion, if attacked, the NPC will switch its locomotion to Defend and then ultimately revert to its original setting.

NPCs can currently:

    Work with most plugins that use NPCs such as GUIShop, Quests, and ServerRewards.



    They can also...
    Defend and return fire when attacked
    Walk up and down a chosen road
    Sit and stand
    Stop and wave

Work in Progress

    Following (when attacked, etc.)
    General roaming around the map whether to antagonize, help, hunt, etc.
    Gathering and looting
    Following multiple roads from one monument to another, etc.
    Riding horses
    Hostility against players
    Hostility against animals
    Monument groups

Future

    Driving ?
    Flying ?

Commands

    /noid -- This is the primary command for all configuration of Humanoids.
        - gui -- This is the recommended command, i.e. /noid gui
        - list -- List the current humanoids
        - show -- Draws the current location of all humanoids for 30 seconds
        - new -- Spawn a new humanoid
        - edit {ID} -- Edit the humanoid you are looking at or optionally by passing the ID
        - delete {ID} -- Delete the humanoid you are looking at or optionally by passing the ID
        ... Many other options, most of which are best utilized via the GUI

### Permissions

There are no configurable permissions at this time.  All configuration requires admin level access on your server.

### Configuration

```json
{
  "Options": {
    "Default Name": "Noid",
    "Default Health": 50.0,
    "Default Respawn Timer": 30.0,
    "Move NPCs to 0,0,0 on server wipe": false,
    "Move Shop NPCs to town when town set": false,
    "Prefab to check for Shop NPCs at town": "wall.window",
    "Close GUI on NPC spawn here": true,
    "debug": true
  },
  "Version": {
    "Major": 1,
    "Minor": 2,
    "Patch": 5
  }
}
```

If Teleportication is installed and either GUIShop or NPCShop are installed, you can have shop NPCs relocated to town when /town set is run:

    - Set "Move Shop NPCs to town when town set" true
    - Set "Prefab to check for Shop NPCs at town" to a common entity name where NPCs should be located (required, default "wall.window")
    - For each NPC associated with a shop, set shopnpc to true (using the GUI or by editing the data/Humanoids/humanoids.json file and reloading.
    - Run /town set and watch the NPCs get put into their new locations.

### API

```cs
private bool IsHumanoid(BasePlayer player)

private ulong SpawnHumanoid(Vector3 position, Quaternion currentRot, string name = "noid", bool ephemeral = false, ulong clone = 0)

private string GetHumanoidName(ulong npcid)

private bool RemoveHumanoidById(ulong npcid)

private bool RemoveHumanoidByName(string name)

private void SetHumanoidInfo(ulong npcid, string toset, string data, string rot = null)

private void GiveHumanoid(ulong npcid, string itemname, string loc = "wear", ulong skinid = 0, int count = 1)
```
