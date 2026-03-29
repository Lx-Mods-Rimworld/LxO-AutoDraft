# Changelog

All notable changes to this mod will be documented in this file.

## [2.0.2] - 2026-03-29

### Fixes
- Strip and Kill now works: replaced broken PrisonerExecution/Slaughter queue (required prisoner status or designation that hostile downed pawns don't have) with direct ExecutionCut kill.
- EnforcePosts no longer interrupts StripThenKill/StripThenCapture jobs. Soldiers finish the full strip+execute sequence without being pulled back to post.
- Downed animals now use StripThenKill for consistent direct execution instead of AttackMelee, fixing 1.6 crawling interactions.

## [2.0.1] - 2026-03-27

### Fixes
- Dormant mechanoids (ancient danger, mech clusters) no longer keep garrison permanently mobilized. Uses RimWorld's ThreatDisabled API to properly identify inactive threats.
- Downed enemies are now correctly detected for strip/kill/capture even with the dormant filter active.
- Soldiers properly stand down after all active threats are eliminated. Added safety timeout for edge cases.
- Fleeing enemies walking to map edge no longer keep soldiers at posts forever.
- Soldiers now finish off downed enemies during active combat, not only after all standing threats are gone.
- Fixed soldiers not activating after save/load mid-combat.
- Fixed Collection modified crash from removing .ToList() too aggressively. Restored on state-modifying loops.
- Execution damage now uses 999 armor penetration (was 0, could be deflected by heavy armor).
- Replaced Traverse reflection with zero-cost Harmony field injection in flee blocking patch.
- Soldiers no longer get interrupted mid-aiming. Verb warmup state is checked before reassignment.
- FindRetreatPosition uses GenRadial instead of brute-force grid search.
- FindSafeSpot limits expensive CanReach calls to 30 max.
- Soldier gizmo no longer shows on mechanoids or animals. (reported by @Cutie Scavenger)
- Faction.OfPlayer null-safe in ThreatTracker and CompSoldier.

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
