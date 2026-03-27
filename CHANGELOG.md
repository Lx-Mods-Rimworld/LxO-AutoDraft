# Changelog

All notable changes to this mod will be documented in this file.

## [2.0.0] - 2026-03-27 -- Smart Soldiers (Combat AI Rework)

**Early release -- rework begun, many more tests needed. First look is promising. Please report any issues!**

### Major: Combat AI Overhaul
- Complete rewrite of soldier combat logic into modular architecture (ThreatTracker, TargetSelector, PositionEvaluator, SquadCoordination, WeaponTactics)
- Combat intelligence scales with CombatInstinct level from Learn to Survive. If LTS not installed, all soldiers act at max level.
- Smart target selection: soldiers prioritize enemies targeting them, high-DPS threats, sappers, and doomsday launchers
- Cover seeking: soldiers find positions with cover near their post (Level 3+)
- Squad coordination: focus fire on the most dangerous enemy (Level 5+)
- Auto-retreat when badly wounded (Level 9+)
- Works independently from Smart Gear -- if Smart Gear is installed, weapon management is deferred to it

### Melee Soldiers: Bodyguard Role
- Melee soldiers no longer charge across the map into friendly fire
- New role: bodyguard/interceptor -- they hold at the ranged line and intercept enemies that breach within 8 tiles
- Only engage when enemy is close enough to threaten ranged soldiers
- Hunt fleeing enemies only when no active threats remain

### Ranged Soldiers
- Position at 80% of weapon range (not the edge where any movement causes cancel loops)
- Optimal range adapts per weapon: short bow holds at 15, sniper rifle at 30
- Smart weapon switching: melee soldiers with ranged sidearm shoot approaching enemies, swap to melee when close

### Fixes
- Single small manhunter animals no longer mobilize entire garrison
- Sleeping soldiers are woken up during raids
- Off-post soldiers doing recreation/eating return to post during combat
- Rescue and tending are no longer interrupted by combat reassignment
- Range precision fix: attacks no longer cancelled at exact weapon range boundary
- Melee AttackMelee no longer cancelled while pawn is closing distance

## [1.1.0] - 2026-03-26

### Features
- Kidnap rescue: soldiers detect when a colonist is being carried away and pursue the kidnapper across the entire map
- Expanded threat detection: soldiers now respond to ranged attacks and any enemy targeting a colonist, not just melee
- Soldiers' hostile response is automatically set to Attack (no more fleeing soldiers)

### Fixes
- Fixed soldiers stuck in useless AttackStatic on enemies that moved out of weapon range. Soldiers now cancel the attack and reassign properly.
- Fixed soldiers doing construction (FinishFrame) at their post during raids instead of guarding
- Fixed soldiers not finishing off downed enemies after save/load
- Fixed soldiers fighting over the same downed enemy (strip+kill loop)
- Fixed strip-then-kill never completing: now handled as a single job with sequential steps
- Fixed downed enemies needing multiple melee hits: now uses instant execution
- Fixed soldiers fleeing instead of fighting (FleeAndCower blocked for active soldiers)
- Fixed melee with rifle when enemy is point blank
- Fixed collection crash during pawn iteration
- Fixed errors when pawns die: soldier comp no longer runs on corpses

### Improvements
- All debug logging conditional on LxDebug mod. Zero performance overhead for normal users.
- Soldiers kite ranged enemies: move to optimal firing distance instead of standing still
- Mutual aid: soldiers rush to help squadmates under attack within 30 tiles

## [1.0.0] - 2026-03-26

### Features
- Mark pawns as Soldiers with a toggle button, assign Combat Posts on the map
- Soldiers automatically sprint to their post when threats appear
- Attack enemies in weapon range from post (no drafting needed)
- When all enemies are downed, soldiers leave post and hunt them down
- Configurable downed enemy handling: Kill, Strip + Kill, Capture, Strip + Capture
- Hostile animals automatically killed for meat and leather
- Pacifists auto-flee to safest roofed area in home zone
- Configurable stand-down delay after threats clear
- Soldiers return to normal work automatically when combat is over
- 7 languages: English, German, Chinese Simplified, Japanese, Korean, Russian, Spanish
