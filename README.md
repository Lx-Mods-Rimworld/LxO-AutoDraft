# LxO - Garrison

Automatically drafts soldiers when threats appear and undrafts them when the danger is over. Non-combatants flee to safety.

## How It Works

1. Raid/manhunter/hostile appears on the map
2. All colonists meeting the minimum combat skill are instantly drafted
3. Non-combatants (pacifists, low-skill) sprint to the safest roofed area
4. You take manual control of your drafted soldiers
5. When all threats are eliminated, soldiers auto-undraft after a configurable delay

## Features

- **Instant response** -- checks every second for hostile threats
- **Skill-based soldier detection** -- minimum shooting or melee skill (default: 4)
- **Auto-undraft** -- soldiers stand down when threats cleared (configurable delay)
- **Non-combatant flee** -- pacifists and non-soldiers run to safety
- **Player override respected** -- if you manually undraft a soldier during combat, they stay undrafted
- **Per-pawn exclude** -- mark specific pawns to never auto-draft
- **Alerts** -- notification when soldiers draft and stand down

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Enable | ON | Master toggle |
| Auto-undraft | ON | Undraft when threats cleared |
| Non-combatant flee | ON | Pacifists flee to safety |
| Show alerts | ON | Draft/undraft notifications |
| Min combat skill | 4 | Minimum shooting or melee to be a soldier |
| Undraft delay | ~8 sec | Wait after last threat before undrafting |

## Works With

- **LxO - Smart Gear** -- auto-equips combat gear (but respects manual control while drafted)
- **LxO - Learn to Survive** -- Combat Instinct improves fighting behavior
- **LxO - Smart Allow** -- auto-allows dropped loot after combat

Complete combat pipeline: threat -> draft -> fight (manual control) -> undraft -> re-equip -> allow loot.

## Compatibility

- Works with any mod
- Safe to add or remove mid-save
- No conflicts

## Requirements

- RimWorld 1.6+
- [Harmony](https://steamcommunity.com/workshop/filedetails/?id=2009463077)

## Languages

English, German, Chinese Simplified, Japanese, Korean, Russian, Spanish

## Credits

Developed by **Lexxers** ([Lx-Mods-Rimworld](https://github.com/Lx-Mods-Rimworld))

Free forever. Donations: **[Ko-fi](https://ko-fi.com/lexxers)**

## License

- **Code:** MIT License
- **Content:** CC-BY-SA 4.0
