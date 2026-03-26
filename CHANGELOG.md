# Changelog

All notable changes to this mod will be documented in this file.

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
