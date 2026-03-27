using RimWorld;
using UnityEngine;
using Verse;

namespace AutoDraft
{
    // ==================== SETTINGS ====================

    public enum DownedHandling
    {
        Kill,
        StripThenKill,
        Capture,
        StripThenCapture
    }

    public class AutoDraftSettings : ModSettings
    {
        public static bool enabled = true;
        public static bool autoUndraft = true;
        public static bool fleeNonCombatants = true;
        public static bool showAlert = true;
        public static int undraftDelay = 500;
        public static DownedHandling downedHandling = DownedHandling.StripThenCapture;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref autoUndraft, "autoUndraft", true);
            Scribe_Values.Look(ref fleeNonCombatants, "fleeNonCombatants", true);
            Scribe_Values.Look(ref showAlert, "showAlert", true);
            Scribe_Values.Look(ref undraftDelay, "undraftDelay", 500);
            Scribe_Values.Look(ref downedHandling, "downedHandling", DownedHandling.StripThenCapture);
            base.ExposeData();
        }

        public static void DrawSettings(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);

            l.CheckboxLabeled("AD_Enabled".Translate(), ref enabled);
            if (!enabled) { l.End(); return; }

            l.GapLine();
            l.CheckboxLabeled("AD_AutoUndraft".Translate(), ref autoUndraft,
                "AD_AutoUndraft_Desc".Translate());
            l.CheckboxLabeled("AD_FleeNonCombatants".Translate(), ref fleeNonCombatants,
                "AD_FleeNonCombatants_Desc".Translate());
            l.CheckboxLabeled("AD_ShowAlert".Translate(), ref showAlert);

            if (autoUndraft)
            {
                l.Label("AD_UndraftDelay".Translate() + ": " + (undraftDelay / 60f).ToString("F1") + "s");
                undraftDelay = (int)l.Slider(undraftDelay, 60, 3000);
            }

            l.GapLine();
            l.Label("AD_DownedHandling".Translate());
            if (l.RadioButton("AD_Downed_Kill".Translate(), downedHandling == DownedHandling.Kill))
                downedHandling = DownedHandling.Kill;
            if (l.RadioButton("AD_Downed_StripKill".Translate(), downedHandling == DownedHandling.StripThenKill))
                downedHandling = DownedHandling.StripThenKill;
            if (l.RadioButton("AD_Downed_Capture".Translate(), downedHandling == DownedHandling.Capture))
                downedHandling = DownedHandling.Capture;
            if (l.RadioButton("AD_Downed_StripCapture".Translate(), downedHandling == DownedHandling.StripThenCapture))
                downedHandling = DownedHandling.StripThenCapture;

            l.GapLine();
            l.Label("AD_AssignHint".Translate());

            l.End();
        }
    }

    public class AutoDraftMod : Mod
    {
        public static AutoDraftSettings settings;

        public AutoDraftMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<AutoDraftSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            AutoDraftSettings.DrawSettings(inRect);
        }

        public override string SettingsCategory() => "LxO - Garrison";
    }
}
