// FlatUi.cs - the "Flat blueprint" button on every DSPCalculator window, the job lifecycle (start, progress,
// cancel, result) and the hand-over to the game's blueprint paste mode.
//
// Button: a clone of the window's black-box blueprint button (UICalcWindow.genBpButtonObj, "panel-bg/gen-bp"),
// placed right of it, re-tinted, with its own click handler. Its visibility and vertical position follow the original
// every frame (the calculator moves the original when the window switches between large and small size).
// Progress: shown in the window title while the engine runs; clicking the button again (or closing the window) cancels.
// Paste: the same sequence DSPCalculator uses for its own blueprints (UICalcWindow.GenerateBlackboxBpAndPaste):
// CloseWindow(), WindowsManager.temporaryCloseBecausePasteBp = true (the calculator reopens the window when the paste
// inspector closes), GameMain.mainPlayer.controller.OpenBlueprintPasteMode(bp, GameConfig.blueprintFolder + name).
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace DSPCalculatorFlat
{
    internal class FlatButton
    {
        public object Window;
        public GameObject Source;     // calculator's black-box button
        public GameObject Obj;        // our clone
        public UIButton UiBtn;
        public Text Title;            // window title text (progress display)
        public string SavedTitle;

        public const float OffsetX = 28f;
    }

    internal static class FlatUi
    {
        static readonly List<FlatButton> buttons = new List<FlatButton>();
        static FlatJob job;
        static FlatButton jobButton;

        const string TipTitle = "Flat blueprint";
        const string TipText = "Generate a flat production block for this target (belts, sorters and machines laid out on one level) "
                             + "and enter paste mode. Runs in the background; click again to cancel.";

        // ------------------------------------------------------------------ button creation (UICalcWindow ctor postfix)
        public static void Attach(object window)
        {
            try
            {
                var src = Reflect.TryGet(window, "genBpButtonObj") as GameObject;
                var windowObj = Reflect.TryGet(window, "windowObj") as GameObject;
                if (src == null && windowObj != null)
                {
                    Transform t = windowObj.transform.Find("panel-bg/gen-bp");
                    if (t != null) src = t.gameObject;
                }
                if (src == null) { Plugin.Log.LogWarning("Black-box blueprint button not found; no Flat blueprint button added."); return; }

                GameObject obj = UnityEngine.Object.Instantiate(src, src.transform.parent);
                obj.name = "gen-flat-bp";
                obj.transform.localScale = Vector3.one;
                var rt = obj.GetComponent<RectTransform>();
                var srt = src.GetComponent<RectTransform>();
                if (rt != null && srt != null) rt.anchoredPosition = srt.anchoredPosition + new Vector2(FlatButton.OffsetX, 0);

                var fb = new FlatButton();
                fb.Window = window; fb.Source = src; fb.Obj = obj;
                fb.Title = Reflect.TryGet(window, "titleText") as Text;

                var btn = obj.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(delegate { OnClick(fb); });
                }
                fb.UiBtn = obj.GetComponent<UIButton>();
                if (fb.UiBtn != null)
                {
                    fb.UiBtn.tips.tipTitle = TipTitle;
                    fb.UiBtn.tips.tipText = TipText;
                    fb.UiBtn.tips.corner = 3;
                    fb.UiBtn.tips.width = 270;
                    if (fb.UiBtn.transitions != null)
                        foreach (var tr in fb.UiBtn.transitions)
                        {
                            if (tr == null) continue;
                            tr.normalColor = new Color(0.45f, 0.85f, 0.55f, tr.normalColor.a);
                            tr.mouseoverColor = new Color(0.6f, 1f, 0.7f, tr.mouseoverColor.a);
                            tr.pressedColor = new Color(0.4f, 0.75f, 0.5f, tr.pressedColor.a);
                        }
                }
                var img = obj.GetComponent<Image>();
                if (img != null) img.color = new Color(0.45f, 0.85f, 0.55f, img.color.a);
                buttons.Add(fb);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Adding the Flat blueprint button failed: " + e);
            }
        }

        // ------------------------------------------------------------------ per-window sync (UICalcWindow.OnUpdate postfix)
        public static void Sync(object window)
        {
            for (int i = buttons.Count - 1; i >= 0; i--)
            {
                FlatButton fb = buttons[i];
                if (fb.Obj == null || fb.Source == null) { buttons.RemoveAt(i); continue; }   // window destroyed
                if (!ReferenceEquals(fb.Window, window)) continue;
                bool running = job != null && jobButton == fb;
                bool show = fb.Source.activeSelf || running;
                if (fb.Obj.activeSelf != show) fb.Obj.SetActive(show);
                var rt = fb.Obj.GetComponent<RectTransform>();
                var srt = fb.Source.GetComponent<RectTransform>();
                if (rt != null && srt != null)
                {
                    Vector2 want = srt.anchoredPosition + new Vector2(FlatButton.OffsetX, 0);
                    if ((rt.anchoredPosition - want).sqrMagnitude > 0.01f) rt.anchoredPosition = want;
                }
            }
        }

        // ------------------------------------------------------------------ click
        static void OnClick(FlatButton fb)
        {
            if (job != null)
            {
                if (jobButton == fb) { job.Cancel(); SetTitle(fb, "Flat blueprint: cancelling..."); }
                else Tip("Another flat blueprint is still being generated.");
                return;
            }
            try { Start(fb); }
            catch (MemberMissingException e) { Message("Flat blueprint", "DSPCalculator data could not be read: " + e.Message); }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                Message("Flat blueprint failed", e.GetType().Name + ": " + e.Message);
            }
        }

        static void Start(FlatButton fb)
        {
            string error;
            CalcPlan plan = CalcReader.Read(fb.Window, out error);
            if (plan == null) { Message("Flat blueprint", error); return; }
            Plugin.Log.LogInfo("Flat blueprint plan: " + CalcReader.Describe(plan));

            int budgetMs = Math.Max(5, Plugin.TimeBudgetSeconds.Value) * 1000;
            var errors = new List<string>();
            PreparedRun run = EngineAdapter.Prepare(plan, budgetMs, errors);
            if (run == null) { Message("Flat blueprint failed", Join(errors)); return; }

            job = new FlatJob(fb.Window, run, budgetMs);
            jobButton = fb;
            fb.SavedTitle = fb.Title != null ? fb.Title.text : null;
            SetTitle(fb, "Flat blueprint: starting...");
            if (fb.UiBtn != null) fb.UiBtn.tips.tipText = "Generating... click to cancel.";
            job.Start();
        }

        // ------------------------------------------------------------------ main-thread poll (Plugin.Update)
        public static void Tick()
        {
            if (job == null) return;
            FlatButton fb = jobButton;

            bool gameGone = GameMain.instance == null || GameMain.mainPlayer == null;
            bool windowClosed = fb == null || fb.Obj == null || !WindowOpen(fb.Window);
            if ((gameGone || windowClosed) && !job.CancelRequested) job.Cancel();
            // watchdog: the engine polls cancel; give it the budget plus 30 s before cancelling on its behalf
            if (!job.CancelRequested && job.ElapsedSec * 1000 > job.BudgetMs + 30000) job.Cancel();

            if (!job.Done)
            {
                double p; string s;
                job.Snapshot(out p, out s);
                SetTitle(fb, "Flat blueprint " + (int)(p * 100) + "% (" + (int)job.ElapsedSec + " s): " + s);
                return;
            }

            FlatJob finished = job;
            job = null; jobButton = null;
            RestoreTitle(fb);
            if (fb != null && fb.UiBtn != null) fb.UiBtn.tips.tipText = TipText;
            Finish(finished, fb, gameGone, windowClosed);
        }

        static void Finish(FlatJob j, FlatButton fb, bool gameGone, bool windowClosed)
        {
            PreparedRun run = j.Run;
            foreach (string w in run.Warnings) Plugin.Log.LogWarning("Flat blueprint: " + w);
            if (gameGone) return;
            if (windowClosed) { Tip("Flat blueprint cancelled (the calculator window was closed)."); return; }
            if (!run.Ok)
            {
                foreach (string e in run.Errors) Plugin.Log.LogWarning("Flat blueprint error: " + e);
                Message(j.CancelRequested ? "Flat blueprint cancelled" : "Flat blueprint failed",
                        Join(run.Errors.Count > 0 ? run.Errors : new List<string> { "Cancelled." }));
                return;
            }

            string err;
            BlueprintData bp = run.ToBlueprint(out err);
            if (bp == null) { Message("Flat blueprint failed", err); return; }
            if (!Paste(fb.Window, bp, out err)) { Message("Flat blueprint failed", err); return; }
            string tip = "Flat blueprint: " + run.BuildingCount + " buildings";
            if (run.Warnings.Count > 0) tip += ", " + run.Warnings.Count + " warning(s) in the BepInEx log";
            if (EngineAdapter.UsingStub) tip += " (STUB ENGINE - demo only)";
            Tip(tip);
        }

        // ------------------------------------------------------------------ paste (same sequence as DSPCalculator)
        static bool Paste(object window, BlueprintData bp, out string error)
        {
            error = null;
            if (GameMain.mainPlayer == null || GameMain.mainPlayer.controller == null) { error = "No player to paste with."; return false; }
            try
            {
                Reflect.Call(window, "CloseWindow");
                if (!Reflect.SetStatic("DSPCalculator.UI.WindowsManager", "temporaryCloseBecausePasteBp", true))
                    Plugin.Log.LogWarning("WindowsManager.temporaryCloseBecausePasteBp not found: the calculator window will not reopen after paste.");
                GameMain.mainPlayer.controller.OpenBlueprintPasteMode(bp, GameConfig.blueprintFolder + "DSPCalcFlatTemp.txt", true);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError(e);
                error = "Entering paste mode failed: " + e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ helpers
        static bool WindowOpen(object window)
        {
            var obj = Reflect.TryGet(window, "windowObj") as GameObject;
            return obj != null && obj.activeSelf;
        }

        static void SetTitle(FlatButton fb, string text)
        {
            if (fb == null || fb.Title == null) return;
            fb.Title.text = text;
        }

        static void RestoreTitle(FlatButton fb)
        {
            if (fb == null || fb.Title == null || fb.SavedTitle == null) return;
            fb.Title.text = fb.SavedTitle;
            fb.SavedTitle = null;
        }

        static string Join(List<string> lines)
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (string l in lines)
            {
                if (shown == 12) { sb.Append("\n... ").Append(lines.Count - shown).Append(" more in the BepInEx log"); break; }
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(l);
                shown++;
            }
            return sb.ToString();
        }

        internal static void Message(string title, string text)
        {
            UIMessageBox.Show(title, text ?? "", "OK", 1, new UIMessageBox.Response(delegate { }));
        }

        static void Tip(string text)
        {
            UIRealtimeTip.Popup(text, false, 0);
        }
    }
}
