// FlatJob.cs - one engine run on a background thread; the main thread polls its state every frame.
using System;
using System.Threading;

namespace DSPCalculatorFlat
{
    internal class FlatJob
    {
        public readonly object Window;          // DSPCalculator UICalcWindow that started the job
        public readonly PreparedRun Run;
        public readonly DateTime Started = DateTime.UtcNow;
        public readonly int BudgetMs;

        readonly object gate = new object();
        double progress;
        string status = "starting";
        volatile bool done;
        volatile bool cancel;
        Thread thread;

        public FlatJob(object window, PreparedRun run, int budgetMs) { Window = window; Run = run; BudgetMs = budgetMs; }

        public bool Done { get { return done; } }
        public bool CancelRequested { get { return cancel; } }
        public void Cancel() { cancel = true; }
        public double ElapsedSec { get { return (DateTime.UtcNow - Started).TotalSeconds; } }

        public void Snapshot(out double p, out string s)
        {
            lock (gate) { p = progress; s = status; }
        }

        public void Start()
        {
            // Large stack: the layout search may recurse deeply.
            thread = new Thread(Body, 64 * 1024 * 1024);
            thread.IsBackground = true;
            thread.Name = "DSPCalculatorFlat engine";
            thread.Start();
        }

        void Body()
        {
            try
            {
                Run.Run(OnProgress, () => cancel);
            }
            catch (Exception e)
            {
                Run.Errors.Add("Worker exception: " + e.Message);
                Plugin.Log.LogError(e);
            }
            finally { done = true; }
        }

        void OnProgress(double p, string s)
        {
            lock (gate)
            {
                if (!double.IsNaN(p)) progress = Math.Max(0, Math.Min(1, p));
                if (s != null) status = s;
            }
        }
    }
}
